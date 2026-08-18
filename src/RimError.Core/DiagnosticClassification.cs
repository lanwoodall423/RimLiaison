namespace RimError.Core;

internal sealed record DiagnosticClassification
{
    public required string Category { get; init; }

    public string? DefType { get; init; }

    public string? DefName { get; init; }

    public string? MissingMember { get; init; }

    public string? TargetType { get; init; }

    public string? TargetMethod { get; init; }

    public string? Asset { get; init; }

    public string? PackageId { get; init; }

    public string? Dependency { get; init; }

    public string? BuildCode { get; init; }

    public string? OriginatingAssembly { get; init; }
}
internal sealed record DiagnosticClassificationContext(
    RawDiagnosticEvent Event,
    string Text);

internal interface IRimWorldDiagnosticParser
{
    DiagnosticClassification? TryParse(DiagnosticClassificationContext context);
}
