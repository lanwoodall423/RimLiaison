using RimLiaison.Stack;
using RimLiaison.RimDev;
using RimLiaison.Qualification;

namespace RimLiaison;

internal enum CliCommand
{
    List,
    ShowTest,
    Suites,
    SuiteRun,
    ShowSuite,
    Validate,
    RunTest,
    RecipeShow,
    RecipePlan,
    RecipeRun,
    Affected,
    GoldenPath,
    Capabilities,
    UiTargets,
    UiScreenshot,
    Doctor,
    SelfCheck,
    Preflight,
    Init,
    Context,
    PublishCheck,
    Benchmarks,
    Qualification,
    ToolchainPromotion,
    Content,
    RimDev
}

internal sealed record CliRequest(
    CliCommand Command,
    string? Id,
    RimDevOperation? RimDevOperation,
    string? RimDevRootPath,
    bool RimDevConfirm,
    bool RimDevJson,
    string CatalogPath,
    string? RecipeListPath,
    string? DevBridgePath,
    string? DevBridgeRootPath,
    string? DevBridgeProject,
    string? RimErrorPath,
    string? RimErrorLogPath,
    string? RimErrorStorePath,
    string? RimContextPath,
    string? RimContextRootPath,
    string? RimContextStorePath,
    string? FallbackSuite,
    int RimContextDepth,
    int RimContextLimit,
    string? CapabilityQuery,
    string? CapabilityCategory,
    string? CapabilityProvider,
    string? CapabilitySource,
    int CapabilityLimit,
    string? UiTarget,
    string? UiCellRect,
    string? UiViewport,
    int? UiViewportWidth,
    int? UiViewportHeight,
    string? UiLeaseId,
    bool UiInputCheck,
    bool Explain,
    bool ContextVerbose,
    bool WorkspaceAudit,
    string? AffectedBase,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyDictionary<string, string> DependencyFingerprints,
    bool RunSelected,
    bool FailFast,
    bool InitForce,
    bool InitManifestOnly,
    bool CatalogExplicit,
    bool FallbackSuiteExplicit,
    bool DevBridgeProjectExplicit,
    StackManifestResolution StackManifest,
    int QualificationRuns,
    bool QualificationRunsSpecified,
    string? QualificationOutputPath,
    string? PromotionPackagePath,
    bool HelpRequested,
    bool ExperimentalToolchain,
    string? ContentKind,
    string? ContentRole,
    string? ContentReuseSource,
    int ContentMaxBytes);

internal sealed class CliParseException : Exception
{
    public CliParseException(string message)
        : base(message)
    {
    }
}

