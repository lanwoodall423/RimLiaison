namespace RimError.Core;

public sealed record DiagnosticIngestionMetadata
{
    public string? WorkflowId { get; init; }

    public string? RunId { get; init; }

    public string? TestId { get; init; }

    public string? OperationId { get; init; }

    public string? OperationName { get; init; }

    public string? SourceAttribution { get; init; }

    public string? RimWorldVersion { get; init; }

    public string? ModProfile { get; init; }

    public DiagnosticIntegrationState? Integration { get; init; }
}
