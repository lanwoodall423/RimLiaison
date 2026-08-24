using System.Text.Json.Serialization;
using RimLiaison.DevBridge;

namespace RimLiaison.Results;

public sealed record RimTestBuildOwnedOutputChange
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// The compact, conservative proof that an affected source run used the
/// artifact identity established by DevBridge2. It intentionally does not
/// claim direct runtime DLL introspection.
/// </summary>
public sealed record RimTestArtifactFreshness
{
    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("builtArtifactSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltArtifactSha256 { get; init; }

    [JsonPropertyName("deployedArtifactSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeployedArtifactSha256 { get; init; }

    [JsonPropertyName("deploymentDecision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentDecision { get; init; }

    [JsonPropertyName("evaluationStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvaluationStatus { get; init; }

    [JsonPropertyName("generationBefore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GenerationBefore { get; init; }

    [JsonPropertyName("generationAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GenerationAfter { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("leaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeaseId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("artifactTestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactTestId { get; init; }

    [JsonPropertyName("operationIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OperationIds { get; init; }

    [JsonPropertyName("loadedArtifactFreshnessProven")]
    public bool LoadedArtifactFreshnessProven { get; init; }

    [JsonPropertyName("proof")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Proof { get; init; }

    [JsonPropertyName("sourceInputsStable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SourceInputsStable { get; init; }

    [JsonPropertyName("buildOwnedOutputChanges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RimTestBuildOwnedOutputChange>? BuildOwnedOutputChanges { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    public static RimTestArtifactFreshness From(
        DevBridgeModDevelopmentResult result,
        string? fallbackWorkflowId = null) => new()
        {
            SourceFingerprint = result.Freshness?.SourceFingerprint,
            BuiltArtifactSha256 = result.Freshness?.BuiltArtifactSha256,
            DeployedArtifactSha256 = result.Freshness?.DeployedArtifactSha256,
            DeploymentDecision = result.Freshness?.DeploymentDecision,
            EvaluationStatus = result.Freshness is null
                ? "NOT_EVALUATED"
                : result.Success == true &&
                    result.Freshness.LoadedArtifactFreshnessProven
                    ? "FRESH"
                    : result.Status.ErrorCode?.Contains(
                        "STALE",
                        StringComparison.OrdinalIgnoreCase) == true ||
                      string.Equals(
                          result.Freshness.DeploymentDecision,
                          "stale",
                          StringComparison.OrdinalIgnoreCase)
                        ? "STALE"
                        : "FAILED",
            GenerationBefore = result.Freshness?.GenerationBefore,
            GenerationAfter = result.Freshness?.GenerationAfter,
            Generation = result.Freshness?.Generation ?? result.Generation,
            TransactionId = result.TransactionId ?? result.Freshness?.TransactionId,
            WorkflowId = result.WorkflowId ?? result.Freshness?.WorkflowId ?? fallbackWorkflowId,
            LeaseId = result.LeaseId ?? result.Freshness?.LeaseId,
            LoadedArtifactFreshnessProven = result.Freshness?.LoadedArtifactFreshnessProven == true,
            Proof = result.Freshness?.Proof,
            ErrorCode = result.Freshness?.ErrorCode ?? result.Status.ErrorCode
        };
}
