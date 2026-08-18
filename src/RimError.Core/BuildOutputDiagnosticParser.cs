using System.Text.RegularExpressions;

namespace RimError.Core;

internal sealed class BuildOutputDiagnosticParser : IRimWorldDiagnosticParser
{
    private static readonly Regex CompilerLevelPattern = new(
        @"\b(?<level>error|warning)\s+(?<code>CS\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var compiler = CompilerLevelPattern.Match(text);
        if (compiler.Success)
        {
            var level = compiler.Groups["level"].Value;
            return new DiagnosticClassification
            {
                Category = level.Equals("warning", StringComparison.OrdinalIgnoreCase)
                    ? "build_compile_warning"
                    : "build_compile_error",
                BuildCode = compiler.Groups["code"].Value.ToUpperInvariant()
            };
        }

        var code = RimWorldDiagnosticText.ExtractCode(text);
        if (code?.StartsWith("NU", StringComparison.OrdinalIgnoreCase) == true &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "build_restore",
                BuildCode = code,
                Dependency = RimWorldDiagnosticText.ExtractDependency(text)
            };
        }

        if (code?.StartsWith("MSB", StringComparison.OrdinalIgnoreCase) == true &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "build_msbuild",
                BuildCode = code
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "build failed", "msbuild failed", "msbuild error") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification { Category = "build_msbuild" };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "restore failed", "unable to resolve package", "package not found") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "build_restore",
                Dependency = RimWorldDiagnosticText.ExtractDependency(text)
            };
        }

        return null;
    }
}
