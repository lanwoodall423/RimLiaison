using System.Collections.ObjectModel;

namespace RimLiaison.Stack;

/// <summary>
/// The verified identity domains for one project-owned production repository.
/// CanonicalProjectId is the stable machine identity; aliases are explicit manifest
/// values only. Package and runtime folder values are never identity aliases.
/// </summary>
public sealed record ProjectIdentity(
    string CanonicalProjectId,
    string DisplayName,
    string RoutingSlug,
    string? PackageId,
    string SourceOwner,
    string RuntimeFolder,
    IReadOnlyList<string> Aliases);

public static class ProjectIdentityResolver
{
    public static ProjectIdentity Resolve(RimDevStackManifest manifest, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string sourceOwner = string.IsNullOrWhiteSpace(repositoryRoot)
            ? string.Empty
            : Path.GetFullPath(repositoryRoot);
        string canonical = string.IsNullOrWhiteSpace(manifest.DevBridgeProject)
            ? manifest.Project
            : manifest.DevBridgeProject;
        string routingSlug = string.IsNullOrWhiteSpace(manifest.DevBridgeProject)
            ? canonical
            : manifest.DevBridgeProject;
        string displayName = manifest.Project;
        string runtimeFolder = string.IsNullOrWhiteSpace(manifest.RuntimeFolder)
            ? displayName
            : manifest.RuntimeFolder;
        var aliases = new[] { canonical, displayName, manifest.DevBridgeProject }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(
            canonical,
            displayName,
            routingSlug,
            manifest.PackageId,
            sourceOwner,
            runtimeFolder,
            new ReadOnlyCollection<string>(aliases));
    }

    public static bool Matches(ProjectIdentity identity, string? requested)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }
        string value = requested.Trim();
        return identity.Aliases.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    public static bool SameCanonical(ProjectIdentity left, ProjectIdentity right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(
            left.CanonicalProjectId,
            right.CanonicalProjectId,
            StringComparison.OrdinalIgnoreCase);
    }
}
