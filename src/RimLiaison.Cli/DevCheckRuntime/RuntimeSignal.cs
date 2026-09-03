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
                foreach (string defName in requestedDefs)
                {
                    bool exists = DefDatabase<ThingDef>.GetNamedSilentFail(defName) != null;
                    defResults.Add("{\"defName\":\"" + Escape(defName) + "\",\"defType\":\"ThingDef\",\"exists\":" + (exists ? "true" : "false") + "}");
                }

                string json = "{\"protocol\":\"" + Protocol + "\",\"timestampUtc\":\"" + Escape(DateTime.UtcNow.ToString("O")) +
                    "\",\"helperPackageId\":\"" + HelperPackageId + "\",\"targetPackageId\":\"" + Escape(targetPackageId) +
                    "\",\"targetModLoaded\":" + (targetModLoaded ? "true" : "false") +
                    ",\"targetAssemblyLoaded\":" + (targetAssemblyLoaded ? "true" : "false") +
                    ",\"targetAssembliesLoaded\":[" + string.Join(",", assemblyResults) + "]" +
                    ",\"requestedDefs\":[" + string.Join(",", defResults) + "] ,\"completion\":\"content-loaded\"}";
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
