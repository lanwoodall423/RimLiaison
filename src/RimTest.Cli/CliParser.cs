using RimTest.Stack;

namespace RimTest;

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
    bool Explain,
    string? AffectedBase,
    IReadOnlyList<string> ChangedPaths,
    bool RunSelected,
    bool InitForce,
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
        bool explain = false;
        string? affectedBase = null;
        bool depthSpecified = false;
        bool limitSpecified = false;
        bool runSelected = false;
        bool initForce = false;
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
                case "--force":
                case "--update":
                    initForce = true;
                    break;
                case "--json":
                    // RimTest's machine-readable contract is the default;
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
                explain,
                null,
                [],
                false,
                initForce,
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
            case "init":
                throw new CliParseException("The command arguments are invalid.");
            default:
                throw new CliParseException($"Unknown command: {positionals[0]}.");
        }

        if (fallbackSuite is null && command is CliCommand.Affected or CliCommand.Init)
        {
            fallbackSuite = stackManifest.Manifest?.FallbackSuite;
        }

        if (command is not (CliCommand.Affected or CliCommand.Init) &&
            (fallbackSuite is not null ||
             explain ||
             depthSpecified ||
             limitSpecified ||
             affectedBase is not null ||
             runSelected))
        {
            throw new CliParseException(
                "RimContext selection options are only valid for affected.");
        }

        if (initForce && command != CliCommand.Init)
        {
            throw new CliParseException("--force/--update is only valid for init.");
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
            explain,
            affectedBase,
            positionals
                .Skip(1)
                .ToArray(),
            runSelected,
            initForce,
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
                "--rimerror-log <log path>",
                "--rimerror-store <RimError store>",
                "--rimcontext <rimctx command>",
                "--rimcontext-root <workspace root>",
                "--rimcontext-store <RimContext store>",
                "--fallback-suite <suite>",
                "--depth <1..8>",
                "--limit <1..100>",
                "--explain",
                "--base <git-ref> (with affected)",
                "--json (default output)",
                "--run (with affected)",
                "--force/--update (with init)"
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
