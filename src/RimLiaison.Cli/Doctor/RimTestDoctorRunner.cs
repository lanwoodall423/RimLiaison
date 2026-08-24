using System.Text.Json;
using RimContext.Core;
using RimContext.Core.Contracts;
using RimLiaison.Catalog;
using RimLiaison.DevBridge;
using RimLiaison.RimContext;
using RimLiaison.RimError;
using RimLiaison.Recovery;

namespace RimLiaison.Doctor;

public static class RimTestDoctorSchema
{
    public const string Current = "rimtest-doctor/v1";
}

internal sealed class RimTestDoctorRunner
{
    private const string AffectedNextAction = "rimliaison affected --run --json";
    private const string ManifestRepairNextAction =
        "rimliaison init --json --manifest-only --force";
    private const string RimContextIndexNextAction = "rimliaison affected --run --json";
    private const string DevBridgeDoctorNextAction = "DevBridge.cmd doctor --json";
    private const int MaximumProbeStdoutBytes = 512 * 1024;
    private const int MaximumProbeStderrBytes = 16 * 1024;

    private readonly TextWriter stderr;

    public RimTestDoctorRunner(TextWriter stderr)
    {
        this.stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
    }

    public async Task<DoctorRunResult> RunAsync(
        CliRequest request,
        IDevBridgeProcessTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);

        if (!request.StackManifest.Found)
        {
            return Blocked(
                "manifest",
                request.StackManifest.ErrorCode ?? "STACK_MANIFEST_MISSING",
                "rimliaison init --json");
        }

        if (request.StackManifest.Manifest is null)
        {
            return Blocked(
                "manifest",
                request.StackManifest.ErrorCode ?? "STACK_MANIFEST_INVALID",
                ManifestRepairNextAction);
        }

        if (string.IsNullOrWhiteSpace(request.DevBridgeProject))
        {
            return Blocked(
                "manifest",
                "STACK_MANIFEST_DEVBRIDGE_PROJECT_MISSING",
                MissingManifestConfigurationNextAction(request));
        }

        CatalogLoadResult catalog = CatalogLoader.Load(request.CatalogPath);
        if (catalog.Catalog is null)
        {
            string code = FirstErrorCode(catalog.Errors, "CATALOG_INVALID");
            return Blocked(
                "catalog",
                code,
                CatalogNextAction(request, code));
        }

        IReadOnlySet<string>? recipeIds = null;
        if (request.RecipeListPath is not null)
        {
            RecipeListLoadResult recipeList = RecipeListLoader.Load(request.RecipeListPath);
            if (recipeList.RecipeIds is null)
            {
                string code = FirstErrorCode(recipeList.Errors, "RECIPE_LIST_INVALID");
                return Blocked(
                    "catalog",
                    code,
                    RecipeListNextAction(request));
            }

            recipeIds = recipeList.RecipeIds;
        }

        CatalogValidationResult validation = CatalogValidator.Validate(
            catalog.Catalog,
            recipeIds);
        if (!validation.IsValid)
        {
            string code = FirstErrorCode(validation.Errors, "CATALOG_INVALID");
            return Blocked(
                "catalog",
                code,
                CatalogValidationNextAction(request));
        }

        DoctorRunResult? fallbackFailure = ValidateFallback(
            request,
            catalog.Catalog);
        if (fallbackFailure is not null)
        {
            return fallbackFailure;
        }

