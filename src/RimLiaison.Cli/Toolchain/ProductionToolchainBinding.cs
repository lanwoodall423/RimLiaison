using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Toolchain;

internal enum ToolchainMode
{
    Production,
    Experimental
}

public sealed record ProductionToolchainBinding(
    string Fingerprint,
    string PromotedFingerprint,
    string RimLiaisonExecutablePath,
    string RimLiaisonExecutableHash,
    string RimLiaisonAssemblyPath,
    string RimLiaisonAssemblyHash,
    string DevBridgeCommandPath,
    string DevBridgeRuntimeRoot,
    string DevBridgePackageHash,
    string TransactionConsumerPath,
    string TransactionConsumerHash,
    string CompatibilityContract)
{
    public object ToEvidence() => new
    {
        mode = "production",
        fingerprint = Fingerprint,
        promotedFingerprint = PromotedFingerprint,
        rimLiaison = new
        {
            executablePath = RimLiaisonExecutablePath,
            executableSha256 = RimLiaisonExecutableHash,
            assemblyPath = RimLiaisonAssemblyPath,
            assemblySha256 = RimLiaisonAssemblyHash
        },
        devBridge = new
        {
            commandPath = DevBridgeCommandPath,
            runtimeRoot = DevBridgeRuntimeRoot,
            packageSha256 = DevBridgePackageHash
        },
        transactionConsumer = new
        {
            path = TransactionConsumerPath,
            sha256 = TransactionConsumerHash
        },
        compatibilityContract = CompatibilityContract
    };
}

internal sealed record ProductionToolchainBindingFailure(
    string ErrorCode,
    string Error,
    string NextAction,
    IReadOnlyList<string> RejectedCandidates,
    string? ExpectedFingerprint = null,
    string? CurrentExecutablePath = null,
    string? DevBridgeRuntimeRoot = null)
{
    public object ToEvidence() => new
    {
        mode = "production",
        errorCode = ErrorCode,
        error = Error,
        nextAction = NextAction,
        rejectedCandidates = RejectedCandidates,
        expectedFingerprint = ExpectedFingerprint,
        currentExecutablePath = CurrentExecutablePath,
        devBridgeRuntimeRoot = DevBridgeRuntimeRoot
    };
}

internal sealed record ProductionToolchainBindingResolution(
    ProductionToolchainBinding? Binding,
    ProductionToolchainBindingFailure? Failure)
{
    public bool Succeeded => Binding is not null;
}

internal sealed class ProductionToolchainManifest
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; init; }
    [JsonPropertyName("promotedFingerprint")]
    public string? PromotedFingerprint { get; init; }
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }
    [JsonPropertyName("rimLiaisonExecutablePath")]
    public string? RimLiaisonExecutablePath { get; init; }
    [JsonPropertyName("rimLiaisonExecutableSha256")]
    public string? RimLiaisonExecutableSha256 { get; init; }
    [JsonPropertyName("rimLiaisonAssemblyPath")]
    public string? RimLiaisonAssemblyPath { get; init; }
    [JsonPropertyName("rimLiaisonAssemblySha256")]
    public string? RimLiaisonAssemblySha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeRoot")]
    public string? DevBridgeRuntimeRoot { get; init; }
    [JsonPropertyName("devBridgePackageSha256")]
    public string? DevBridgePackageSha256 { get; init; }
    [JsonPropertyName("transactionConsumerPath")]
    public string? TransactionConsumerPath { get; init; }
    [JsonPropertyName("transactionConsumerSha256")]
    public string? TransactionConsumerSha256 { get; init; }
    [JsonPropertyName("compatibilityContract")]
    public string? CompatibilityContract { get; init; }
}

internal static class ProductionToolchainBindingResolver
{
    private const string ManifestSchema = "rimliaison-production-toolchain/v1";
    private const string CompatibilityContract = "devbridge-mod-development/v1";
    private const string ManifestEnvironment = "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST";
    private const string CliEnvironment = "RIMLIAISON_PRODUCTION_CLI";

