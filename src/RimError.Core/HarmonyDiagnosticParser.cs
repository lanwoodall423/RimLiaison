namespace RimError.Core;

internal sealed class HarmonyDiagnosticParser : IRimWorldDiagnosticParser
{
    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var failure = RimWorldDiagnosticText.HasFailure(text);
        var patchFailure = failure &&
            RimWorldDiagnosticText.ContainsAny(
                text,
                "prefix",
                "postfix",
                "transpiler",
                "patch processing",
                "patching");
        if (!RimWorldDiagnosticText.ContainsAny(text, "Harmony", "HarmonyPatch", "patch target") &&
            !patchFailure)
        {
            return null;
        }

        var target = RimWorldDiagnosticText.ExtractTarget(text);
        var targetFailure =
            RimWorldDiagnosticText.ContainsAny(
                text,
                "undefined patch target",
                "target method not found",
                "could not find method",
                "no target method",
                "patch target",
                "failed to patch",
                "could not patch",
                "unable to patch");

        if (targetFailure && failure)
        {
            return new DiagnosticClassification
            {
                Category = "harmony_target",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        if (RimWorldDiagnosticText.SimpleName(context.Event.ExceptionType)
                .Equals("HarmonyException", StringComparison.OrdinalIgnoreCase) ||
            RimWorldDiagnosticText.Contains(text, "HarmonyException"))
        {
            return new DiagnosticClassification
            {
                Category = "harmony_exception",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "prefix", "postfix") &&
            failure)
        {
            return new DiagnosticClassification
            {
                Category = "harmony_prefix_postfix",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        if (RimWorldDiagnosticText.Contains(text, "signature") &&
            RimWorldDiagnosticText.ContainsAny(text, "mismatch", "does not match", "invalid", "wrong") &&
            failure)
        {
            return new DiagnosticClassification
            {
                Category = "harmony_signature",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        if (RimWorldDiagnosticText.Contains(text, "transpiler") &&
            failure)
        {
            return new DiagnosticClassification
            {
                Category = "harmony_transpiler",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "patch processing", "processing patch", "patching failed") &&
            failure)
        {
            return new DiagnosticClassification
            {
                Category = "harmony_processing",
                TargetType = target.TargetType,
                TargetMethod = target.TargetMethod
            };
        }

        return null;
    }
}