        RimContextAdapterOptions rimContext;
        DevBridgeAdapterOptions devBridge;
        RimErrorAdapterOptions rimError;
        try
        {
            rimContext = RimContextAdapterOptions.Discover(
                request.RimContextPath,
                request.RimContextRootPath,
                request.RimContextStorePath,
                request.RimContextDepth,
                request.RimContextLimit);
            devBridge = DevBridgeAdapterOptions.Discover(
                request.DevBridgePath,
                request.DevBridgeRootPath);
            rimError = RimErrorAdapterOptions.Discover(
                request.RimErrorPath,
                request.RimErrorLogPath,
                request.RimErrorStorePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or UnauthorizedAccessException)
        {
            return Blocked("configuration", "CONFIGURATION_INVALID");
        }

        DoctorRunResult? configurationFailure = ValidateConfiguration(
            rimContext,
            devBridge,
            rimError);
        if (configurationFailure is not null)
        {
            return configurationFailure;
        }

        ProbeResult context = await ProbeRimContextAsync(
                rimContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (!context.Ready)
        {
            WriteDiagnostic("rimctx", context.Code);
            return Blocked("rimctx", context.Code!, context.NextAction);
        }

        if (!IsPathDiscoverable(devBridge.CommandPath))
        {
            return Blocked("devbridge", "DEVBRIDGE_COMMAND_NOT_FOUND");
        }

        ProbeResult devBridgeProbe = await ProbeDevBridgeAsync(
                devBridge,
                transport,
                cancellationToken)
            .ConfigureAwait(false);
        if (!devBridgeProbe.Ready)
        {
            WriteDiagnostic("devbridge", devBridgeProbe.Code);
            return Blocked(
                "devbridge",
                devBridgeProbe.Code!,
                devBridgeProbe.NextAction,
                devBridgeProbe.IdentityMismatch);
        }

        ProbeResult projectProbe = await ProbeDevBridgeProjectAsync(
                devBridge,
                request.DevBridgeProject,
                transport,
                cancellationToken)
            .ConfigureAwait(false);
        if (!projectProbe.Ready)
        {
            WriteDiagnostic("devbridge", projectProbe.Code);
            return Blocked("devbridge", projectProbe.Code!, projectProbe.NextAction);
        }

        if (string.Equals(
                request.StackManifest.Manifest.RimBridge,
                "via-devbridge",
                StringComparison.Ordinal) &&
            string.Equals(
                devBridgeProbe.IntegrationStatus,
                "disabled",
                StringComparison.Ordinal))
        {
            return Blocked(
                "rimbridge",
                "RIMBRIDGE_OWNERSHIP_MISMATCH",
                DevBridgeDoctorNextAction);
        }

        string project = request.StackManifest.Manifest.Project;
        return new DoctorRunResult(
            0,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = RimTestDoctorSchema.Current,
                ["status"] = "ready",
                ["project"] = project,
                ["catalog"] = "ok",
                ["rimctx"] = "ok",
                ["devbridge"] = "ok",
                ["rimerror"] = "ok",
                ["rimbridge"] = devBridgeProbe.IntegrationStatus ?? "unknown",
                ["nextAction"] = AffectedNextAction
            });
    }

    private DoctorRunResult? ValidateConfiguration(
        RimContextAdapterOptions rimContext,
        DevBridgeAdapterOptions devBridge,
        RimErrorAdapterOptions rimError)
    {
        if (!Directory.Exists(rimContext.RootPath))
        {
            return Blocked("rimctx", "RIMCONTEXT_ROOT_NOT_FOUND");
        }

        if (!Directory.Exists(devBridge.RootPath))
        {
            return Blocked("devbridge", "DEVBRIDGE_ROOT_NOT_FOUND");
        }

        if (!HasExistingParent(rimContext.StorePath))
        {
            return Blocked("rimctx", "RIMCONTEXT_STORE_INVALID");
        }

        if (!HasExistingParent(rimError.LogPath) ||
            !HasExistingParent(rimError.StorePath))
        {
            return Blocked("rimerror", "RIMERROR_STORE_INVALID");
        }

        return null;
    }

