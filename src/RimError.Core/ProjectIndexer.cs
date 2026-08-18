using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RimError.Core;

public sealed class ProjectIndexer
{
    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+(?<name>[A-Za-z_][\w.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TypePattern = new(
        @"\b(?<kind>class|struct|interface|record)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MethodPattern = new(
        @"(?<name>[A-Za-z_]\w*)\s*\((?<parameters>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public async ValueTask<ProjectIndex> BuildOrLoadAsync(
        string rootPath,
        string? cachePath = null,
        ProjectIndexOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var validatedOptions = options ?? new ProjectIndexOptions();
        validatedOptions.Validate();
        var root = ResolveRoot(rootPath);
        var cache = Path.GetFullPath(
            cachePath ?? Path.Combine(root, ".rimerror", "index.json"));
        var files = await DiscoverFilesAsync(root, validatedOptions, cancellationToken)
            .ConfigureAwait(false);

        var cached = await TryReadCacheAsync(cache, cancellationToken).ConfigureAwait(false);
        if (cached is not null &&
            cached.SchemaVersion == ProjectIndex.CurrentSchemaVersion &&
            PathsEqual(cached.RootPath, root) &&
            SameManifest(cached.Files, files))
        {
            return cached;
        }

        var index = await BuildIndexAsync(
                root,
                files,
                validatedOptions,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteCacheAsync(cache, index, cancellationToken).ConfigureAwait(false);
        return index;
    }

    public ValueTask<ProjectIndex> BuildAsync(
        string rootPath,
        ProjectIndexOptions? options = null,
        CancellationToken cancellationToken = default) =>
        BuildOrLoadAsync(rootPath, cachePath: null, options, cancellationToken);

    private static async Task<ProjectIndex> BuildIndexAsync(
        string root,
        ProjectIndexFile[] files,
        ProjectIndexOptions options,
        CancellationToken cancellationToken)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length > options.MaxIndexedFileBytes)
            {
                continue;
            }

            var absolutePath = Path.Combine(
                root,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                contents[file.RelativePath] = await File.ReadAllTextAsync(
                        absolutePath,
                        Encoding.UTF8,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // A source tree can change while it is being indexed. The next
                // cache validation will retry the file.
            }
            catch (UnauthorizedAccessException)
            {
                // An unreadable source file is simply unavailable for attribution.
            }
            catch (IOException)
            {
                // Keep the optional index useful for the readable remainder.
            }
        }

        var projects = ParseProjects(root, files, contents);
        var symbols = new List<ProjectIndexSymbol>();
        var definitions = new List<ProjectIndexDefinition>();
        var xmlFiles = new List<(ProjectIndexFile File, string Text)>();

        foreach (var file in files)
        {
            if (!contents.TryGetValue(file.RelativePath, out var text))
            {
                continue;
            }

            var project = FindOwningProject(root, file.RelativePath, projects);
            if (file.Kind == "cs")
            {
                symbols.AddRange(ParseCSharp(
                    file.RelativePath,
                    text,
                    project));
            }
            else if (file.Kind == "xml")
            {
                xmlFiles.Add((file, text));
            }
        }

        foreach (var (file, text) in xmlFiles)
        {
            var project = FindOwningProject(root, file.RelativePath, projects);
            definitions.AddRange(ParseDefinitions(
                file.RelativePath,
                text,
                project));
        }

        var references = ParseReferences(root, xmlFiles, definitions, projects);
        return new ProjectIndex
        {
            RootPath = root,
            Files = files,
            Projects = projects,
            Symbols = symbols
                .OrderBy(symbol => symbol.File, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
                .ToArray(),
            Definitions = definitions
                .OrderBy(definition => definition.Name, StringComparer.Ordinal)
                .ThenBy(definition => definition.Type, StringComparer.Ordinal)
                .ThenBy(definition => definition.File, StringComparer.Ordinal)
                .ToArray(),
            References = references
                .OrderBy(reference => reference.Name, StringComparer.Ordinal)
                .ThenBy(reference => reference.File, StringComparer.Ordinal)
                .ThenBy(reference => reference.Line)
                .ToArray()
        };
    }

    private static async Task<ProjectIndexFile[]> DiscoverFilesAsync(
        string root,
        ProjectIndexOptions options,
        CancellationToken cancellationToken)
    {
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => IsSupportedExtension(path))
            .Where(path => !IsExcludedPath(root, path))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
        var files = new List<ProjectIndexFile>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(root, path);
            try
            {
                var info = new FileInfo(path);
                var hash = info.Length <= options.MaxIndexedFileBytes
                    ? await HashFileAsync(path, cancellationToken).ConfigureAwait(false)
                    : null;
                files.Add(new ProjectIndexFile
                {
                    RelativePath = relative,
                    Kind = GetKind(path),
                    Length = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    ContentHash = hash
                });
            }
            catch (FileNotFoundException)
            {
                // The file disappeared during enumeration.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible source cannot contribute symbols.
            }
            catch (IOException)
            {
                // Continue indexing readable files.
            }
        }

        return files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<ProjectIndex?> TryReadCacheAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<ProjectIndex>(
                    stream,
                    CacheJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(
        string cachePath,
        ProjectIndex index,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The project index cache path has no directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = cachePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        index,
                        CacheJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static ProjectIndexProject[] ParseProjects(
        string root,
        IEnumerable<ProjectIndexFile> files,
        IReadOnlyDictionary<string, string> contents)
    {
        return files
            .Where(file => file.Kind == "csproj")
            .Select(file =>
            {
                var name = Path.GetFileNameWithoutExtension(file.RelativePath);
                contents.TryGetValue(file.RelativePath, out var text);
                var properties = ReadProjectProperties(text);
                return new ProjectIndexProject
                {
                    RelativePath = file.RelativePath,
                    Name = name,
                    AssemblyName = FirstValue(properties, "AssemblyName") ?? name,
                    RootNamespace = FirstValue(properties, "RootNamespace"),
                    PackageId = FirstValue(properties, "PackageId")
                };
            })
            .OrderBy(project => project.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string> ReadProjectProperties(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        try
        {
            var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            foreach (var property in document.Descendants().Where(element =>
                         element.Parent?.Name.LocalName.Equals(
                             "PropertyGroup",
                             StringComparison.OrdinalIgnoreCase) == true))
            {
                var value = property.Value.Trim();
                if (value.Length > 0 && !result.ContainsKey(property.Name.LocalName))
                {
                    result[property.Name.LocalName] = value;
                }
            }
        }
        catch (XmlException)
        {
            // A malformed project still gets a filename-derived project name.
        }

        return result;
    }

    private static string? FirstValue(
        IReadOnlyDictionary<string, string> properties,
        string key) =>
        properties.TryGetValue(key, out var value) ? value : null;

    private static ProjectIndexSymbol[] ParseCSharp(
        string relativePath,
        string text,
        ProjectIndexProject? project)
    {
        var symbols = new List<ProjectIndexSymbol>();
        var typeScopes = new List<TypeScope>();
        string? namespaceName = null;
        TypeScope? pendingType = null;
        var depth = 0;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];
            while (typeScopes.Count > 0 && depth < typeScopes[^1].BodyDepth)
            {
                typeScopes.RemoveAt(typeScopes.Count - 1);
            }

            var namespaceMatch = NamespacePattern.Match(line);
            if (namespaceMatch.Success)
            {
                namespaceName = namespaceMatch.Groups["name"].Value;
            }

            var typeMatch = TypePattern.Match(line);
            if (typeMatch.Success)
            {
                var shortName = typeMatch.Groups["name"].Value;
                var parent = typeScopes.Count == 0 ? null : typeScopes[^1].FullName;
                var fullName = parent is null
                    ? Qualify(namespaceName, shortName)
                    : $"{parent}.{shortName}";
                var scope = new TypeScope(fullName, shortName, lineNumber, 0);
                symbols.Add(CreateSymbol(
                    "type",
                    fullName,
                    fullName,
                    null,
                    null,
                    relativePath,
                    lineNumber,
                    project));

                var openingBraces = CountCharacter(line, '{');
                if (openingBraces > 0)
                {
                    scope = scope with { BodyDepth = depth + openingBraces };
                    typeScopes.Add(scope);
                }
                else
                {
                    pendingType = scope;
                }
            }
            else if (pendingType is not null && line.Contains('{'))
            {
                typeScopes.Add(pendingType with { BodyDepth = depth + 1 });
                pendingType = null;
            }

            if (typeScopes.Count > 0)
            {
                var methodMatch = MethodPattern.Match(line);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups["name"].Value;
                    if (!IsControlWord(methodName) &&
                        LooksLikeMethodDeclaration(line, methodMatch, typeScopes[^1].ShortName))
                    {
                        var type = typeScopes[^1];
                        var fullName = $"{type.FullName}.{methodName}";
                        symbols.Add(CreateSymbol(
                            "method",
                            fullName,
                            type.FullName,
                            methodName,
                            CountParameters(methodMatch.Groups["parameters"].Value),
                            relativePath,
                            lineNumber,
                            project));
                    }
                }
            }

            depth += CountCharacter(line, '{') - CountCharacter(line, '}');
            if (depth < 0)
            {
                depth = 0;
            }
        }

        return symbols.ToArray();
    }

    private static bool LooksLikeMethodDeclaration(
        string line,
        Match match,
        string typeName)
    {
        var name = match.Groups["name"].Value;
        if (name.Equals(typeName, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = line[..match.Index].Trim();
        if (prefix.Length == 0)
        {
            return false;
        }

        return prefix.Contains(
                   "public ",
                   StringComparison.Ordinal) ||
               prefix.Contains("private ", StringComparison.Ordinal) ||
               prefix.Contains("protected ", StringComparison.Ordinal) ||
               prefix.Contains("internal ", StringComparison.Ordinal) ||
               prefix.Contains("static ", StringComparison.Ordinal) ||
               prefix.Contains("override ", StringComparison.Ordinal) ||
               prefix.Contains("virtual ", StringComparison.Ordinal) ||
               prefix.Contains("async ", StringComparison.Ordinal) ||
               prefix.Contains("abstract ", StringComparison.Ordinal) ||
               prefix.Contains("extern ", StringComparison.Ordinal);
    }

    private static ProjectIndexSymbol CreateSymbol(
        string kind,
        string name,
        string typeName,
        string? methodName,
        int? parameterCount,
        string file,
        int? line,
        ProjectIndexProject? project) =>
        new()
        {
            Kind = kind,
            Name = name,
            TypeName = typeName,
            MethodName = methodName,
            ParameterCount = parameterCount,
            File = file,
            Line = line,
            Project = project?.RelativePath,
            AssemblyName = project?.AssemblyName
        };

    private static ProjectIndexDefinition[] ParseDefinitions(
        string relativePath,
        string text,
        ProjectIndexProject? project)
    {
        try
        {
            var document = XDocument.Parse(
                text,
                LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var definitions = new List<ProjectIndexDefinition>();
            foreach (var element in document.Descendants())
            {
                var type = element.Name.LocalName;
                if (!IsDefinitionElement(type))
                {
                    continue;
                }

                var nameElement = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName.Equals(
                        "defName",
                        StringComparison.OrdinalIgnoreCase));
                var name = nameElement?.Value.Trim() ??
                    element.Attributes().FirstOrDefault(attribute =>
                        attribute.Name.LocalName.Equals(
                            "defName",
                            StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (!IsIdentifier(name))
                {
                    continue;
                }

                definitions.Add(new ProjectIndexDefinition
                {
                    Type = type,
                    Name = name!,
                    File = relativePath,
                    Line = GetLine(element),
                    Project = project?.RelativePath,
                    AssemblyName = project?.AssemblyName
                });
            }

            return definitions.ToArray();
        }
        catch (XmlException)
        {
            return [];
        }
    }

    private static ProjectIndexReference[] ParseReferences(
        string root,
        IReadOnlyList<(ProjectIndexFile File, string Text)> xmlFiles,
        IReadOnlyList<ProjectIndexDefinition> definitions,
        IReadOnlyList<ProjectIndexProject> projects)
    {
        var knownNames = definitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var references = new Dictionary<string, ProjectIndexReference>(
            StringComparer.Ordinal);

        foreach (var (file, text) in xmlFiles)
        {
            XDocument? document;
            try
            {
                document = XDocument.Parse(
                    text,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                continue;
            }

            var project = FindOwningProject(root, file.RelativePath, projects);
            foreach (var element in document.Descendants())
            {
                var isLeaf = !element.Elements().Any();
                if (!isLeaf)
                {
                    continue;
                }

                var value = element.Value.Trim();
                if (!IsIdentifier(value) ||
                    IsDefinitionNameElement(element) &&
                    element.Parent is not null &&
                    IsDefinitionElement(element.Parent.Name.LocalName))
                {
                    continue;
                }

                var localName = element.Name.LocalName;
                if (!IsReferenceValue(localName, value, knownNames))
                {
                    continue;
                }

                AddReference(
                    references,
                    value,
                    InferDefType(localName),
                    file.RelativePath,
                    GetLine(element),
                    project);
            }

            foreach (var attribute in document.Descendants().Attributes())
            {
                var value = attribute.Value.Trim();
                if (!IsIdentifier(value) ||
                    !IsReferenceValue(attribute.Name.LocalName, value, knownNames))
                {
                    continue;
                }

                AddReference(
                    references,
                    value,
                    InferDefType(attribute.Name.LocalName),
                    file.RelativePath,
                    GetLine(attribute),
                    project);
            }
        }

        return references.Values.ToArray();
    }

    private static void AddReference(
        IDictionary<string, ProjectIndexReference> references,
        string name,
        string? type,
        string file,
        int? line,
        ProjectIndexProject? project)
    {
        var key = $"{name}\u001f{file}\u001f{line}";
        references.TryAdd(
            key,
            new ProjectIndexReference
            {
                Name = name,
                Type = type,
                File = file,
                Line = line,
                Project = project?.RelativePath,
                AssemblyName = project?.AssemblyName
            });
    }

    private static bool IsReferenceValue(
        string elementName,
        string value,
        ISet<string> knownNames)
    {
        if (knownNames.Contains(value))
        {
            return true;
        }

        var lower = elementName.ToLowerInvariant();
        return lower.Contains("def", StringComparison.Ordinal) ||
               lower.Contains("parent", StringComparison.Ordinal) ||
               lower.Contains("reference", StringComparison.Ordinal) ||
               lower.Contains("name", StringComparison.Ordinal) &&
               (value.Contains('_') || value.Contains('.'));
    }

    private static bool IsDefinitionNameElement(XElement element) =>
        element.Name.LocalName.Equals("defName", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefinitionElement(string name) =>
        name.EndsWith("Def", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("defName", StringComparison.OrdinalIgnoreCase);

    private static string? InferDefType(string elementName)
    {
        if (elementName.Equals("defName", StringComparison.OrdinalIgnoreCase) ||
            elementName.Contains("parent", StringComparison.OrdinalIgnoreCase) ||
            elementName.Contains("reference", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (elementName.EndsWith("Def", StringComparison.OrdinalIgnoreCase))
        {
            return char.ToUpperInvariant(elementName[0]) + elementName[1..];
        }

        return null;
    }

    private static int? GetLine(XObject node)
    {
        var lineInfo = node as IXmlLineInfo;
        return lineInfo?.HasLineInfo() == true && lineInfo.LineNumber > 0
            ? lineInfo.LineNumber
            : null;
    }

    private static ProjectIndexProject? FindOwningProject(
        string root,
        string relativePath,
        IReadOnlyList<ProjectIndexProject> projects)
    {
        var absolutePath = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return projects
            .Where(project =>
            {
                var projectPath = Path.Combine(
                    root,
                    project.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(projectPath);
                return directory is not null && IsUnder(directory, absolutePath);
            })
            .OrderByDescending(project => project.RelativePath.Length)
            .ThenBy(project => project.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string Qualify(string? namespaceName, string name) =>
        string.IsNullOrWhiteSpace(namespaceName) ? name : $"{namespaceName}.{name}";

    private static int CountParameters(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return 0;
        }

        var angleDepth = 0;
        var count = 1;
        foreach (var character in parameters)
        {
            switch (character)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case ',' when angleDepth == 0:
                    count++;
                    break;
            }
        }

        return count;
    }

    private static bool IsControlWord(string name) =>
        name is "if" or "for" or "foreach" or "while" or "switch" or
        "catch" or "using" or "lock" or "nameof" or "return";

    private static int CountCharacter(string value, char character) =>
        value.Count(candidate => candidate == character);

    private static bool IsIdentifier(string? value) =>
        value is not null &&
        value.Length <= 256 &&
        IdentifierPattern.IsMatch(value);

    private static string ResolveRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A project or source root is required.", nameof(rootPath));
        }

        var fullPath = Path.GetFullPath(rootPath);
        if (File.Exists(fullPath) &&
            Path.GetExtension(fullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(fullPath)!;
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Project source root not found: {rootPath}");
        }

        return fullPath;
    }

    private static bool IsSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".xml";

    private static string GetKind(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "cs",
            ".csproj" => "csproj",
            ".xml" => "xml",
            _ => "other"
        };

    private static bool IsExcludedPath(string root, string path)
    {
        var relative = NormalizeRelativePath(root, path);
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(".rimerror", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("TestResults", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool SameManifest(
        IReadOnlyList<ProjectIndexFile> left,
        IReadOnlyList<ProjectIndexFile> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (!first.RelativePath.Equals(second.RelativePath, StringComparison.Ordinal) ||
                !first.Kind.Equals(second.Kind, StringComparison.Ordinal) ||
                first.Length != second.Length ||
                first.LastWriteUtcTicks != second.LastWriteUtcTicks ||
                !string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsUnder(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(fullDirectory, comparison);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original cache-write failure.
        }
    }

    private sealed record TypeScope(
        string FullName,
        string ShortName,
        int Line,
        int BodyDepth);
}
