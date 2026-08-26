using System.Text.Json;
using System.Text.Json.Serialization;
using RimContext.Core;
using RimContext.Core.Configuration;
using RimContext.Core.Context;
using RimContext.Core.Contracts;
using RimContext.Core.Logging;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Semantics;
using RimContext.Core.Storage;
using RimContext.Core.Content;


namespace RimContext.Cli;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var logger = new TextWriterLogger(stderr);
        var command = GetCommandForError(args);
        var outputOptions = new JsonOutputOptions();
        try
        {
            var request = CliParser.Parse(args);
            command = request.Command;
            outputOptions = new JsonOutputOptions(request.Compact, request.MaxBytes, request.Human);
            var envelope = Execute(request, logger);
            if (request.Command == CliCommands.Context && envelope.Data is RimContextBundle bundle)
            {
                stdout.WriteLine(RimContextBundleJson.Serialize(
                    bundle,
                    verbose: request.Verbose || request.Human,
                    maxBytes: request.MaxBytes));
            }
            else
            {
                JsonOutput.Write(stdout, envelope, outputOptions);
            }

            return 0;
        }
        catch (RimContextException ex)
        {
            JsonOutput.Write(stdout, JsonOutput.Error(command, ex.Error), outputOptions);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Error($"{ex.GetType().Name}: {ex.Message}");
            var error = ErrorFactory.Internal("An unexpected internal error occurred.").Error;
            JsonOutput.Write(stdout, JsonOutput.Error(command, error), outputOptions);
            return 10;
        }
    }

    private static JsonEnvelope Execute(CliRequest request, ILogger logger) => request.Command switch
    {
        CliCommands.Help => JsonOutput.Success(CliCommands.Help, new
        {
            commands = CliCommands.All,
            usage = "rimctx <command> [selector ...] --json [--compact|--human] [--limit N] [--max-bytes N]"
        }),
        CliCommands.Version => JsonOutput.Success(CliCommands.Version, new VersionResponse(
            IndexConstants.ToolVersion,
            IndexConstants.SchemaVersionText)),
        CliCommands.Index => ExecuteIndex(request, logger),
        CliCommands.Summary => ExecuteSummary(request),
        CliCommands.Context => ExecuteContext(request),
        CliCommands.Find => ExecuteFind(request),
        CliCommands.Definition => ExecuteDefinition(request),
        CliCommands.Refs => ExecuteRefs(request),
        CliCommands.Harmony => ExecuteHarmony(request),
        CliCommands.File => ExecuteFile(request),
        CliCommands.Affected => ExecuteAffected(request),
        CliCommands.Content => ExecuteContent(request),
        _ when CliCommands.IsQuery(request.Command) => throw ErrorFactory.NotImplemented(request.Command),
        _ => throw ErrorFactory.InvalidArgument($"Unknown command '{request.Command}'.")
    };

    private static JsonEnvelope ExecuteIndex(CliRequest request, ILogger logger)
    {
        var result = new RimContextService(logger).Index(
            new RimContextIndexRequest(
                request.Root,
                request.Store,
                request.AssemblyRoots,
                request.Force));
        var data = new
        {
            files = new
            {
                scanned = result.Statistics.Scanned,
                added = result.Statistics.Added,
                changed = result.Statistics.Changed,
                removed = result.Statistics.Removed,
                unchanged = result.Statistics.Unchanged
            },
            duration_ms = result.DurationMilliseconds
        };
        var diagnostics = result.Diagnostics ?? [];
        if (diagnostics.Count == 0)
        {
            return JsonOutput.Success(CliCommands.Index, data);
        }

        return JsonOutput.Partial(
            CliCommands.Index,
            data,
            warnings: diagnostics
                .Select(diagnostic => new JsonWarning(
                    diagnostic.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        ? "CSHARP_PARSE"
                        : diagnostic.Code == "INDEX"
                            ? "XML_PARSE"
                            : diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Path))
                .ToArray());
    }

    private static JsonEnvelope ExecuteSummary(CliRequest request)
    {
        var summary = new RimContextService().Summary(
            new RimContextSummaryRequest(
                request.Root,
                request.Store,
                request.AssemblyRoots));
        return JsonOutput.Success(CliCommands.Summary, new SummaryResponse(
            summary.SchemaVersion,
            summary.ToolVersion,
            summary.WorkspaceId,
            summary.IndexedAtUtc,
            summary.Store,
            summary.FileCount,
            summary.EntityCount,
            summary.RelationCount,
            summary.Mods,
            summary.Projects,
            summary.SourceFiles,
            summary.XmlFiles,
            summary.Defs,
            summary.HarmonyPatches,
            new DiagnosticCounts(summary.DiagnosticErrors, summary.DiagnosticWarnings)));
    }

    private static JsonEnvelope ExecuteContext(CliRequest request)
    {
        var bundle = new RimContextService().ContextBundleAsync(
                new RimContextBundleRequest(
                    request.Root,
                    request.Store,
                    request.AssemblyRoots,
                    request.Verbose))
            .GetAwaiter()
            .GetResult();
        return new JsonEnvelope
        {
            SchemaVersion = RimContextBundleSchema.Current,
            Command = CliCommands.Context,
            Data = bundle
        };
    }

    private static JsonEnvelope ExecuteFind(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var page = engine.FindResultsPage(request.Subject!, request.Limit, request.Kind);
        return JsonOutput.Success(
            CliCommands.Find,
            results: page.Items,
            meta: new JsonQueryMetadata(page.Count, page.Truncated));
    }

    private static JsonEnvelope ExecuteDefinition(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var page = engine.FindDefinitionResultsPage(request.Subject!, request.Limit);
        if (page.Count == 0)
        {
            throw ErrorFactory.NotFound(request.Subject!);
        }

        return JsonOutput.Success(
            CliCommands.Definition,
            results: page.Items,
            meta: new JsonQueryMetadata(page.Count, page.Truncated));
    }

    private static JsonEnvelope ExecuteRefs(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var page = engine.FindReferencesPage(request.Subject!, request.Limit, request.Direction);
        if (!page.Found)
        {
            throw ErrorFactory.NotFound(request.Subject!);
        }

        return JsonOutput.Success(
            CliCommands.Refs,
            data: new
            {
                incoming = page.Result.Incoming,
                outgoing = page.Result.Outgoing
            },
            meta: new JsonQueryMetadata(page.Count, page.Truncated));
    }

    private static JsonEnvelope ExecuteFile(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var page = engine.FindFilesPage(request.Subject!, request.Limit);
        if (page.Count == 0)
        {
            throw ErrorFactory.NotFound(request.Subject!);
        }

        return JsonOutput.Success(
            CliCommands.File,
            results: page.Items,
            meta: new JsonQueryMetadata(page.Count, page.Truncated));
    }

    private static JsonEnvelope ExecuteHarmony(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var page = engine.FindHarmonyPage(request.Subject, request.File, request.Limit);
        return JsonOutput.Success(
            CliCommands.Harmony,
            results: page.Items,
            meta: new JsonQueryMetadata(page.Count, page.Truncated));
    }

    private static JsonEnvelope ExecuteAffected(CliRequest request)
    {
        var service = new RimContextService();
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        var result = service.Affected(
            new RimContextAffectedRequest(
                request.Inputs,
                configuration.RootPath,
                configuration.StorePath,
                request.AssemblyRoots,
                request.Depth,
                request.Limit));
        return JsonOutput.Success(
            CliCommands.Affected,
            data: result,
            meta: new JsonQueryMetadata(
                result.Direct.Count + result.Dependent.Count + result.RuntimeRisk.Count,
                result.Truncated));
    }

    private static JsonEnvelope ExecuteContent(CliRequest request)
    {
        ContentQueryResult result = new RimContextService().QueryContent(
            new ContentQueryRequest(
                request.Subject,
                request.Kind,
                request.GameplayRole,
                Limit: request.Limit,
                MaxBytes: request.MaxBytes ?? 65_536,
                IncludeFailures: request.IncludeFailures),
            request.Store);
        return JsonOutput.Success(CliCommands.Content, result);
    }

    private static string GetCommandForError(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return CliCommands.Help;
        }

        var candidate = args[0].Trim().ToLowerInvariant();
        return candidate.StartsWith("--", StringComparison.Ordinal) ? "unknown" : candidate;
    }

    private sealed record VersionResponse(string ToolVersion, string SchemaVersion);

    private sealed record SummaryResponse(
        int SchemaVersion,
        string ToolVersion,
        string WorkspaceId,
        string IndexedAtUtc,
        string Store,
        long FileCount,
        long EntityCount,
        long RelationCount,
        long Mods,
        long Projects,
        [property: JsonPropertyName("source_files")]
        long SourceFiles,
        [property: JsonPropertyName("xml_files")]
        long XmlFiles,
        long Defs,
        [property: JsonPropertyName("harmony_patches")]
        long HarmonyPatches,
        DiagnosticCounts Diagnostics);

    private sealed record DiagnosticCounts(long Error, long Warning);

    private sealed record DiagnosticEntry(string Severity);
}
