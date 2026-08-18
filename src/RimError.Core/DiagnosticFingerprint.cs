using System.Security.Cryptography;
using System.Text;

namespace RimError.Core;

public static class DiagnosticFingerprint
{
    public const int CurrentSchemaVersion = 1;

    private const int FingerprintBytes = 16;

    public static string Compute(DiagnosticRecord diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var canonical = new StringBuilder();
        AppendStable(canonical, diagnostic.Category);
        AppendStable(canonical, diagnostic.ExceptionType);
        AppendMessage(canonical, string.IsNullOrWhiteSpace(diagnostic.NormalizedMessage)
            ? diagnostic.Message
            : diagnostic.NormalizedMessage);
        AppendStable(canonical, diagnostic.OriginatingAssembly);
        AppendStable(canonical, diagnostic.OriginatingType);
        AppendStable(canonical, diagnostic.OriginatingMethod);
        AppendStable(canonical, diagnostic.TargetType);
        AppendStable(canonical, diagnostic.TargetMethod);

        if (diagnostic.StackFrames is not null)
        {
            foreach (var frame in diagnostic.StackFrames.Take(8))
            {
                AppendMessage(canonical, frame);
            }
        }

        AppendStable(canonical, diagnostic.DefType);
        AppendStable(canonical, diagnostic.DefName);
        AppendStable(canonical, diagnostic.MissingMember);
        AppendStable(canonical, diagnostic.Asset);
        AppendStable(canonical, diagnostic.PackageId);
        AppendStable(canonical, diagnostic.Dependency);
        AppendStable(canonical, diagnostic.BuildCode);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return $"d-{Convert.ToHexString(digest[..FingerprintBytes]).ToLowerInvariant()}";
    }

    public static string NormalizeMessage(string? message)
        => DiagnosticNormalizer.NormalizeMessage(message);

    private static void AppendStable(StringBuilder builder, string? value)
    {
        var normalized = DiagnosticNormalizer.NormalizeStableValue(value);
        builder.Append(normalized.Length);
        builder.Append(':');
        builder.Append(normalized);
        builder.Append('|');
    }

    private static void AppendMessage(StringBuilder builder, string? value)
    {
        var normalized = DiagnosticNormalizer.NormalizeMessage(value);
        builder.Append(normalized.Length);
        builder.Append(':');
        builder.Append(normalized);
        builder.Append('|');
    }
}
