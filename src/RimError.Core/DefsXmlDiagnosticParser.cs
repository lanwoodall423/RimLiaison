using System.Text.RegularExpressions;

namespace RimError.Core;

internal sealed class DefsXmlDiagnosticParser : IRimWorldDiagnosticParser
{
    private static readonly Regex DefTokenPattern = new(
        @"\b[A-Za-z_][\w.]*Def(?:Of)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var def = RimWorldDiagnosticText.ExtractDef(text);

        if (RimWorldDiagnosticText.Contains(text, "Could not resolve cross-reference") ||
            (DefTokenPattern.IsMatch(text) &&
             RimWorldDiagnosticText.ContainsAny(text, "missing", "could not find", "not found", "no ")))
        {
            return new DiagnosticClassification
            {
                Category = "missing_def",
                DefType = def.DefType,
                DefName = def.DefName
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "duplicate defname", "duplicate def name", "duplicate def"))
        {
            return new DiagnosticClassification
            {
                Category = "duplicate_def",
                DefType = def.DefType,
                DefName = def.DefName
            };
        }

        if (RimWorldDiagnosticText.Contains(text, "config") &&
            RimWorldDiagnosticText.ContainsAny(text, "error", "failed", "invalid"))
        {
            return new DiagnosticClassification { Category = "config_error" };
        }

        var parentOrInheritance = RimWorldDiagnosticText.ContainsAny(
            text,
            "xml parent",
            "parent xml",
            "could not find parent",
            "inheritance",
            "inherits");
        if (parentOrInheritance &&
            RimWorldDiagnosticText.ContainsAny(text, "xml", "def", "parent") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "xml_parent",
                DefType = def.DefType,
                DefName = def.DefName
            };
        }

        if (RimWorldDiagnosticText.SimpleName(context.Event.ExceptionType)
                .Equals("XmlException", StringComparison.OrdinalIgnoreCase) ||
            RimWorldDiagnosticText.ContainsAny(
                text,
                "malformed xml",
                "error parsing xml",
                "xml parse error",
                "xml parsing failed"))
        {
            return new DiagnosticClassification { Category = "xml_malformed" };
        }

        if (RimWorldDiagnosticText.Contains(text, "PatchOperation") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification { Category = "patch_operation" };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "load xml", "loading xml", "xml loading") &&
            RimWorldDiagnosticText.HasFailure(text))
        {
            return new DiagnosticClassification
            {
                Category = "xml_load",
                DefType = def.DefType,
                DefName = def.DefName
            };
        }

        return null;
    }
}