internal static class CliParser
{
    public static CliRequest Parse(IReadOnlyList<string> args)
    {
        StackManifestResolution stackManifest = StackManifestResolver.Discover();
        string? catalogPath = null;
        bool catalogExplicit = false;
        string? recipeListPath = null;
        string? devBridgePath = null;
        string? devBridgeRootPath = null;
        string? devBridgeProject = null;
        bool devBridgeProjectExplicit = false;
        string? rimErrorPath = null;
        string? rimErrorLogPath = null;
        string? rimErrorStorePath = null;
        string? rimContextPath = null;
        string? rimContextRootPath = null;
        string? rimContextStorePath = null;
        string? configuredFallbackSuite =
            Environment.GetEnvironmentVariable("RIMTEST_FALLBACK_SUITE");
        string? fallbackSuite = string.IsNullOrWhiteSpace(configuredFallbackSuite)
            ? null
            : configuredFallbackSuite;
        bool fallbackSuiteExplicit = false;
        int rimContextDepth = 8;
        int rimContextLimit = 100;
        string? capabilityQuery = null;
        string? capabilityCategory = null;
        string? capabilityProvider = null;
        string? capabilitySource = null;
        int capabilityLimit = 20;
        string? uiTarget = null;
        string? uiCellRect = null;
        string? uiViewport = null;
        int? uiViewportWidth = null;
        int? uiViewportHeight = null;
        string? uiLeaseId = null;
        bool uiInputCheck = false;
        bool explain = false;
        bool contextVerbose = false;
        bool workspaceAudit = false;
        string? affectedBase = null;
        bool depthSpecified = false;
        bool limitSpecified = false;
        bool runSelected = false;
        bool failFast = false;
        bool initForce = false;
        bool initManifestOnly = false;
        RimDevOperation? rimDevOperation = null;
        string? rimDevRootPath = null;
        bool rimDevConfirm = false;
        bool rimDevJson = false;
        bool helpRequested = false;
        var dependencyFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        int qualificationRuns = 1;
        bool qualificationRunsSpecified = false;
        string? qualificationOutputPath = null;
        string? promotionPackagePath = null;
        string? contentKind = null;
        string? contentRole = null;
        string? contentReuseSource = null;
        int contentMaxBytes = 65_536;
        bool experimentalToolchain = false;
        var positionals = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--catalog":
                    catalogPath = ReadOptionValue(args, ref index, argument);
                    catalogExplicit = true;
                    break;
                case "--recipes":
                    recipeListPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--devbridge":
                    devBridgePath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--devbridge-root":
                    devBridgeRootPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--devbridge-project":
                    devBridgeProject = ReadOptionValue(args, ref index, argument);
                    devBridgeProjectExplicit = true;
                    break;
                case "--rimerror":
                    rimErrorPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--rimerror-log":
                    rimErrorLogPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--rimerror-store":
                    rimErrorStorePath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--rimcontext":
                    rimContextPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--rimcontext-root":
                    rimContextRootPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--rimcontext-store":
                    rimContextStorePath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--fallback-suite":
                    fallbackSuite = ReadOptionValue(args, ref index, argument);
                    fallbackSuiteExplicit = true;
                    break;
                case "--depth":
                    depthSpecified = true;
                    rimContextDepth = ReadPositiveBoundedInt(
                        args,
                        ref index,
                        argument,
                        1,
                        8);
                    break;
                case "--limit":
                    limitSpecified = true;
                    rimContextLimit = ReadPositiveBoundedInt(
                        args,
                        ref index,
                        argument,
                        1,
                        100);
                    capabilityLimit = rimContextLimit;
                    break;
                case "--query":
                    capabilityQuery = ReadOptionValue(args, ref index, argument);
                    break;
                case "--category":
                    capabilityCategory = ReadOptionValue(args, ref index, argument);
                    break;
                case "--provider":
                case "--provider-id":
                    capabilityProvider = ReadOptionValue(args, ref index, argument);
                    break;
                case "--source":
                    capabilitySource = ReadOptionValue(args, ref index, argument);
                    break;
                case "--target":
                    uiTarget = ReadOptionValue(args, ref index, argument);
                    break;
                case "--cell-rect":
                    uiCellRect = ReadOptionValue(args, ref index, argument);
                    break;
                case "--viewport":
                    uiViewport = ReadOptionValue(args, ref index, argument);
                    break;
                case "--viewport-width":
                    uiViewportWidth = ReadPositiveBoundedInt(
                        args, ref index, argument, 320, 7680);
                    break;
                case "--viewport-height":
                    uiViewportHeight = ReadPositiveBoundedInt(
                        args, ref index, argument, 240, 4320);
                    break;
                case "--lease":
                    uiLeaseId = ReadOptionValue(args, ref index, argument);
                    break;
                case "--content-kind":
                    contentKind = ReadOptionValue(args, ref index, argument);
                    break;
                case "--content-role":
                    contentRole = ReadOptionValue(args, ref index, argument);
                    break;
                case "--content-reuse-source":
                    contentReuseSource = ReadOptionValue(args, ref index, argument);
                    break;
                case "--workspace":
                    workspaceAudit = true;
                    break;
                case "--verbose":
                    contextVerbose = true;
                    break;
                case "--base":
                    affectedBase = ReadOptionValue(args, ref index, argument);
                    if (affectedBase.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliParseException("Option --base must be a Git reference, not an option.");
                    }

                    break;
                case "--dependency-fingerprint":
                    string dependency = ReadOptionValue(args, ref index, argument);
                    int separator = dependency.IndexOf('=');
                    if (separator <= 0 || separator == dependency.Length - 1)
                    {
                        throw new CliParseException("Option --dependency-fingerprint must use NAME=VALUE.");
                    }

                    dependencyFingerprints[dependency[..separator]] = dependency[(separator + 1)..];
                    break;
                case "--run":
                    runSelected = true;
                    break;
                case "--fail-fast":
                    failFast = true;
                    break;
                case "--force":
                case "--update":
                    initForce = true;
                    break;
                case "--manifest-only":
                    initManifestOnly = true;
                    break;
                case "--root":
                    rimDevRootPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--yes":
                case "--confirm":
                    rimDevConfirm = true;
                    break;
                case "--runs":
                    string runsValue = ReadOptionValue(args, ref index, argument);
                    qualificationRunsSpecified = true;
                    if (!int.TryParse(runsValue, out qualificationRuns) || qualificationRuns < 1)
                    {
                        throw new CliParseException("Option --runs must be a positive integer.");
                    }
                    break;
                case "--qualification-output":
                    qualificationOutputPath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--promotion-package":
                    promotionPackagePath = ReadOptionValue(args, ref index, argument);
                    break;
                case "--max-bytes":
                    contentMaxBytes = ReadPositiveBoundedInt(
                        args, ref index, argument, 256, 1_048_576);
                    break;
                case "--json":
                    // RimLiaison's machine-readable contract is the default;
                    // accept the explicit agent-facing spelling as a no-op.
                    rimDevJson = true;
                    break;
                case "--experimental":
                    experimentalToolchain = true;
                    break;
                case "--help":
                case "-h":
                    helpRequested = true;
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliParseException($"Unknown option: {argument}.");
                    }

