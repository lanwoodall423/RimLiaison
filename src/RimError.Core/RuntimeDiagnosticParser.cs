using System.Text.RegularExpressions;

namespace RimError.Core;

internal sealed class RuntimeDiagnosticParser : IRimWorldDiagnosticParser
{
    private static readonly Regex DefTokenPattern = new(
        @"\b[A-Za-z_][\w.]*Def(?:Of)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var exceptionType = RimWorldDiagnosticText.SimpleName(context.Event.ExceptionType);

        if (RimWorldDiagnosticText.Contains(text, "exception ticking"))
        {
            return new DiagnosticClassification { Category = "runtime_ticking" };
        }

        if (RimWorldDiagnosticText.Contains(text, "exception drawing"))
        {
            return new DiagnosticClassification { Category = "runtime_drawing" };
        }

        if (RimWorldDiagnosticText.Contains(text, "LongEventHandler") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification { Category = "runtime_long_event" };
        }

        if (exceptionType.Equals("TypeInitializationException", StringComparison.OrdinalIgnoreCase))
        {
            var target = RimWorldDiagnosticText.ExtractNamedType(text);
            return new DiagnosticClassification
            {
                Category = "runtime_type_initialization",
                TargetType = target
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "type initializer", "static constructor", ".cctor") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "runtime_static_initialization",
                TargetType = RimWorldDiagnosticText.ExtractNamedType(text)
            };
        }

        switch (exceptionType)
        {
            case "NullReferenceException":
                return new DiagnosticClassification { Category = "runtime_null_reference" };
            case "MissingMethodException":
                return MissingMemberClassification("runtime_missing_method", text);
            case "MissingFieldException":
                return MissingMemberClassification("runtime_missing_field", text);
            case "TypeLoadException":
                return new DiagnosticClassification
                {
                    Category = "runtime_type_load",
                    TargetType = RimWorldDiagnosticText.ExtractNamedType(text),
                    OriginatingAssembly = RimWorldDiagnosticText.ExtractAssembly(text)
                };
            case "ReflectionTypeLoadException":
                return new DiagnosticClassification { Category = "runtime_reflection_type_load" };
        }

        if (!string.IsNullOrWhiteSpace(context.Event.ExceptionType) &&
            (context.Event.StackFrames is { Length: > 0 } ||
             exceptionType.EndsWith("Exception", StringComparison.OrdinalIgnoreCase)))
        {
            return new DiagnosticClassification { Category = "runtime_exception" };
        }

        if (DefTokenPattern.IsMatch(text) &&
            RimWorldDiagnosticText.ContainsAny(text, "error", "failed", "exception"))
        {
            return new DiagnosticClassification { Category = "runtime_exception" };
        }

        return null;
    }

    private static DiagnosticClassification MissingMemberClassification(
        string category,
        string text)
    {
        var member = RimWorldDiagnosticText.ExtractMissingMember(text);
        var target = RimWorldDiagnosticText.ParseTarget(member);
        return new DiagnosticClassification
        {
            Category = category,
            MissingMember = member,
            TargetType = target.TargetType,
            TargetMethod = target.TargetMethod
        };
    }
}