    public static ProductionToolchainBindingResolution Resolve(
        string _repositoryRoot,
        string? requestedCliPath = null,
        string? requestedDevBridgePath = null,
        string? requestedDevBridgeRoot = null,
        string? currentExecutablePath = null)
    {
        var rejected = new List<string>();
        string? manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironment);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            string? configuredRoot = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_ROOT");
            manifestPath = string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine("C:\\RimDev", ".rimdev", "production-toolchain.json")
                : Path.Combine(configuredRoot, "production-toolchain.json");
        }

        if (!TryReadManifest(manifestPath, out ProductionToolchainManifest? manifest, out string? manifestError))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_MANIFEST_MISSING",
                manifestError ?? "The promoted production toolchain manifest could not be loaded.",
                "Install and promote the production RimLiaison toolchain, then retry.",
                rejected,
                manifestPath);
        }

        if (!string.Equals(manifest!.SchemaVersion, ManifestSchema, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.PromotedFingerprint) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonExecutablePath) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonExecutableSha256) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonAssemblyPath) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonAssemblySha256) ||
            string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeRoot) ||
            string.IsNullOrWhiteSpace(manifest.DevBridgePackageSha256) ||
            string.IsNullOrWhiteSpace(manifest.TransactionConsumerPath) ||
            string.IsNullOrWhiteSpace(manifest.TransactionConsumerSha256) ||
            !string.Equals(manifest.CompatibilityContract, CompatibilityContract, StringComparison.Ordinal))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_MANIFEST_INVALID",
                "The promoted production toolchain manifest is incomplete or incompatible.",
                "Regenerate the production manifest from the promoted artifacts.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint);
        }

        string cliPath = FullPath(manifest.RimLiaisonExecutablePath!);
        string assemblyPath = FullPath(manifest.RimLiaisonAssemblyPath!);
        string? configuredCli = Environment.GetEnvironmentVariable(CliEnvironment);
        if (!string.IsNullOrWhiteSpace(configuredCli) &&
            !SamePath(configuredCli, cliPath))
        {
            rejected.Add(FullPath(configuredCli));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "A production CLI override does not match the promoted immutable CLI.",
                "Use the promoted CLI recorded by the production manifest.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                configuredCli);
        }

        if (!string.IsNullOrWhiteSpace(requestedCliPath) &&
            !SamePath(requestedCliPath, cliPath))
        {
            rejected.Add(FullPath(requestedCliPath));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "The requested CLI is not the promoted production CLI.",
                "Remove the source-checkout override and invoke the promoted CLI.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                requestedCliPath);
        }

        string currentCli = FullPath(currentExecutablePath ?? Environment.ProcessPath ?? string.Empty);
        if (!SamePath(currentCli, cliPath))
        {
            rejected.Add(currentCli);
            return Fail(
                "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                "Production execution was requested from a non-promoted RimLiaison executable.",
                "Invoke the immutable promoted RimLiaison executable; use --experimental only for qualification or tooling work.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli);
        }

        string runtimeRoot = FullPath(manifest.DevBridgeRuntimeRoot!);
        if (!string.IsNullOrWhiteSpace(requestedDevBridgeRoot) &&
            !SamePath(requestedDevBridgeRoot, runtimeRoot))
        {
            rejected.Add(FullPath(requestedDevBridgeRoot));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "The requested DevBridge root is not the promoted runtime root.",
                "Use the installed DevBridge runtime root recorded by the production manifest.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string commandPath = Path.Combine(runtimeRoot, "DevBridge.cmd");
        if (!string.IsNullOrWhiteSpace(requestedDevBridgePath) &&
            !SamePath(requestedDevBridgePath, commandPath))
        {
            rejected.Add(FullPath(requestedDevBridgePath));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "The requested DevBridge command is not the promoted runtime command.",
                "Use the installed DevBridge.cmd recorded by the production manifest.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        if (!File.Exists(cliPath) || !File.Exists(assemblyPath) ||
            !File.Exists(commandPath) || !File.Exists(manifest.TransactionConsumerPath!))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
                "A promoted production toolchain artifact is missing.",
                "Repair or reinstall the promoted production toolchain.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }
        foreach (string variable in new[] { "DEVBRIDGE_SOURCE_ROOT", "RIMTEST_DEVBRIDGE_ROOT" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) &&
                !SamePath(Path.Combine(FullPath(value), "scripts", "mod-test.ps1"),
                    manifest.TransactionConsumerPath!))
            {
                return Fail(
                    "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                    $"Production environment variable {variable} points at an unpinned DevBridge source tree.",
                    "Clear the source-checkout override or point it at the pinned transaction consumer.",
                    rejected,
                    manifestPath,
                    manifest.PromotedFingerprint,
                    currentCli,
                    runtimeRoot);
            }
        }

        if (!string.Equals(Path.GetFileName(FullPath(manifest.TransactionConsumerPath!)),
            "mod-test.ps1", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_CONSUMER_INVALID",
                "The production transaction consumer is not the supported mod-test.ps1 consumer.",
                "Regenerate the production manifest from the supported DevBridge consumer.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string runtimeManifestPath = Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json");
        if (!File.Exists(runtimeManifestPath))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_RUNTIME_MANIFEST_MISSING",
                "The installed DevBridge runtime manifest is missing.",
                "Repair or reinstall the promoted DevBridge runtime.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string cliHash = Sha256(cliPath);
        string assemblyHash = Sha256(assemblyPath);
        string consumerHash = Sha256(manifest.TransactionConsumerPath!);
        string packageHash = ReadRuntimePackageHash(runtimeManifestPath);
        if (!string.Equals(cliHash, manifest.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assemblyHash, manifest.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packageHash, manifest.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(consumerHash, manifest.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
                "Promoted toolchain artifact hashes do not match the production manifest.",
                "Re-promote the complete toolchain atomically and retry.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string fingerprint = ComputeFingerprint(
            manifest.PromotedFingerprint!,
            cliHash,
            assemblyHash,
            runtimeRoot,
            packageHash,
            manifest.TransactionConsumerPath!,
            consumerHash,
            manifest.CompatibilityContract!);
        if (!string.IsNullOrWhiteSpace(manifest.Fingerprint) &&
            !string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
                "The production manifest fingerprint does not match its immutable components.",
                "Re-promote the complete toolchain atomically and retry.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }
        return new ProductionToolchainBindingResolution(
            new ProductionToolchainBinding(
                fingerprint,
                manifest.PromotedFingerprint!,
                cliPath,
                cliHash,
                assemblyPath,
                assemblyHash,
                commandPath,
                runtimeRoot,
                packageHash,
                FullPath(manifest.TransactionConsumerPath!),
                consumerHash,
                manifest.CompatibilityContract!),
            null);
    }

    private static ProductionToolchainBindingResolution Fail(
        string code,
        string error,
        string nextAction,
        List<string> rejected,
        string? manifestPath,
        string? expectedFingerprint = null,
        string? currentCli = null,
        string? runtimeRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            rejected.Add(FullPath(manifestPath));
        }
        return new ProductionToolchainBindingResolution(
            null,
            new ProductionToolchainBindingFailure(
                code,
                error,
                nextAction,
                rejected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                expectedFingerprint,
                currentCli,
                runtimeRoot));
    }

    private static bool TryReadManifest(
        string path,
        out ProductionToolchainManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"Production manifest was not found: {FullPath(path)}.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<ProductionToolchainManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                });
            if (manifest is null)
            {
                error = "Production manifest was empty.";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Production manifest could not be read: {exception.Message}";
            return false;
        }
    }

    private static string ReadRuntimePackageHash(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        return document.RootElement.TryGetProperty("packageSha256", out JsonElement packageHash)
            ? packageHash.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ComputeFingerprint(params string[] values)
    {
        string payload = string.Join("\n", values);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "tcx-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FullPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static bool SamePath(string left, string right) =>
        string.Equals(FullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            FullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
