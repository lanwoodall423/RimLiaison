using System.Text.Json;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class CliDeploymentTests
{
    public static void CompleteClosurePassesAndMutationFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-cli-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "rimliaison.exe"), "exe");
            File.WriteAllText(Path.Combine(root, "rimliaison.dll"), "assembly");
            File.WriteAllText(Path.Combine(root, "dependency.json"), "dependency");
            CliDeploymentManifest manifest = CliDeploymentManifestService.Write(root, "source", "net8.0");
            string manifestPath = Path.Combine(root, CliDeploymentManifestService.FileName);
            string manifestHash = ToolchainFileHash.Sha256(manifestPath);
            Assert(CliDeploymentManifestService.Verify(root, manifestPath, manifestHash, manifest.PackageSha256,
                out _, out _), "complete CLI deployment must verify");

            File.Delete(Path.Combine(root, "dependency.json"));
            Assert(!CliDeploymentManifestService.Verify(root, manifestPath, manifestHash, manifest.PackageSha256,
                out _, out _), "missing CLI dependency must fail closure verification");
            File.WriteAllText(Path.Combine(root, "dependency.json"), "substituted");
            Assert(!CliDeploymentManifestService.Verify(root, manifestPath, manifestHash, manifest.PackageSha256,
                out _, out _), "substituted CLI dependency must fail hash verification");
            File.WriteAllText(Path.Combine(root, "extra.dll"), "unlisted");
            Assert(!CliDeploymentManifestService.Verify(root, manifestPath, manifestHash, manifest.PackageSha256,
                out _, out _), "unlisted CLI file must fail strict closure verification");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void SelfCheckReturnsStructuredReadyJson()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = CliApplication.RunAsync(["self-check", "--json"], stdout, stderr)
            .GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        Assert(exitCode == 0 && document.RootElement.GetProperty("schemaVersion").GetString() == "rimliaison-self-check/v1" &&
            document.RootElement.GetProperty("status").GetString() == "ready" && string.IsNullOrWhiteSpace(stderr.ToString()),
            "self-check must return exit 0 and structured ready JSON");
    }
    public static void ChildProcessStartFailureRetainsBoundedEvidence()
    {
        PromotionChildProcessResult result = ToolchainPromotionService.RunJsonCommandAsync(
                Path.Combine(Path.GetTempPath(), "missing-rimliaison-" + Guid.NewGuid().ToString("N") + ".exe"),
                [],
                CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(result.ExitCode != 0 &&
            !string.IsNullOrWhiteSpace(result.StartError) &&
            result.Stdout.Length <= 512 * 1024 &&
            result.Stderr.Length <= 16 * 1024 &&
            !string.IsNullOrWhiteSpace(result.ExecutablePath) &&
            !string.IsNullOrWhiteSpace(result.WorkingDirectory),
            "child process start failure must retain bounded structured evidence");
    }


    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
