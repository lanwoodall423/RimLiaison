using RimLiaison.Stack;

namespace RimLiaison;

internal enum CliCommand
{
    List,
    ShowTest,
    Suites,
    ShowSuite,
    Validate,
    RunTest,
    RecipeShow,
    RecipePlan,
    RecipeRun,
    Affected,
    SuiteRun,
    Capabilities,
    UiTargets,
    UiScreenshot,
    Doctor,
    Init
}

internal sealed record CliRequest(
    CliCommand Command,
    string? Id,
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
    bool Explain,
    string? AffectedBase,
    IReadOnlyList<string> ChangedPaths,
    bool RunSelected,
    bool FailFast,
    bool InitForce,
    bool InitManifestOnly,
    bool CatalogExplicit,
    bool FallbackSuiteExplicit,
    bool DevBridgeProjectExplicit,
    StackManifestResolution StackManifest,
    bool HelpRequested);

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
        bool explain = false;
        string? affectedBase = null;
        bool depthSpecified = false;
        bool limitSpecified = false;
        bool runSelected = false;
        bool failFast = false;
        bool initForce = false;
        bool initManifestOnly = false;
        bool helpRequested = false;
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
                case "--explain":
                    explain = true;
                    break;
                case "--base":
                    affectedBase = ReadOptionValue(args, ref index, argument);
                    if (affectedBase.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliParseException("Option --base must be a Git reference, not an option.");
                    }

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
                case "--json":
                    // RimLiaison's machine-readable contract is the default;
                    // accept the explicit agent-facing spelling as a no-op.
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
                explain,
                null,
                [],
                false,
                false,
                initForce,
                initManifestOnly,
                catalogExplicit,
                fallbackSuiteExplicit,
                devBridgeProjectExplicit,
                stackManifest,
                true);
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
            case "doctor" when positionals.Count == 1:
                command = CliCommand.Doctor;
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
            case "list":
            case "show":
            case "suites":
            case "suite":
            case "validate":
            case "run":
            case "recipe":
            case "test":
            case "affected":
            case "doctor":
            case "capabilities":
            case "ui":
            case "init":
                throw new CliParseException("The command arguments are invalid.");
            default:
                throw new CliParseException($"Unknown command: {positionals[0]}.");
        }

        if (fallbackSuite is null &&
            command is (CliCommand.Affected or CliCommand.Doctor or CliCommand.Init))
        {
            fallbackSuite = stackManifest.Manifest?.FallbackSuite;
        }

        if (command is not (CliCommand.Affected or CliCommand.Doctor or CliCommand.Capabilities or CliCommand.Init) &&
            (fallbackSuiteExplicit ||
             explain ||
             depthSpecified ||
             limitSpecified ||
             affectedBase is not null ||
             runSelected))
        {
            throw new CliParseException(
                "RimContext selection options are only valid for affected.");
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
            (uiTarget is not null || uiCellRect is not null))
        {
            throw new CliParseException(
                "ui targets does not accept a target or cell rectangle.");
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

        if (command is not (CliCommand.Affected or CliCommand.Doctor) &&
            (rimContextPath is not null ||
             rimContextRootPath is not null ||
             rimContextStorePath is not null))
        {
            throw new CliParseException(
                "RimContext configuration options are only valid for affected or doctor.");
        }

        if (catalogPath is null)
        {
            catalogPath = stackManifest.Manifest is not null
                ? StackManifestResolver.CatalogPath(stackManifest)
                : ResolveDefaultCatalogPath();
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
            explain,
            affectedBase,
            positionals
                .Skip(1)
                .ToArray(),
            runSelected,
            failFast,
            initForce,
            initManifestOnly,
            catalogExplicit,
            fallbackSuiteExplicit,
            devBridgeProjectExplicit,
            stackManifest,
            false);
    }

    public static void WriteHelp(TextWriter stdout)
    {
        var help = new
        {
            progressiveDisclosure =
                "Canonical loop: edit, rimliaison affected --run --fail-fast --json, inspect the result, fix immediately on failure, and repeat; once stable, run rimliaison affected --run --json for complete validation. Run rimliaison doctor --json only when readiness is unknown; affected source changes automatically build, hash, deploy when needed, establish a DevBridge generation, prove artifact freshness, and then run selected recipes. Failed recipes automatically use DevBridge's bounded generation-scoped diagnostics; do not read Player.log directly. Use rimliaison capabilities --json for live-game authoring and ui targets / ui screenshot for visual validation.",
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
                "capabilities",
                "ui targets",
                "ui screenshot",
                "doctor",
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
                "--limit <1..100>",
                "--query <text> (with capabilities)",
                "--category <category> (with capabilities)",
                "--provider <provider> (with capabilities)",
                "--source <source> (with capabilities)",
                "--target <target-id> (with ui screenshot)",
                "--cell-rect <x,z,width,height> (with ui screenshot)",
                "--explain",
                "--base <git-ref> (with affected)",
                "--json (default output)",
                "--run (with affected)",
                "--fail-fast (with affected --run or suite run)",
                "--force/--update (with init)",
                "--manifest-only (with init)"
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
