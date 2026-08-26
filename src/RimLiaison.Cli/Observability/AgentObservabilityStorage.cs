using System.Globalization;
using System.Text;

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
    public const string DiagnosticDirectoryName = "diagnostics";

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
    /// <summary>
    /// Returns the application-level directory for bounded startup diagnostics.
    /// It is intentionally separate from the shared observability history.
    /// </summary>
    public static string ResolveDiagnosticRoot() =>
        Path.GetFullPath(Path.Combine(
            ResolveApplicationDataDirectory(),
            ApplicationDirectoryName,
            DiagnosticDirectoryName));

    /// <summary>
    /// Writes a bounded startup diagnostic without exposing arbitrary exception
    /// payloads. The returned path is suitable for user-facing error text.
    /// </summary>
    public static string WriteStartupDiagnostic(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string directory = ResolveDiagnosticRoot();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            "desktop-startup-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N") + ".log");
        string content =
            "RimLiaison Observability UI startup failure\r\n" +
            "Utc: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\r\n" +
            "Exception: " + BoundDiagnosticText(exception.GetType().FullName, 256) + "\r\n" +
            "Message: " + BoundDiagnosticText(exception.Message, 2_048) + "\r\n" +
            "Stack:\r\n" + BoundDiagnosticText(exception.StackTrace, 8_192) + "\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string BoundDiagnosticText(string? value, int maximum) =>
        string.IsNullOrEmpty(value)
            ? "(none)"
            : value.Length <= maximum
                ? value
                : value[..maximum] + " [truncated]";

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
