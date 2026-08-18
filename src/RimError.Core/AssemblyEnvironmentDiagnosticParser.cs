namespace RimError.Core;

internal sealed class AssemblyEnvironmentDiagnosticParser : IRimWorldDiagnosticParser
{
    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var packageId = RimWorldDiagnosticText.ExtractPackageId(text);
        var failure = RimWorldDiagnosticText.HasFailure(text);

        if (packageId is not null &&
            RimWorldDiagnosticText.ContainsAny(text, "invalid", "duplicate", "missing", "not found", "problem", "error"))
        {
            return new DiagnosticClassification
            {
                Category = "package_id",
                PackageId = packageId
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "load order", "mod order", "must load before", "must load after") &&
            failure)
        {
            return new DiagnosticClassification { Category = "load_order" };
        }

        var dependencyFailure = RimWorldDiagnosticText.ContainsAny(
            text,
            "missing dependency",
            "could not load dependency",
            "unable to load dependency",
            "dependency failure",
            "dependency failed",
            "dependency error",
            "unresolved dependency",
            "dependency not found",
            "unable to resolve dependency",
            "dependency resolution failed");
        if (dependencyFailure && failure)
        {
            return new DiagnosticClassification
            {
                Category = "dependency_failure",
                Dependency = RimWorldDiagnosticText.ExtractDependency(text)
            };
        }

        var exceptionType = RimWorldDiagnosticText.SimpleName(context.Event.ExceptionType);
        if (exceptionType.Equals("TypeLoadException", StringComparison.OrdinalIgnoreCase) &&
            RimWorldDiagnosticText.Contains(text, "could not load type"))
        {
            return null;
        }

        var assemblyFailure =
            RimWorldDiagnosticText.ContainsAny(
                text,
                "file or assembly",
                "load assembly",
                "assembly resolution",
                "assembly loading") ||
            exceptionType is "FileNotFoundException" or "FileLoadException" or "BadImageFormatException";
        if (assemblyFailure && failure)
        {
            return new DiagnosticClassification
            {
                Category = "assembly_load",
                OriginatingAssembly = RimWorldDiagnosticText.ExtractAssembly(text)
            };
        }

        return null;
    }
}
