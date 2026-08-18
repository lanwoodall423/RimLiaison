namespace RimError.Core;

internal sealed class AssetDiagnosticParser : IRimWorldDiagnosticParser
{
    public DiagnosticClassification? TryParse(DiagnosticClassificationContext context)
    {
        var text = context.Text;
        var failure = RimWorldDiagnosticText.HasFailure(text);

        if (RimWorldDiagnosticText.Contains(text, "texture") && failure)
        {
            return new DiagnosticClassification
            {
                Category = "missing_texture",
                Asset = RimWorldDiagnosticText.ExtractAsset(text)
            };
        }

        if (RimWorldDiagnosticText.Contains(text, "shader") && failure)
        {
            return new DiagnosticClassification
            {
                Category = "missing_shader",
                Asset = RimWorldDiagnosticText.ExtractAsset(text)
            };
        }

        var unityLoading = RimWorldDiagnosticText.ContainsAny(
            text,
            "AssetBundle",
            "Resources.Load",
            "UnityEngine.Object");
        if (unityLoading && failure)
        {
            return new DiagnosticClassification
            {
                Category = "unity_asset_load",
                Asset = RimWorldDiagnosticText.ExtractAsset(text)
            };
        }

        if (RimWorldDiagnosticText.ContainsAny(text, "resource", "asset") && failure)
        {
            return new DiagnosticClassification
            {
                Category = "missing_asset",
                Asset = RimWorldDiagnosticText.ExtractAsset(text)
            };
        }

        return null;
    }
}