                    positionals.Add(argument);
                    break;
            }
        }

        if (helpRequested)
        {
            return new CliRequest(
                CliCommand.List,
                null,
                null,
                rimDevRootPath,
                rimDevConfirm,
                rimDevJson,
                catalogPath ??
                    (stackManifest.Manifest is not null
                        ? StackManifestResolver.CatalogPath(stackManifest)
                        : ResolveDefaultCatalogPath()),
                recipeListPath,
                devBridgePath,
                devBridgeRootPath,
                devBridgeProject ??
                    Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_PROJECT") ??
                    stackManifest.Manifest?.DevBridgeProject,
                rimErrorPath,
                rimErrorLogPath,
                rimErrorStorePath,
                rimContextPath,
                rimContextRootPath,
                rimContextStorePath,
                fallbackSuite,
                rimContextDepth,
                rimContextLimit,
                capabilityQuery,
                capabilityCategory,
                capabilityProvider,
                capabilitySource,
                capabilityLimit,
                uiTarget,
                uiCellRect,
                uiViewport,
                uiViewportWidth,
                uiViewportHeight,
                uiLeaseId,
                uiInputCheck,
                explain,
                contextVerbose,
                workspaceAudit,
                null,
                [],
                dependencyFingerprints,
                false,
                false,
                initForce,
                initManifestOnly,
                catalogExplicit,
                fallbackSuiteExplicit,
                devBridgeProjectExplicit,
                stackManifest,
                qualificationRuns,
                qualificationRunsSpecified,
                qualificationOutputPath,
                promotionPackagePath,
                true,
                experimentalToolchain,
                contentKind,
                contentRole,
                contentReuseSource,
                contentMaxBytes);
        }

        if (positionals.Count == 0)
        {
            throw new CliParseException("A command is required.");
        }

        CliCommand command;
        string? id = null;
        switch (positionals[0].ToLowerInvariant())
        {
            case "list" when positionals.Count == 1:
                command = CliCommand.List;
                break;
            case "suites" when positionals.Count == 1:
                command = CliCommand.Suites;
                break;
            case "validate" when positionals.Count == 1:
                command = CliCommand.Validate;
                break;
            case "show" when positionals.Count == 2:
                command = CliCommand.ShowTest;
                id = positionals[1];
                break;
            case "run" when positionals.Count == 2:
                command = CliCommand.RunTest;
                id = positionals[1];
                break;
            case "affected" when positionals.Count >= 1:
                command = CliCommand.Affected;
                break;
            case "golden-path" when positionals.Count == 1:
            case "develop" when positionals.Count == 1:
                command = CliCommand.GoldenPath;
                break;
            case "preflight" when positionals.Count == 1:
                command = CliCommand.Preflight;
                break;
            case "doctor" when positionals.Count == 1:
                command = CliCommand.Doctor;
                break;
            case "self-check" when positionals.Count == 1:
                command = CliCommand.SelfCheck;
                break;
            case "context" when positionals.Count == 1:
                command = CliCommand.Context;
                break;
            case "content" when positionals.Count == 1:
                command = CliCommand.Content;
                break;
            case "content" when positionals.Count == 2 &&
                string.Equals(positionals[1], "query", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.Content;
                break;
            case "content" when positionals.Count == 3 &&
                string.Equals(positionals[1], "query", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.Content;
                id = positionals[2];
                break;
            case "publish" when positionals.Count == 2 &&
                string.Equals(positionals[1], "check", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.PublishCheck;
                break;
            case "benchmarks" when positionals.Count == 1:
                command = CliCommand.Benchmarks;
                break;
            case "rimdev" when positionals.Count == 1:
                command = CliCommand.RimDev;
                rimDevOperation = RimDevOperation.Menu;
                break;
            case "rimdev" when positionals.Count == 2:
                command = CliCommand.RimDev;
                rimDevOperation = ParseRimDevOperation(positionals[1]);
                break;
            case "capabilities" when positionals.Count == 1:
                command = CliCommand.Capabilities;
                break;
            case "ui" when positionals.Count == 2 &&
                string.Equals(positionals[1], "targets", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.UiTargets;
                break;
            case "ui" when positionals.Count == 2 &&
                string.Equals(positionals[1], "screenshot", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.UiScreenshot;
                break;
            case "init" when positionals.Count == 1:
                command = CliCommand.Init;
                break;
            case "suite" when positionals.Count == 3 &&
                string.Equals(positionals[1], "show", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.ShowSuite;
                id = positionals[2];
                break;
            case "suite" when positionals.Count == 3 &&
                string.Equals(positionals[1], "run", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.SuiteRun;
                id = positionals[2];
                break;
            case "recipe" when positionals.Count == 3:
                command = ParseRecipeCommand(positionals[1]);
                id = positionals[2];
                break;
            case "test" when positionals.Count == 4 &&
                string.Equals(positionals[1], "recipe", StringComparison.OrdinalIgnoreCase):
                command = ParseRecipeCommand(positionals[2]);
                id = positionals[3];
                break;
            case "qualification" when positionals.Count is 1 or 2 &&
                (positionals.Count == 1 ||
                    string.Equals(positionals[1], "burn-in", StringComparison.OrdinalIgnoreCase)):
                command = CliCommand.Qualification;
                id = positionals.Count == 2 ? "burn-in" : "qualification";
                break;
            case "qualification" when positionals.Count == 2 &&
                string.Equals(positionals[1], "promote", StringComparison.OrdinalIgnoreCase):
                command = CliCommand.ToolchainPromotion;
                id = "promote";
                break;
            case "list":
            case "show":
            case "suites":
            case "suite":
            case "validate":
            case "run":
            case "recipe":
            case "test":
            case "affected":
            case "golden-path":
            case "develop":
            case "preflight":
            case "doctor":
            case "self-check":
            case "context":
            case "publish":
            case "benchmarks":
            case "rimdev":
            case "capabilities":
            case "ui":
            case "init":
            case "qualification":
            case "content":
                throw new CliParseException("The command arguments are invalid.");
            default:
                throw new CliParseException($"Unknown command: {positionals[0]}.");
        }
        if (command == CliCommand.Qualification &&
            string.Equals(id, "burn-in", StringComparison.OrdinalIgnoreCase))
        {
            if (qualificationRunsSpecified &&
                qualificationRuns != QualificationProfiles.PromotionBurnInRuns)
            {
                throw new CliParseException(
                    $"qualification burn-in requires exactly {QualificationProfiles.PromotionBurnInRuns} runs");
            }

            qualificationRuns = QualificationProfiles.PromotionBurnInRuns;
        }


        if (fallbackSuite is null &&
            command is (CliCommand.Affected or CliCommand.GoldenPath or CliCommand.Preflight or
                CliCommand.Doctor or CliCommand.Context or CliCommand.Init))
        {
            fallbackSuite = stackManifest.Manifest?.FallbackSuite;
        }

        if (command is not (CliCommand.Affected or CliCommand.Doctor or
                CliCommand.Capabilities or CliCommand.Init or CliCommand.Content) &&
            (explain || depthSpecified || limitSpecified || runSelected))
        {
            throw new CliParseException(
                "RimContext selection options are only valid for affected or content.");
        }

        if (affectedBase is not null &&
            command is not (CliCommand.Affected or CliCommand.PublishCheck))
        {
            throw new CliParseException(
                "Option --base is only valid for affected or publish check.");
        }

        if (fallbackSuiteExplicit &&
            command is not (CliCommand.Affected or CliCommand.Doctor or CliCommand.Context or CliCommand.Init))
        {
            throw new CliParseException(
                "Option --fallback-suite is only valid for affected, doctor, or context.");
        }

        if (contextVerbose && command != CliCommand.Context)
        {
            throw new CliParseException("Option --verbose is only valid for context.");
        }

        if (failFast && command is not (CliCommand.Affected or CliCommand.SuiteRun))
        {
            throw new CliParseException(
                "Option --fail-fast is only valid for affected --run or suite run.");
        }

        if (failFast && command == CliCommand.Affected && !runSelected)
        {
            throw new CliParseException(
                "Option --fail-fast requires --run with affected.");
        }

        if (command != CliCommand.Capabilities &&
            (capabilityQuery is not null ||
             capabilityCategory is not null ||
             capabilityProvider is not null ||
             capabilitySource is not null))
        {
            throw new CliParseException(
                "Capability discovery filters are only valid for capabilities.");
        }

        if (command is not (CliCommand.Capabilities or CliCommand.UiTargets or CliCommand.UiScreenshot) &&
            (uiViewport is not null || uiViewportWidth.HasValue || uiViewportHeight.HasValue ||
             uiLeaseId is not null || uiInputCheck))
        {
            throw new CliParseException(
                "Viewport and lease options are only valid for capabilities or UI commands.");
        }

        if (command == CliCommand.Capabilities &&
            (fallbackSuiteExplicit ||
             explain ||
             depthSpecified ||
             affectedBase is not null ||
             runSelected))
        {
            throw new CliParseException(
                "RimContext selection options are not valid for capabilities.");
        }

        if (command is not (CliCommand.UiTargets or CliCommand.UiScreenshot) &&
            (uiTarget is not null || uiCellRect is not null))
        {
            throw new CliParseException(
                "UI target options are only valid for ui screenshot.");
        }

        if (command == CliCommand.UiTargets &&
            (uiTarget is not null || uiCellRect is not null || uiViewport is not null ||
             uiViewportWidth.HasValue || uiViewportHeight.HasValue ||
             uiInputCheck))
        {
            throw new CliParseException(
                "ui targets does not accept a target or cell rectangle.");
        }

        if (command == CliCommand.UiScreenshot && uiViewport is null &&
            (uiViewportWidth.HasValue || uiViewportHeight.HasValue))
        {
            throw new CliParseException(
                "--viewport-width and --viewport-height require --viewport.");
        }

        if (command == CliCommand.UiScreenshot && uiViewport is not null)
        {
            string viewportKind = uiViewport.Trim().ToLowerInvariant();
            bool explicitViewport = string.Equals(viewportKind, "explicit", StringComparison.Ordinal);
            if (!explicitViewport && (uiViewportWidth.HasValue || uiViewportHeight.HasValue))
            {
                throw new CliParseException(
                    "Custom dimensions are only valid with --viewport explicit.");
            }

            if (explicitViewport && (!uiViewportWidth.HasValue || !uiViewportHeight.HasValue))
            {
                throw new CliParseException(
                    "--viewport explicit requires --viewport-width and --viewport-height.");
            }
        }

        if (command == CliCommand.UiScreenshot &&
            ((uiTarget is null) == (uiCellRect is null)))
        {
            throw new CliParseException(
                "ui screenshot requires exactly one of --target or --cell-rect.");
        }

        if (initForce && command != CliCommand.Init)
        {
            throw new CliParseException("--force/--update is only valid for init.");
        }

        if (initManifestOnly && command != CliCommand.Init)
        {
            throw new CliParseException("--manifest-only is only valid for init.");
        }

        if (contentMaxBytes != 65_536 && command != CliCommand.Content)
        {
            throw new CliParseException("--max-bytes is only valid for content.");
        }

        if ((contentKind is not null || contentRole is not null || contentReuseSource is not null) &&
            command is not (CliCommand.Affected or CliCommand.GoldenPath or CliCommand.Content))
        {
            throw new CliParseException(
                "Content blueprint options are only valid for affected, golden-path, or content.");
        }

        if (command != CliCommand.RimDev &&
            (rimDevRootPath is not null || rimDevConfirm))
        {
            throw new CliParseException("--root, --yes, and --confirm are only valid for rimdev.");
        }

        if (command == CliCommand.RimDev && rimDevOperation is null)
        {
            throw new CliParseException("rimdev requires a command such as status, all, deploy, push, merge, or help.");
        }

        if (command == CliCommand.RimDev && rimDevConfirm && rimDevOperation != RimDevOperation.Merge)
        {
            throw new CliParseException("--yes/--confirm is only valid for rimdev merge.");
        }

        if (command is not (CliCommand.Affected or CliCommand.Doctor or CliCommand.Context or CliCommand.Content) &&
            (rimContextPath is not null ||
             rimContextRootPath is not null ||
             rimContextStorePath is not null))
        {
            throw new CliParseException(
                "RimContext configuration options are only valid for affected, doctor, context, or content.");
        }

        if (catalogPath is null)
        {
            catalogPath = stackManifest.Manifest is not null
                ? StackManifestResolver.CatalogPath(stackManifest)
                : ResolveDefaultCatalogPath();
        }

        if (experimentalToolchain &&
            string.Equals(
                stackManifest.Manifest?.Workload,
                "production",
                StringComparison.OrdinalIgnoreCase) &&
            command is not (CliCommand.Qualification or CliCommand.ToolchainPromotion))
        {
            throw new CliParseException(
                "--experimental is not valid for a production workload.");
        }

        if (devBridgeProject is null)
        {
            devBridgeProject = Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_PROJECT") ??
                stackManifest.Manifest?.DevBridgeProject;
        }

        if (rimContextRootPath is null && stackManifest.Manifest is not null &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RIMTEST_RIMCONTEXT_ROOT")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RIMCONTEXT_ROOT")))
        {
            rimContextRootPath = stackManifest.RepositoryRoot;
        }

        return new CliRequest(
            command,
            id,
            rimDevOperation,
            rimDevRootPath,
            rimDevConfirm,
            rimDevJson,
            catalogPath,
            recipeListPath,
            devBridgePath,
            devBridgeRootPath,
            devBridgeProject,
            rimErrorPath,
            rimErrorLogPath,
            rimErrorStorePath,
            rimContextPath,
            rimContextRootPath,
            rimContextStorePath,
            fallbackSuite,
            rimContextDepth,
            rimContextLimit,
            capabilityQuery,
            capabilityCategory,
            capabilityProvider,
            capabilitySource,
            capabilityLimit,
            uiTarget,
            uiCellRect,
            uiViewport,
            uiViewportWidth,
            uiViewportHeight,
            uiLeaseId,
            uiInputCheck,
            explain,
            contextVerbose,
            workspaceAudit,
            affectedBase,
            positionals.Skip(1).ToArray(),
            dependencyFingerprints,
            runSelected,
            failFast,
            initForce,
            initManifestOnly,
            catalogExplicit,
            fallbackSuiteExplicit,
            devBridgeProjectExplicit,
            stackManifest,
            qualificationRuns,
            qualificationRunsSpecified,
            qualificationOutputPath,
            promotionPackagePath,
            false,
            experimentalToolchain,
            contentKind,
            contentRole,
            contentReuseSource,
            contentMaxBytes);
    }

    public static void WriteHelp(TextWriter stdout)
    {
        var help = new
        {
            progressiveDisclosure =
                "Canonical mod path: rimliaison preflight, edit, rimliaison golden-path --json. Supporting-tool details remain in structured diagnostics.",
            commands = new[]
            {
                "list",
                "show <test>",
                "suites",
                "suite show <suite>",
                "suite run <suite>",
                "validate",
                "run <test>",
                "affected [<changed-path> ...]",
                "golden-path (or develop)",
                "self-check",
                "preflight",
                "capabilities",
                "ui targets",
                "ui screenshot",
                "doctor",
                "context",
                "content query [<selector>]",
                "publish check",
                "qualification [burn-in]",
                "qualification promote",
                "init",
                "recipe show <recipe>",
                "recipe plan <recipe>",
                "recipe run <recipe>"
            },
            options = new[]
            {
                "--catalog <path>",
                "--recipes <devbridge-recipe-list.json>",
                "--devbridge <DevBridge.cmd>",
                "--devbridge-root <DevBridge2-root>",
                "--devbridge-project <alias>",
                "--rimerror <rimerror command>",
                "--rimerror-log <log path> (fallback only)",
                "--rimerror-store <RimError store> (fallback only)",
                "--rimcontext <rimctx command>",
                "--rimcontext-root <workspace root>",
                "--rimcontext-store <RimContext store>",
                "--fallback-suite <suite>",
                "--depth <1..8>",
                "--max-bytes <256..1048576> (with content)",
                "--content-kind <kind>",
                "--promotion-package <qualified-toolchain-package.json>",
                "--content-role <role>",
                "--content-reuse-source <source>",
                "--limit <1..100>",
                "--query <text> (with capabilities)",
                "--category <category> (with capabilities)",
                "--provider <provider> (with capabilities)",
                "--source <source> (with capabilities)",
                "--target <target-id> (with ui screenshot)",
                "--cell-rect <x,z,width,height> (with ui screenshot)",
                "--viewport <current|wide|narrow|explicit> (with ui screenshot)",
                "--viewport-width <320..7680> (with --viewport explicit)",
                "--viewport-height <240..4320> (with --viewport explicit)",
                "--lease <lease-id> (with transactional ui screenshot; omit to acquire one)",
                "--check-input (with ui screenshot; uses semantic live input state when registered)",
                "--explain",
                "--base <git-ref> (with affected or publish check)",
                "--experimental (qualification/tooling mode; never production)",
                "--dependency-fingerprint <name=value> (with affected validation)",
                "--json (default output)",
                "--run (with affected)",
                "--runs <positive integer> (with qualification)",
                "--qualification-output <path> (with qualification)",
                "--fail-fast (with affected --run or suite run)",
                "--force/--update (with init)",
                "--manifest-only (with init)",
                "--root <workspace-root> (with rimdev)",
                "--yes/--confirm (with rimdev merge)"
            }
        };

        stdout.WriteLine(Catalog.CatalogJsonFacade.Serialize(help));
    }

    private static CliCommand ParseRecipeCommand(string operation)
    {
        return operation.ToLowerInvariant() switch
        {
            "show" => CliCommand.RecipeShow,
            "plan" => CliCommand.RecipePlan,
            "run" => CliCommand.RecipeRun,
            _ => throw new CliParseException(
                $"Unknown recipe operation: {operation}.")
        };
    }

    private static RimDevOperation ParseRimDevOperation(string operation)
    {
        return operation.Trim().ToLowerInvariant() switch
        {
            "status" => RimDevOperation.Status,
            "sync" => RimDevOperation.Sync,
            "build" => RimDevOperation.Build,
            "test" => RimDevOperation.Test,
            "deploy" => RimDevOperation.Deploy,
            "push" => RimDevOperation.Push,
            "merge" => RimDevOperation.Merge,
            "all" => RimDevOperation.All,
            "help" => RimDevOperation.Help,
            _ => throw new CliParseException(
                $"Unknown rimdev operation: {operation}. Use status, sync, build, test, deploy, push, merge, all, or help.")
        };
    }

    private static string ReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new CliParseException($"Option {option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ReadPositiveBoundedInt(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        int minimum,
        int maximum)
    {
        string value = ReadOptionValue(args, ref index, option);
        if (!int.TryParse(value, out int parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new CliParseException(
                $"Option {option} must be an integer from {minimum} through {maximum}.");
        }

        return parsed;
    }

    private static string ResolveDefaultCatalogPath()
    {
        string preferred = Path.Combine(
            Environment.CurrentDirectory,
            "TestCatalog",
            "rimtest.catalog.json");
        return File.Exists(preferred)
            ? preferred
            : Path.Combine(Environment.CurrentDirectory, "rimtest.catalog.json");
    }
}
