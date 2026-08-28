using System.Text.Json;
using System.Xml.Linq;

namespace RimLiaison.Stack;

internal static class ProjectMetadataValidator
{
    private static readonly string[] RuntimeIncludeRoots =
    [
        "About/**",
        "1.*/**"
    ];

    public static string? Validate(RimDevStackManifest manifest, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(manifest.Workload))
        {
            return null;
        }

        if (manifest.Workload is not ("production" or "fixture" or "test" or "internal" or "example"))
        {
            return "PROJECT_METADATA_WORKLOAD_INVALID";
        }

        if (manifest.Workload != "production")
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.ProjectType) ||
            string.IsNullOrWhiteSpace(manifest.PackageId) ||
            string.IsNullOrWhiteSpace(manifest.SourceProject) ||
            string.IsNullOrWhiteSpace(manifest.Configuration) ||
            string.IsNullOrWhiteSpace(manifest.ExpectedAssembly) ||
            string.IsNullOrWhiteSpace(manifest.DeploymentTarget) ||
            string.IsNullOrWhiteSpace(manifest.TestRecipe) ||
            manifest.RuntimePackage is not { ValueKind: JsonValueKind.Object })
        {
            return "PROJECT_METADATA_MISSING";
        }

        if (!IsToken(manifest.ProjectType) ||
            !IsToken(manifest.PackageId) ||
            !IsRelativePath(manifest.SourceProject, ".csproj") ||
            !IsRelativePath(manifest.DeploymentTarget, null) ||
            !IsAssembly(manifest.ExpectedAssembly) ||
            !IsToken(manifest.Configuration) ||
            !IsToken(manifest.TestRecipe) ||
            (manifest.Dependencies is not null &&
                manifest.Dependencies.Any(dependency => !IsToken(dependency))))
        {
            return "PROJECT_METADATA_FIELD_INVALID";
        }

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                manifest.SourceProject.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return "PROJECT_METADATA_FIELD_INVALID";
        }
        if (!IsWithin(sourcePath, repositoryRoot) || !File.Exists(sourcePath))
        {
            return "PROJECT_METADATA_SOURCE_MISSING";
        }

        string? assemblyName = ReadAssemblyName(sourcePath);
        if (!string.Equals(assemblyName + ".dll", manifest.ExpectedAssembly, StringComparison.OrdinalIgnoreCase))
        {
            return "PROJECT_METADATA_IDENTITY_CONTRADICTION";
        }

        string aboutPath = Path.Combine(repositoryRoot, "About", "About.xml");
        if (File.Exists(aboutPath))
        {
            string? packageId = ReadPackageId(aboutPath);
            if (!string.Equals(packageId, manifest.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return "PROJECT_METADATA_IDENTITY_CONTRADICTION";
            }
        }

        JsonElement runtimePackage = manifest.RuntimePackage.Value;
        if (!TryValidateRuntimePackage(runtimePackage))
        {
            return "PROJECT_METADATA_RUNTIME_PACKAGE_INVALID";
        }

        return null;
    }

    private static bool TryValidateRuntimePackage(JsonElement package)
    {
        string[] allowed = ["sourceRoot", "include", "exclude"];
        if (package.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
        {
            return false;
        }
        if (!TryGetString(package, "sourceRoot", out string? sourceRoot) || sourceRoot != "." ||
            !package.TryGetProperty("include", out JsonElement include) ||
            include.ValueKind != JsonValueKind.Array ||
            !package.TryGetProperty("exclude", out JsonElement exclude) ||
            exclude.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        HashSet<string> includes = include.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!RuntimeIncludeRoots.All(includes.Contains))
        {
            return false;
        }

        HashSet<string> excludes = exclude.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return excludes.Contains(".rimdev/**") &&
            excludes.Contains("Source/**") &&
            excludes.Contains("bin/**") &&
            excludes.Contains("obj/**") &&
            !includes.Contains(".rimdev/**");
    }

    private static string? ReadAssemblyName(string path)
    {
        try
        {
            XDocument document = XDocument.Load(path, LoadOptions.None);
            return document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value.Trim()
                ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadPackageId(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.None)
                .Descendants("packageId")
                .FirstOrDefault()?.Value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement parent, string name, out string? value)
    {
        value = null;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsAssembly(string? value) =>
        IsToken(value?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true
            ? value[..^4]
            : null) &&
        value!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelativePath(string? value, string? extension)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':'))
        {
            return false;
        }

        string[] segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
            segments.All(segment => segment is not ("." or "..")) &&
            (extension is null || value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithin(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
