namespace RimLiaison.RimDev;

internal static class ProjectBindingHealthStates
{
    public const string Healthy = "HEALTHY";
    public const string Repaired = "REPAIRED";
    public const string MissingRegistrationRepairable = "MISSING_REGISTRATION_REPAIRABLE";
    public const string StaleSourceRootRepairable = "STALE_SOURCE_ROOT_REPAIRABLE";
    public const string StaleRuntimeRootRepairable = "STALE_RUNTIME_ROOT_REPAIRABLE";
    public const string RimWorldRootMissing = "RIMWORLD_ROOT_MISSING";
    public const string RuntimeRootConflict = "RUNTIME_ROOT_CONFLICT";
    public const string ProjectIdentityConflict = "PROJECT_IDENTITY_CONFLICT";
    public const string SourceEqualsRuntime = "SOURCE_EQUALS_RUNTIME";
    public const string RuntimeOutsideMods = "RUNTIME_OUTSIDE_MODS";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Unknown = "UNKNOWN";
}

internal sealed record WorkspaceIntegrityEntry(
    string Project,
    string SourceRoot,
    string? RuntimeRoot,
    string Health,
    bool Repairable,
    string? IssueCode,
    string? OriginalRuntimeRoot,
    string? RepairedRuntimeRoot,
    string? ResolutionMethod,
    string WorkspaceEntryStatus,
    string? TimestampUtc = null,
    string? WorkflowId = null);

internal sealed record WorkspaceIntegrityAuditResult(
    bool Succeeded,
    string Status,
    IReadOnlyList<WorkspaceIntegrityEntry> Projects,
    string? ErrorCode = null,
    string? Error = null,
    string? NextAction = null)
{
    public bool HasBlockedProjects => Projects.Any(project =>
        project.Health is not (ProjectBindingHealthStates.Healthy or ProjectBindingHealthStates.Repaired));

    public object ToEvidence() => new
    {
        schemaVersion = "rimliaison-workspace-integrity/v1",
        status = Status,
        projects = Projects,
        errorCode = ErrorCode,
        error = Error,
        nextAction = NextAction
    };
}
