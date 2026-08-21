namespace RimLiaison.Observability;

/// <summary>
/// Resolves the one application-level location used by every production
/// observability consumer. This deliberately has no repository or current
/// directory input: a mod worktree must never become the runtime data store.
/// </summary>
public static class AgentObservabilityStorage
{
    public const string DirectoryEnvironmentVariable = "RIMLIAISON_OBSERVABILITY_DIR";
    public const string ApplicationDirectoryName = "RimLiaison";
    public const string ObservabilityDirectoryName = "observability";

    /// <summary>
    /// Returns the canonical application/runtime observability root.
    /// </summary>
    public static string ResolveCanonicalRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(
            DirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        string applicationData = ResolveApplicationDataDirectory();
        return Path.GetFullPath(Path.Combine(
            applicationData,
            ApplicationDirectoryName,
            ObservabilityDirectoryName));
    }

    /// <summary>
    /// Compatibility name for callers that describe the default as a storage
    /// directory rather than a canonical application root.
    /// </summary>
    public static string ResolveDefaultRoot() => ResolveCanonicalRoot();

    private static string ResolveApplicationDataDirectory()
    {
        string? localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return localApplicationData;
        }

        string? applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            return applicationData;
        }

        // This is only a host fallback for environments that do not expose a
        // platform application-data folder. It remains outside the current
        // directory and therefore outside repository worktrees.
        return Path.Combine(Path.GetTempPath(), ApplicationDirectoryName);
    }
}
