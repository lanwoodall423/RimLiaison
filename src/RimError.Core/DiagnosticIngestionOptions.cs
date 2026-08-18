namespace RimError.Core;

public sealed record DiagnosticIngestionOptions
{
    public int MaxLineLength { get; init; } = 16_384;

    public int MaxMessageLength { get; init; } = 4_096;

    public int MaxRawSampleLength { get; init; } = 2_048;

    public int MaxStackDepth { get; init; } = 32;

    public int MaxFrameLength { get; init; } = 512;

    public int MaxContinuationLines { get; init; } = 128;

    public int MaxUniqueDiagnostics { get; init; } = 10_000;

    public DateTimeOffset? IngestionTime { get; init; }

    internal void Validate()
    {
        ValidatePositive(MaxLineLength, nameof(MaxLineLength));
        ValidatePositive(MaxMessageLength, nameof(MaxMessageLength));
        ValidatePositive(MaxRawSampleLength, nameof(MaxRawSampleLength));
        ValidatePositive(MaxStackDepth, nameof(MaxStackDepth));
        ValidatePositive(MaxFrameLength, nameof(MaxFrameLength));
        ValidatePositive(MaxContinuationLines, nameof(MaxContinuationLines));
        ValidatePositive(MaxUniqueDiagnostics, nameof(MaxUniqueDiagnostics));
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The limit must be positive.");
        }
    }
}
