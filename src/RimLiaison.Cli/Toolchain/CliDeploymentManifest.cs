using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Toolchain;

public static class CliDeploymentManifestSchemas
{
    public const string Current = "rimliaison-cli-manifest/v1";
}

public sealed record CliDeploymentFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record CliDeploymentManifest(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("ownerProduct")] string OwnerProduct,
    [property: JsonPropertyName("sourceCommit")] string SourceCommit,
    [property: JsonPropertyName("targetFramework")] string TargetFramework,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("files")] IReadOnlyList<CliDeploymentFileEntry> Files);

internal static class CliDeploymentManifestService
{
    public const string FileName = "rimliaison-cli-manifest.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static CliDeploymentManifest Write(
        string deploymentRoot,
        string sourceCommit,
        string targetFramework)
    {
        string root = Path.GetFullPath(deploymentRoot);
        Directory.CreateDirectory(root);
        string manifestPath = Path.Combine(root, FileName);
        CliDeploymentFileEntry[] entries = EnumerateEntries(root, manifestPath);
        if (entries.Length == 0)
            throw new InvalidDataException("The published RimLiaison CLI contains no deployment files.");

        CliDeploymentManifest manifest = new(
            CliDeploymentManifestSchemas.Current,
            ToolchainPromotionSchemas.OwnerProduct,
            sourceCommit,
            targetFramework,
            ComputePackageHash(entries),
            entries);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, WriteOptions));
        return manifest;
    }

    public static bool Verify(
        string deploymentRoot,
        string manifestPath,
        string? expectedManifestSha256,
        string? expectedPackageSha256,
        out CliDeploymentManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            string root = Path.GetFullPath(deploymentRoot);
            string path = Path.GetFullPath(manifestPath);
            if (!Directory.Exists(root) || !File.Exists(path) || !IsWithin(root, path))
            {
                error = "The CLI deployment manifest or deployment directory is missing.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedManifestSha256) &&
                !string.Equals(ToolchainFileHash.Sha256(path), expectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment manifest hash does not match its qualified identity.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<CliDeploymentManifest>(
                File.ReadAllText(path), WriteOptions);
            if (manifest is null ||
                !string.Equals(manifest.SchemaVersion, CliDeploymentManifestSchemas.Current, StringComparison.Ordinal) ||
                !string.Equals(manifest.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(manifest.SourceCommit) ||
                string.IsNullOrWhiteSpace(manifest.TargetFramework) ||
                string.IsNullOrWhiteSpace(manifest.PackageSha256) ||
                manifest.Files is null || manifest.Files.Count == 0)
            {
                error = "The CLI deployment manifest is incomplete or unsupported.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedPackageSha256) &&
                !string.Equals(manifest.PackageSha256, expectedPackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment package hash does not match its qualified identity.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CliDeploymentFileEntry entry in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256) ||
                    !seen.Add(entry.Path) || entry.Path.Contains('\\') || Path.IsPathRooted(entry.Path) ||
                    entry.Path.Contains(':'))
                {
                    error = "The CLI deployment manifest contains an invalid or duplicate file entry.";
                    return false;
                }
                string? file = SafePath(root, entry.Path);
                if (file is null || !File.Exists(file) ||
                    !string.Equals(ToolchainFileHash.Sha256(file), entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "The CLI deployment closure is missing or contains a mismatching file: " + entry.Path;
                    return false;
                }
            }

            string[] actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !SamePath(file, path))
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();
            string[] expected = seen.OrderBy(file => file, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                error = "The CLI deployment closure contains an unqualified or missing file.";
                return false;
            }
            if (!string.Equals(ComputePackageHash(manifest.Files), manifest.PackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The CLI deployment package hash does not match its manifest entries.";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            manifest = null;
            error = "The CLI deployment manifest could not be verified: " + exception.Message;
            return false;
        }
    }

    public static bool ContainsFile(CliDeploymentManifest manifest, string relativePath) =>
        manifest.Files.Any(entry => string.Equals(entry.Path, relativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase));

    public static string ComputePackageHash(IEnumerable<CliDeploymentFileEntry> entries) =>
        ComputePackageHash(entries.Select(entry => (entry.Path, entry.Sha256)));

    private static CliDeploymentFileEntry[] EnumerateEntries(string root, string manifestPath) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !SamePath(file, manifestPath))
            .Select(file => new CliDeploymentFileEntry(
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                ToolchainFileHash.Sha256(file)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

    private static string ComputePackageHash(IEnumerable<(string Path, string Sha256)> entries)
    {
        string canonical = string.Join("\n", entries
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => entry.Path.ToLowerInvariant() + "\0" + entry.Sha256));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? SafePath(string root, string relative)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        return IsWithin(root, candidate) ? candidate : null;
    }

    private static bool IsWithin(string root, string path)
    {
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path);
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
            SamePath(root, candidate);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
