using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;

namespace RimLiaison.Validation;

public static class ValidationCapabilitySchema
{
    public const string Current = "rimtest-validation-capability/v1";
    public const string UnavailableCode = "VALIDATION_CAPABILITY_UNAVAILABLE";
    public const string IncompatibleCode = "VALIDATION_CAPABILITY_INCOMPATIBLE";
    public const string DiscoveryFailedCode = "VALIDATION_CAPABILITY_DISCOVERY_FAILED";
}

public enum ValidationCapabilityPreflightOutcome
{
    Available,
    Blocked,
    InfrastructureFailure,
    Cancelled
}

public sealed record ValidationCapabilityEvidence
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = ValidationCapabilitySchema.Current;

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "blocked";

    [JsonPropertyName("state")]
    public string State { get; init; } = "CAPABILITY_GAP";

    [JsonPropertyName("errorCode")]
    public required string ErrorCode { get; init; }

    [JsonPropertyName("validationId")]
    public required string ValidationId { get; init; }

    [JsonPropertyName("requiredCapabilityId")]
    public required string RequiredCapabilityId { get; init; }

    [JsonPropertyName("expectedProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedProvider { get; init; }

    [JsonPropertyName("minimumSchemaVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumSchemaVersion { get; init; }

    [JsonPropertyName("minimumVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumVersion { get; init; }

    [JsonPropertyName("discoveredProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscoveredProvider { get; init; }

    [JsonPropertyName("discoveredSchemaVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscoveredSchemaVersion { get; init; }

    [JsonPropertyName("discoveredVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscoveredVersion { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("probableOwner")]
    public required string ProbableOwner { get; init; }

    [JsonPropertyName("recommendedRemediation")]
    public required string RecommendedRemediation { get; init; }

    [JsonPropertyName("operationAttempted")]
    public bool OperationAttempted { get; init; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("agentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("evidenceLink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceLink { get; init; }

    [JsonPropertyName("discoveryErrorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiscoveryErrorCode { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }
}

public sealed record ValidationCapabilityPreflightResult(
    ValidationCapabilityPreflightOutcome Outcome,
    IReadOnlyList<ValidationCapabilityEvidence> Evidence,
    string? ErrorCode = null)
{
    public bool IsAvailable => Outcome == ValidationCapabilityPreflightOutcome.Available;
    public bool IsBlocked => Outcome == ValidationCapabilityPreflightOutcome.Blocked;
}

/// <summary>
/// Negotiates declared validation requirements through the read-only capability registry.
/// It never invokes tools or owns lifecycle state.
/// </summary>
public sealed class ValidationCapabilityNegotiator
{
    private readonly IDevBridgeCapabilityAdapter capabilityAdapter;

    public ValidationCapabilityNegotiator(IDevBridgeCapabilityAdapter capabilityAdapter)
    {
        this.capabilityAdapter = capabilityAdapter ?? throw new ArgumentNullException(nameof(capabilityAdapter));
    }

    public async Task<ValidationCapabilityPreflightResult> NegotiateAsync(
        CatalogTest test,
        string? workflowId = null,
        string? leaseId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(test);
        IReadOnlyList<CatalogCapabilityRequirement> requirements = test.RequiredCapabilities ?? [];
        if (requirements.Count == 0)
        {
            return new(ValidationCapabilityPreflightOutcome.Available, []);
        }

        var evidence = new List<ValidationCapabilityEvidence>(requirements.Count);
        foreach (CatalogCapabilityRequirement requirement in requirements)
        {
            DevBridgeCapabilityDiscoveryResult discovered = await capabilityAdapter.DiscoverAsync(
                    new DevBridgeCapabilityQuery(Text: requirement.CapabilityId, Limit: 100),
                    workflowId,
                    leaseId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (discovered.Status.Outcome == DevBridgeCapabilityOutcome.Cancelled)
            {
                return new(
                    ValidationCapabilityPreflightOutcome.Cancelled,
                    evidence,
                    discovered.Status.ErrorCode ?? "RIMTEST_CANCELLED");
            }

            if (!discovered.Status.IsSuccess)
            {
                return new(
                    ValidationCapabilityPreflightOutcome.InfrastructureFailure,
                    evidence,
                    discovered.Status.ErrorCode ?? ValidationCapabilitySchema.DiscoveryFailedCode);
            }

            DevBridgeCapability? match = discovered.Capabilities.FirstOrDefault(
                capability => string.Equals(
                    capability.Id,
                    requirement.CapabilityId,
                    StringComparison.Ordinal));
            if (match is null)
            {
                evidence.Add(CreateEvidence(
                    test,
                    requirement,
                    ValidationCapabilitySchema.UnavailableCode,
                    workflowId,
                    reason: requirement.Purpose,
                    discovered: null));
                continue;
            }

            bool providerCompatible = string.IsNullOrWhiteSpace(requirement.ExpectedProvider) ||
                string.Equals(
                    requirement.ExpectedProvider,
                    match.ProviderId,
                    StringComparison.OrdinalIgnoreCase);
            bool schemaCompatible = IsAtLeast(
                match.SchemaVersion,
                requirement.MinimumSchemaVersion);
            bool versionCompatible = IsAtLeast(
                match.Version,
                requirement.MinimumVersion);
            if (!providerCompatible || !schemaCompatible || !versionCompatible)
            {
                evidence.Add(CreateEvidence(
                    test,
                    requirement,
                    ValidationCapabilitySchema.IncompatibleCode,
                    workflowId,
                    reason: requirement.Purpose,
                    discovered: match));
            }
        }

        if (evidence.Count > 0)
        {
            return new(
                ValidationCapabilityPreflightOutcome.Blocked,
                evidence,
                evidence[0].ErrorCode);
        }

        return new(ValidationCapabilityPreflightOutcome.Available, []);
    }

    private static ValidationCapabilityEvidence CreateEvidence(
        CatalogTest test,
        CatalogCapabilityRequirement requirement,
        string errorCode,
        string? workflowId,
        string reason,
        DevBridgeCapability? discovered)
    {
        string expectedProvider = requirement.ExpectedProvider ?? "any provider";
        string owner = requirement.Owner ??
            discovered?.ProviderId ??
            (string.IsNullOrWhiteSpace(expectedProvider) ? "capability provider" : expectedProvider);
        string remediation = errorCode == ValidationCapabilitySchema.UnavailableCode
            ? $"Install or enable capability {requirement.CapabilityId} from {owner}, then refresh capability discovery."
            : $"Upgrade or configure {owner} so capability {requirement.CapabilityId} satisfies the declared provider/schema/version requirement.";
        return new ValidationCapabilityEvidence
        {
            ErrorCode = errorCode,
            ValidationId = test.Id,
            RequiredCapabilityId = requirement.CapabilityId,
            ExpectedProvider = requirement.ExpectedProvider,
            MinimumSchemaVersion = requirement.MinimumSchemaVersion,
            EvidenceLink = "devbridge-capability-registry/" + requirement.CapabilityId,
            MinimumVersion = requirement.MinimumVersion,
            DiscoveredProvider = discovered?.ProviderId,
            DiscoveredSchemaVersion = discovered?.SchemaVersion,
            DiscoveredVersion = discovered?.Version,
            Reason = reason,
            ProbableOwner = owner,
            RecommendedRemediation = remediation,
            OperationAttempted = false,
            WorkflowId = workflowId,
            AgentId = Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_AGENT"),
            Fingerprint = "capability|" + requirement.CapabilityId + "|provider|" + expectedProvider,
        };
    }

    private static bool IsAtLeast(string? actual, string? minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        if (TryParseComparableVersion(actual, out Version? actualVersion) &&
            TryParseComparableVersion(minimum, out Version? minimumVersion))
        {
            return actualVersion >= minimumVersion;
        }

        return string.Equals(actual.Trim(), minimum.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseComparableVersion(string value, out Version? version)
    {
        string candidate = value.Trim();
        Match match = Regex.Match(candidate, @"(\d+(?:\.\d+){0,3})$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            version = null;
            return false;
        }

        string numeric = match.Groups[1].Value;
        if (!numeric.Contains('.', StringComparison.Ordinal))
        {
            numeric += ".0";
        }

        return Version.TryParse(numeric, out version);
    }
}