    private Task<ProbeResult> ProbeRimContextAsync(
        RimContextAdapterOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = new RimContextService().Summary(
                new RimContextSummaryRequest(options.RootPath, options.StorePath),
                cancellationToken);
            return Task.FromResult(Success());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure("RIMCONTEXT_CANCELLED"));
        }
        catch (RimContextException exception)
        {
            return Task.FromResult(MapRimContextError(exception.Error.Code));
        }
        catch (Exception)
        {
            return Task.FromResult(Failure("RIMCONTEXT_RESPONSE_INVALID"));
        }
    }

    private async Task<ProbeResult> ProbeDevBridgeAsync(
        DevBridgeAdapterOptions options,
        IDevBridgeProcessTransport transport,
        CancellationToken cancellationToken)
    {
        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.RootPath,
            ["--root", options.RootPath, "doctor", "--json"],
            TimeSpan.FromSeconds(20),
            MaximumProbeStdoutBytes,
            MaximumProbeStderrBytes);
        DevBridgeProcessResult process = await ExecuteProbeAsync(
                transport,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (process.Cancelled)
        {
            return Failure("DEVBRIDGE_CANCELLED");
        }

        if (process.TimedOut)
        {
            return Failure("DEVBRIDGE_CLIENT_TIMEOUT", DevBridgeDoctorNextAction);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError))
        {
            return Failure("DEVBRIDGE_START_FAILED", DevBridgeDoctorNextAction);
        }

        if (process.StdoutTruncated || process.StderrTruncated ||
            string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failure("DEVBRIDGE_RESPONSE_INVALID", DevBridgeDoctorNextAction);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            JsonElement root = document.RootElement;
            if (!TryGetBoolean(root, "healthy", out bool healthy))
            {
                return Failure("DEVBRIDGE_RESPONSE_INVALID", DevBridgeDoctorNextAction);
            }
            string? integrationStatus = ReadRimBridgeStatus(root);
            if (healthy && process.ExitCode is not > 0)
            {
                return Success(integrationStatus);
            }

            string code = TryGetString(root, "errorCode", out string? errorCode)
                ? errorCode!
                : FirstDoctorFindingCode(root) ?? "DEVBRIDGE_REFUSAL";
            DevBridgeIdentityMismatch? identityMismatch =
                DevBridgeIdentityMismatchParser.Parse(root, options.RootPath, code);
            return Failure(
                code,
                DevBridgeDoctorNextAction,
                integrationStatus,
                identityMismatch);
        }
        catch (JsonException)
        {
            return Failure("DEVBRIDGE_RESPONSE_INVALID", DevBridgeDoctorNextAction);
        }
    }

    private async Task<ProbeResult> ProbeDevBridgeProjectAsync(
        DevBridgeAdapterOptions options,
        string project,
        IDevBridgeProcessTransport transport,
        CancellationToken cancellationToken)
    {
        var request = new DevBridgeProcessRequest(
            options.CommandPath,
            options.RootPath,
            [
                "--root",
                options.RootPath,
                "project",
                "resolve",
                project,
                "--json"
            ],
            TimeSpan.FromSeconds(15),
            MaximumProbeStdoutBytes,
            MaximumProbeStderrBytes);
        DevBridgeProcessResult process = await ExecuteProbeAsync(
                transport,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (process.Cancelled)
        {
            return Failure("DEVBRIDGE_CANCELLED");
        }

        if (process.TimedOut)
        {
            return Failure("DEVBRIDGE_PROJECT_TIMEOUT", DevBridgeDoctorNextAction);
        }

        if (!string.IsNullOrWhiteSpace(process.StartError) ||
            process.StdoutTruncated ||
            process.StderrTruncated ||
            string.IsNullOrWhiteSpace(process.Stdout))
        {
            return Failure("DEVBRIDGE_PROJECT_RESPONSE_INVALID", DevBridgeDoctorNextAction);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            JsonElement root = document.RootElement;
            if (!TryGetBoolean(root, "success", out bool success) ||
                !success ||
                process.ExitCode is > 0)
            {
                string code = TryGetString(root, "errorCode", out string? errorCode)
                    ? errorCode!
                    : "DEVBRIDGE_PROJECT_UNRESOLVED";
                return Failure(code, DevBridgeDoctorNextAction);
            }

            if (!root.TryGetProperty("projectResolution", out JsonElement resolution) ||
                resolution.ValueKind != JsonValueKind.Object ||
                !resolution.TryGetProperty("canonicalProjects", out JsonElement aliases) ||
                aliases.ValueKind != JsonValueKind.Array ||
                !aliases.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), project, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("DEVBRIDGE_PROJECT_RESPONSE_INVALID", DevBridgeDoctorNextAction);
            }

            return Success();
        }
        catch (JsonException)
        {
            return Failure("DEVBRIDGE_PROJECT_RESPONSE_INVALID", DevBridgeDoctorNextAction);
        }
    }

    private static async Task<DevBridgeProcessResult> ExecuteProbeAsync(
        IDevBridgeProcessTransport transport,
        DevBridgeProcessRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transport.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                Cancelled: true);
        }
        catch (Exception exception)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: exception.Message);
        }
    }

    private static ProbeResult MapRimContextError(string code) => code switch
    {
        "INDEX_NOT_FOUND" or "INDEX_MISSING" =>
            Failure("INDEX_MISSING", RimContextIndexNextAction),
        "INDEX_INCOMPATIBLE" or "ROOT_MISMATCH" or "CONTEXT_STALE" =>
            Failure("CONTEXT_STALE", RimContextIndexNextAction),
        _ => Failure(code)
    };

    private static string? ReadRimBridgeStatus(JsonElement root)
    {
        if (!root.TryGetProperty("rimBridge", out JsonElement bridge) ||
            bridge.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string mode = TryGetFirstString(
                bridge,
                out string? configuredMode,
                "configuredMode",
                "ConfiguredMode")
            ? configuredMode!
            : string.Empty;
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return "disabled";
        }

        string lifecycle = TryGetFirstString(
                bridge,
                out string? lifecycleState,
                "lifecycleState",
                "LifecycleState")
            ? lifecycleState!
            : string.Empty;
        if (lifecycle.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
            lifecycle.Equals("STALE", StringComparison.OrdinalIgnoreCase) ||
            lifecycle.Equals("NOT_INSTALLED", StringComparison.OrdinalIgnoreCase))
        {
            return "unavailable";
        }

        return "configured";
    }

    private static string? FirstDoctorFindingCode(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out JsonElement findings) ||
            findings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement finding in findings.EnumerateArray())
        {
            if (finding.ValueKind == JsonValueKind.Object &&
                TryGetString(finding, "severity", out string? severity) &&
                string.Equals(severity, "ERROR", StringComparison.OrdinalIgnoreCase) &&
                TryGetString(finding, "code", out string? code))
            {
                return code;
            }
        }

        return null;
    }

    private static bool TryGetFirstString(
        JsonElement parent,
        out string? value,
        params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (TryGetString(parent, propertyName, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsPathDiscoverable(string path)
    {
        if (Path.IsPathRooted(path) ||
            path.Contains(Path.DirectorySeparatorChar) ||
            path.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(path);
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        string[] extensions = ["", ".exe", ".cmd", ".bat", ".com"];
        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (string extension in extensions)
            {
                if (File.Exists(Path.Combine(directory, path + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasExistingParent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string? parent = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(parent) || Directory.Exists(parent);
    }

    private static string ProjectName(string rootPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(rootPath);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "workspace" : name;
    }

    private void WriteDiagnostic(string component, string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            stderr.WriteLine($"rimliaison doctor: {component} {code}");
        }
    }

    private static DoctorRunResult Blocked(
        string component,
        string code,
        string? nextAction = null,
        DevBridgeIdentityMismatch? identityMismatch = null)
    {
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = RimTestDoctorSchema.Current,
            ["status"] = "blocked",
            ["component"] = component,
            ["code"] = code
        };
        if (!string.IsNullOrWhiteSpace(nextAction))
        {
            output["nextAction"] = nextAction;
        }

        if (identityMismatch is not null)
        {
            output["identityMismatch"] = identityMismatch;
        }

        return new DoctorRunResult(3, output);
    }

    private static ProbeResult Success(string? integrationStatus = null) =>
        new(true, null, null, integrationStatus);

    private static ProbeResult Failure(
        string code,
        string? nextAction = null,
        string? integrationStatus = null,
        DevBridgeIdentityMismatch? identityMismatch = null) =>
        new(false, code, nextAction, integrationStatus, identityMismatch);

    private static string FirstErrorCode(
        IReadOnlyList<CatalogIssue> errors,
        string fallback) => errors.FirstOrDefault()?.Code ?? fallback;

    private static DoctorRunResult? ValidateFallback(
        CliRequest request,
        CatalogDocument catalog)
    {
        if (string.IsNullOrWhiteSpace(request.FallbackSuite))
        {
            return Blocked(
                "manifest",
                "STACK_MANIFEST_FALLBACK_SUITE_MISSING",
                FallbackNextAction(catalog));
        }

        CatalogSuite? suite = CatalogNavigator.FindSuite(
            catalog,
            request.FallbackSuite);
        if (suite is null)
        {
            return Blocked(
                "manifest",
                "STACK_MANIFEST_FALLBACK_SUITE_NOT_FOUND",
                FallbackNextAction(catalog));
        }

        if (CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count == 0)
        {
            return Blocked(
                "manifest",
                "STACK_MANIFEST_FALLBACK_SUITE_EMPTY",
                FallbackNextAction(catalog));
        }

        return null;
    }

    private static string MissingManifestConfigurationNextAction(CliRequest request)
    {
        var arguments = new List<string>
        {
            "rimliaison",
            "init",
            "--json"
        };
        if (string.IsNullOrWhiteSpace(request.DevBridgeProject))
        {
            arguments.Add("--devbridge-project");
            arguments.Add("<project>");
        }

        if (string.IsNullOrWhiteSpace(request.FallbackSuite))
        {
            arguments.Add("--fallback-suite");
            arguments.Add("<suite>");
        }

        return string.Join(' ', arguments);
    }

    private static string FallbackNextAction(CatalogDocument catalog)
    {
        string? suite = SelectFallbackSuite(catalog);
        return suite is null
            ? "rimliaison suites --json"
            : $"rimliaison init --json --fallback-suite {suite}";
    }

    private static string? SelectFallbackSuite(CatalogDocument catalog)
    {
        CatalogSuite? smoke = (catalog.Suites ?? [])
            .FirstOrDefault(suite => suite is not null &&
                string.Equals(suite.Id, "smoke", StringComparison.Ordinal) &&
                CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count > 0);
        if (smoke is not null)
        {
            return smoke.Id;
        }

        return (catalog.Suites ?? [])
            .Where(suite => suite is not null)
            .OrderBy(suite => suite.Id, StringComparer.Ordinal)
            .FirstOrDefault(suite =>
                CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count > 0)
            ?.Id;
    }

    private static string CatalogNextAction(CliRequest request, string code) =>
        code is "CATALOG_NOT_FOUND" or "CATALOG_PATH_INVALID"
            ? $"rimliaison init --json --manifest-only --force --catalog {CatalogArgument(request)}"
            : CatalogValidationNextAction(request);

    private static string CatalogValidationNextAction(CliRequest request) =>
        $"rimliaison validate --json --catalog {CatalogArgument(request)}";

    private static string RecipeListNextAction(CliRequest request) =>
        request.RecipeListPath is null
            ? "rimliaison validate --json"
            : $"rimliaison validate --json --recipes {RepositoryArgument(
                request.RecipeListPath,
                request.StackManifest.RepositoryRoot,
                "<recipes>")}";

    private static string CatalogArgument(CliRequest request) =>
        RepositoryArgument(
            request.CatalogPath,
            request.StackManifest.RepositoryRoot,
            "<catalog>");

    private static string RepositoryArgument(
        string path,
        string root,
        string placeholder)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (!Path.IsPathRooted(relative) &&
                relative is not ("." or "") &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith("../", StringComparison.Ordinal))
            {
                return relative;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
        }

        return placeholder;
    }

    private static bool TryGetString(
        JsonElement parent,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBoolean(
        JsonElement parent,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private sealed record ProbeResult(
        bool Ready,
        string? Code,
        string? NextAction,
        string? IntegrationStatus = null,
        DevBridgeIdentityMismatch? IdentityMismatch = null);
}

internal sealed record DoctorRunResult(
    int ExitCode,
    IReadOnlyDictionary<string, object?> Output);
