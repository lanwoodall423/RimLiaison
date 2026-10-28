using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Verse;
using RimWorld;

namespace DirectValidationRuntimeSignal
{
    [StaticConstructorOnStartup]
    public static class RuntimeSignal
    {
        private const string Protocol = "direct-validation-signal/v1";
        private const string HelperPackageId = "direct.validation.runtime.signal";

        static RuntimeSignal()
        {
            string resultPath = Environment.GetEnvironmentVariable("DIRECT_VALIDATION_SIGNAL_RESULT");
            if (string.IsNullOrWhiteSpace(resultPath))
                return;
            string targetPackageId = Environment.GetEnvironmentVariable("DIRECT_VALIDATION_TARGET_PACKAGE") ?? "";
            string targetAssembliesText = Environment.GetEnvironmentVariable("DIRECT_VALIDATION_TARGET_ASSEMBLIES") ?? "";
            var targetAssemblyNames = targetAssembliesText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string requestedDefsText = Environment.GetEnvironmentVariable("DIRECT_VALIDATION_TARGET_DEFS") ?? "";
            var requestedDefs = requestedDefsText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string typedDefsText = Environment.GetEnvironmentVariable(
                "DIRECT_VALIDATION_TARGET_TYPED_DEFS") ?? "";
            string biomeStepsText = Environment.GetEnvironmentVariable(
                "DIRECT_VALIDATION_EXPECTED_BIOME_GENSTEPS") ?? "";

            try
            {
                bool targetModLoaded = LoadedModManager.RunningModsListForReading.Any(
                    mod => string.Equals(mod.PackageId, targetPackageId, StringComparison.OrdinalIgnoreCase));
                var assemblyResults = targetAssemblyNames.Select(name =>
                {
                    bool loaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                        string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                    return "{\"name\":\"" + Escape(name) + "\",\"loaded\":" + (loaded ? "true" : "false") + "}";
                }).ToList();
                bool targetAssemblyLoaded = assemblyResults.Count == 0 || assemblyResults.All(
                    result => result.IndexOf("\"loaded\":true", StringComparison.Ordinal) >= 0);
                var defResults = new List<string>();
                var typedRequests = typedDefsText.Length == 0
                    ? requestedDefs.Select(name => new[] { "ThingDef", name }).ToList()
                    : ReadPairs(typedDefsText);
                foreach (string[] request in typedRequests)
                {
                    string defType = request[0];
                    string defName = request[1];
                    bool exists;
                    if (defType == "ThingDef")
                        exists = DefDatabase<ThingDef>.GetNamedSilentFail(defName) != null;
                    else if (defType == "GenStepDef")
                        exists = DefDatabase<GenStepDef>.GetNamedSilentFail(defName) != null;
                    else
                        throw new InvalidDataException("Unsupported direct validation Def type: " + defType);
                    defResults.Add("{\"defName\":\"" + Escape(defName) +
                        "\",\"defType\":\"" + Escape(defType) +
                        "\",\"exists\":" + (exists ? "true" : "false") + "}");
                }
                var biomeResults = new List<string>();
                foreach (string[] request in ReadPairs(biomeStepsText))
                {
                    string biomeName = request[0];
                    string genStepName = request[1];
                    BiomeDef biome = DefDatabase<BiomeDef>.GetNamedSilentFail(biomeName);
                    GenStepDef step = DefDatabase<GenStepDef>.GetNamedSilentFail(genStepName);
                    bool included = biome != null && step != null &&
                        biome.extraGenSteps != null && biome.extraGenSteps.Contains(step);
                    biomeResults.Add("{\"biomeDefName\":\"" + Escape(biomeName) +
                        "\",\"genStepDefName\":\"" + Escape(genStepName) +
                        "\",\"exists\":" + (included ? "true" : "false") + "}");
                }

                string json = "{\"protocol\":\"" + Protocol + "\",\"timestampUtc\":\"" + Escape(DateTime.UtcNow.ToString("O")) +
                    "\",\"helperPackageId\":\"" + HelperPackageId + "\",\"targetPackageId\":\"" + Escape(targetPackageId) +
                    "\",\"targetModLoaded\":" + (targetModLoaded ? "true" : "false") +
                    ",\"targetAssemblyLoaded\":" + (targetAssemblyLoaded ? "true" : "false") +
                    ",\"targetAssembliesLoaded\":[" + string.Join(",", assemblyResults) + "]" +
                    ",\"requestedDefs\":[" + string.Join(",", defResults) + "]" +
                    ",\"biomeGenSteps\":[" + string.Join(",", biomeResults) + "]" +
                    ",\"completion\":\"content-loaded\"}";
                WriteAtomic(resultPath, json);
            }
            catch (Exception ex)
            {
                string json = "{\"protocol\":\"" + Protocol + "\",\"timestampUtc\":\"" + Escape(DateTime.UtcNow.ToString("O")) +
                    "\",\"helperPackageId\":\"" + HelperPackageId + "\",\"targetPackageId\":\"" + Escape(targetPackageId) +
                    "\",\"targetModLoaded\":false,\"targetAssembliesLoaded\":[],\"requestedDefs\":[],\"completion\":\"error\",\"error\":\"" + Escape(ex.ToString()) + "\"}";
                try { WriteAtomic(resultPath, json); } catch { }
            }
            return;
        }

        private static List<string[]> ReadPairs(string text)
        {
            var result = new List<string[]>();
            if (string.IsNullOrWhiteSpace(text))
                return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string encoded in text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = encoded.Split('|');
                if (pair.Length != 2 || pair.Any(value => string.IsNullOrWhiteSpace(value) ||
                    value.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-' && ch != '.')) ||
                    !seen.Add(pair[0] + "|" + pair[1]))
                    throw new InvalidDataException("Invalid or duplicate direct-validation expectation.");
                result.Add(pair);
            }
            return result;
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 16);
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }

        private static void WriteAtomic(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
