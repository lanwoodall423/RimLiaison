namespace RimError.Core;

internal static class RimWorldDiagnosticClassifier
{
    private static readonly IRimWorldDiagnosticParser[] Parsers =
    [
        new BuildOutputDiagnosticParser(),
        new DefsXmlDiagnosticParser(),
        new HarmonyDiagnosticParser(),
        new AssetDiagnosticParser(),
        new AssemblyEnvironmentDiagnosticParser(),
        new RuntimeDiagnosticParser()
    ];

    public static DiagnosticClassification? Classify(RawDiagnosticEvent diagnostic)
    {
        var text = RimWorldDiagnosticText.Combine(diagnostic);
        var context = new DiagnosticClassificationContext(diagnostic, text);

        foreach (var parser in Parsers)
        {
            var classification = parser.TryParse(context);
            if (classification is not null)
            {
                return classification;
            }
        }

        return null;
    }
}
