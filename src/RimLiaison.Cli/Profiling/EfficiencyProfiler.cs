using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace RimLiaison.Profiling;

/// <summary>
/// The single always-on local execution profiler used by RimLiaison. It
/// samples every activity, aggregates completed activities in memory, and
/// writes one bounded sidecar at process shutdown. It intentionally has no
/// exporter, collector, background worker, or configurable profiling mode.
/// </summary>
public sealed class EfficiencyProfiler : IDisposable
{
    public const string ActivitySourceName = "RimLiaison.Efficiency";
    public const string SchemaVersion = "rimliaison-efficiency-profile/v1";
    public const int MaximumProfileBytes = 16 * 1024;
    public const int MaximumRetainedProfiles = 20;
    public const int MaximumRetentionBytes = 256 * 1024;

    private static readonly AsyncLocal<EfficiencyProfiler?> CurrentSlot = new();

    private readonly EfficiencyActivityProcessor processor;
    private readonly ActivitySource source;
    private readonly TracerProvider? provider;
    private readonly Activity? invocation;
    private readonly EfficiencyProfiler? previous;
    private readonly long startedTimestamp;
    private readonly DateTime startedUtc;
    private readonly string profileDirectory;
    private readonly string runId;
    private string command = "unknown";
    private string outcome = "unknown";
    private int exitCode = -1;
    private int completed;
    private int disposed;

    private EfficiencyProfiler(string? profileDirectory)
    {
        previous = CurrentSlot.Value;
        CurrentSlot.Value = this;
        startedTimestamp = Stopwatch.GetTimestamp();
        startedUtc = DateTime.UtcNow;
        runId = "rp-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N");
        this.profileDirectory = ResolveProfileDirectory(profileDirectory);
        source = new ActivitySource(ActivitySourceName);
        processor = new EfficiencyActivityProcessor();

        try
        {
            provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ActivitySourceName)
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(processor)
                .Build();
        }
        catch
        {
            // Profiling must never alter the wrapped command. The processor
            // remains available for a best-effort empty profile if SDK setup
            // is unavailable in a constrained host.
            provider = null;
        }

        try
        {
            invocation = StartActivity("rimliaison.invoke", "cli", "process");
        }
        catch
        {
            invocation = null;
        }
    }

    public static EfficiencyProfiler Current =>
        CurrentSlot.Value ?? throw new InvalidOperationException(
            "No RimLiaison efficiency profiler is active.");

    internal static EfficiencyProfiler? Active => CurrentSlot.Value;

    /// <summary>
    /// Starts the one process profiler. The optional directory exists only
    /// for deterministic test isolation; normal callers use .rimdev/profiles.
    /// </summary>
    public static EfficiencyProfiler Start(string? profileDirectory = null) =>
        new(profileDirectory);

    public string RunId => runId;

    public string? ProfilePath { get; private set; }

    public Activity? StartActivity(
        string operation,
        string category,
        string? phase = null)
    {
        if (Volatile.Read(ref disposed) != 0 || provider is null)
        {
            return null;
        }

        try
        {
            string semanticOperation = ProfilerValue.SemanticName(operation, "operation");
            Activity? activity = source.StartActivity(
                semanticOperation,
                ActivityKind.Internal);
            if (activity is null)
            {
                return null;
            }

            activity.SetTag(EfficiencyProfilerTags.Operation, semanticOperation);
            activity.SetTag(
                EfficiencyProfilerTags.Category,
                ProfilerValue.SemanticName(category, "other"));
            if (!string.IsNullOrWhiteSpace(phase))
            {
                activity.SetTag(
                    EfficiencyProfilerTags.Phase,
                    ProfilerValue.SemanticName(phase, "phase"));
            }

            return activity;
        }
        catch
        {
            return null;
        }
    }

    public void SetCommand(string? value)
    {
        command = ProfilerValue.SemanticName(value, "unknown");
        invocation?.SetTag(EfficiencyProfilerTags.Command, command);
    }

    public void SetWorkflow(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            invocation?.SetTag(
                EfficiencyProfilerTags.Workflow,
                ProfilerValue.Hash(value));
        }
    }

    public void Complete(int commandExitCode, bool wasCancelled = false)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return;
        }

        exitCode = commandExitCode;
        outcome = wasCancelled
            ? "cancelled"
            : commandExitCode == 0
                ? "success"
                : "failure";

        if (invocation is null)
        {
            return;
        }

        try
        {
            invocation.SetTag(EfficiencyProfilerTags.Outcome, outcome);
            invocation.SetTag(EfficiencyProfilerTags.ExitCode, commandExitCode);
            invocation.Stop();
        }
        catch
        {
            // A profiler shutdown failure is deliberately invisible to the
            // command and cannot change its exit semantics.
        }
    }

    public string BuildProfileJson()
    {
        try
        {
            byte[] bytes = BuildBoundedProfileBytes();
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return MinimalProfileJson("profile-build-failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Complete(exitCode, wasCancelled: outcome == "cancelled");
            TryPersist(BuildBoundedProfileBytes());
        }
        catch
        {
            // Persistence and serialization are best effort and must not
            // affect the underlying RimLiaison result.
        }
        finally
        {
            try
            {
                provider?.Dispose();
            }
            catch
            {
            }

            if (ReferenceEquals(CurrentSlot.Value, this))
            {
                CurrentSlot.Value = previous;
            }
        }
    }

    private byte[] BuildBoundedProfileBytes()
    {
        EfficiencyProfileSnapshot snapshot = processor.Snapshot();
        int[] limits = [16, 12, 8, 6, 4, 2, 1, 0];
        foreach (int limit in limits)
        {
            byte[] candidate = Serialize(snapshot, limit);
            if (candidate.Length <= MaximumProfileBytes)
            {
                return candidate;
            }
        }

        return Encoding.UTF8.GetBytes(MinimalProfileJson("profile-size-bound"));
    }

    private byte[] Serialize(EfficiencyProfileSnapshot snapshot, int evidenceLimit)
    {
        var profile = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = SchemaVersion,
            ["identity"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                ["command"] = command,
                ["startedUtc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["processId"] = Environment.ProcessId
            },
            ["coverage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["boundary"] = "RimLiaison process only",
                ["observed"] = new[]
                {
                    "cli-invocation",
                    "command-phases",
                    "affected-selection",
                    "git-discovery-initiated-by-rimliaison",
                    "build-deploy-freshness",
                    "suite-test-recipe-execution",
                    "devbridge-child-operations",
                    "rimerror-diagnosis",
                    "generation-lease-transitions"
                },
                ["unobservable"] = new[]
                {
                    "file-edits-outside-rimliaison",
                    "shell-commands-outside-rimliaison",
                    "git-operations-outside-rimliaison",
                    "model-calls",
                    "prompts-and-reasoning"
                }
            },
            ["outcome"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = outcome,
                ["exitCode"] = exitCode,
                ["wallTimeMs"] = ElapsedMilliseconds()
            },
            ["usefulWork"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["activities"] = snapshot.TotalActivities,
                ["cumulativeActivityMs"] = ToMilliseconds(snapshot.TotalDurationTicks),
                ["timingModel"] = "activity durations are cumulative and may overlap; outcome.wallTimeMs is elapsed process time",
                ["failedActivities"] = snapshot.TotalFailures,
                ["cancelledActivities"] = snapshot.TotalCancelled,
                ["retryCount"] = snapshot.TotalRetries,
                ["noOpCount"] = snapshot.TotalNoOps
            },
            ["phaseTiming"] = snapshot.Phases
                .Take(evidenceLimit)
                .Select(ToPhaseEvidence)
                .ToArray(),
            ["operationCounts"] = snapshot.Operations
                .Take(evidenceLimit)
                .Select(ToOperationEvidence)
                .ToArray(),
            ["testing"] = snapshot.Summary("testing"),
            ["buildDeploy"] = snapshot.Summary("build-deploy"),
            ["repeatedOperations"] = snapshot.Operations
                .Where(static value => value.Runs > 1)
                .OrderByDescending(static value => value.Runs)
                .ThenByDescending(static value => value.DurationTicks)
                .ThenBy(static value => value.Fingerprint, StringComparer.Ordinal)
                .Take(evidenceLimit)
                .Select(ToRepeatEvidence)
                .ToArray(),
            ["unchangedStateRepetition"] = snapshot.Operations
                .Where(static value => value.RepeatedGenerations.Count > 0)
                .OrderByDescending(static value => value.RepeatedGenerations.Count)
                .ThenByDescending(static value => value.Runs)
                .ThenByDescending(static value => value.DurationTicks)
                .ThenBy(static value => value.Fingerprint, StringComparer.Ordinal)
                .Take(evidenceLimit)
                .Select(ToUnchangedEvidence)
                .ToArray(),
            ["retryFailureGroups"] = snapshot.Operations
                .Where(static value => value.Failures > 0 || value.Retries > 0)
                .OrderByDescending(static value => value.Failures)
                .ThenByDescending(static value => value.Retries)
                .ThenByDescending(static value => value.DurationTicks)
                .ThenBy(static value => value.Fingerprint, StringComparer.Ordinal)
                .Take(evidenceLimit)
                .Select(ToFailureEvidence)
                .ToArray(),
            ["noOpEvidence"] = snapshot.Operations
                .Where(static value => value.NoOpRuns > 0)
                .OrderByDescending(static value => value.NoOpRuns)
                .ThenByDescending(static value => value.RepeatedGenerations.Count)
                .ThenByDescending(static value => value.DurationTicks)
                .ThenBy(static value => value.Fingerprint, StringComparer.Ordinal)
                .Take(evidenceLimit)
                .Select(ToNoOpEvidence)
                .ToArray(),
            ["slowest"] = snapshot.Operations
                .Take(evidenceLimit)
                .Select(static value => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fingerprint"] = value.Fingerprint,
                    ["operation"] = value.Operation,
                    ["cumulativeMs"] = ToMilliseconds(value.DurationTicks),
                    ["runs"] = value.Runs
                })
                .ToArray(),
            ["churn"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["items"] = snapshot.TotalItems,
                ["targets"] = snapshot.TotalTargets,
                ["outputChars"] = snapshot.TotalOutputChars
            },
            ["overflow"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operationGroupsOmitted"] = snapshot.OmittedOperationGroups,
                ["phaseGroupsOmitted"] = snapshot.OmittedPhaseGroups,
                ["categoryGroupsOmitted"] = snapshot.OmittedCategoryGroups,
                ["generationValuesOmitted"] = snapshot.OmittedGenerationValues,
                ["errorCodesOmitted"] = snapshot.OmittedErrorCodes,
                ["processorFailures"] = snapshot.ProcessorFailures,
                ["evidenceLimit"] = evidenceLimit,
                ["outputTrimmed"] = evidenceLimit < 16
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(
            profile,
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
    }

    private void TryPersist(byte[] bytes)
    {
        if (bytes.Length > MaximumProfileBytes)
        {
            return;
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(profileDirectory);
            string finalPath = Path.Combine(profileDirectory, "rimliaison-" + runId + ".json");
            temporaryPath = finalPath + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, finalPath, overwrite: true);
            temporaryPath = null;
            ProfilePath = finalPath;
            RetainProfiles(profileDirectory);
        }
        catch
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static void RetainProfiles(string directory)
    {
        string[] files = Directory
            .EnumerateFiles(directory, "rimliaison-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        long bytes = 0;
        for (int index = 0; index < files.Length; index++)
        {
            bool keep = index < MaximumRetainedProfiles;
            if (keep)
            {
                try
                {
                    long length = new FileInfo(files[index]).Length;
                    keep = bytes + length <= MaximumRetentionBytes;
                    if (keep)
                    {
                        bytes += length;
                    }
                }
                catch
                {
                    keep = false;
                }
            }

            if (!keep)
            {
                try
                {
                    File.Delete(files[index]);
                }
                catch
                {
                }
            }
        }
    }

    private string MinimalProfileJson(string reason) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = SchemaVersion,
            ["identity"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                ["command"] = command,
                ["startedUtc"] = startedUtc.ToString("O", CultureInfo.InvariantCulture)
            },
            ["coverage"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["boundary"] = "RimLiaison process only"
            },
            ["outcome"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = outcome,
                ["exitCode"] = exitCode,
                ["wallTimeMs"] = ElapsedMilliseconds()
            },
            ["overflow"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outputTrimmed"] = true,
                ["reason"] = reason
            }
        });

    private long ElapsedMilliseconds() =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    private static string ResolveProfileDirectory(string? requested)
    {
        try
        {
            return Path.GetFullPath(
                string.IsNullOrWhiteSpace(requested)
                    ? Path.Combine(Environment.CurrentDirectory, ".rimdev", "profiles")
                    : requested);
        }
        catch
        {
            return Path.Combine(Environment.CurrentDirectory, ".rimdev", "profiles");
        }
    }

    private static Dictionary<string, object?> ToPhaseEvidence(PhaseEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["phase"] = value.Phase,
            ["runs"] = value.Runs,
            ["cumulativeMs"] = ToMilliseconds(value.DurationTicks),
            ["failures"] = value.Failures
        };

    private static Dictionary<string, object?> ToOperationEvidence(OperationEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["operation"] = value.Operation,
            ["category"] = value.Category,
            ["phase"] = value.Phase,
            ["fingerprint"] = value.Fingerprint,
            ["runs"] = value.Runs,
            ["cumulativeMs"] = ToMilliseconds(value.DurationTicks),
            ["failures"] = value.Failures,
            ["cancelled"] = value.Cancelled,
            ["retries"] = value.Retries,
            ["noOpRuns"] = value.NoOpRuns,
            ["target"] = value.TargetHash,
            ["scope"] = value.ScopeHash,
            ["testType"] = value.TestType,
            ["generations"] = value.Generations,
            ["errorCodes"] = value.ErrorCodes
        };

    private static Dictionary<string, object?> ToRepeatEvidence(OperationEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["fingerprint"] = value.Fingerprint,
            ["operation"] = value.Operation,
            ["runs"] = value.Runs,
            ["cumulativeMs"] = ToMilliseconds(value.DurationTicks),
            ["generationRuns"] = value.Generations
        };

    private static Dictionary<string, object?> ToUnchangedEvidence(OperationEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["fingerprint"] = value.Fingerprint,
            ["operation"] = value.Operation,
            ["generations"] = value.RepeatedGenerations
        };

    private static Dictionary<string, object?> ToFailureEvidence(OperationEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["fingerprint"] = value.Fingerprint,
            ["operation"] = value.Operation,
            ["runs"] = value.Runs,
            ["failures"] = value.Failures,
            ["retries"] = value.Retries,
            ["errorCodes"] = value.ErrorCodes
        };

    private static Dictionary<string, object?> ToNoOpEvidence(OperationEvidence value) =>
        new(StringComparer.Ordinal)
        {
            ["fingerprint"] = value.Fingerprint,
            ["operation"] = value.Operation,
            ["runs"] = value.Runs,
            ["noOpRuns"] = value.NoOpRuns,
            ["generations"] = value.Generations
        };

    private static long ToMilliseconds(long ticks) =>
        Math.Max(0, (long)TimeSpan.FromTicks(Math.Max(0, ticks)).TotalMilliseconds);
}

public static class EfficiencyProfilerTags
{
    public const string Operation = "rim.operation";
    public const string Category = "rim.category";
    public const string Phase = "rim.phase";
    public const string Outcome = "rim.outcome";
    public const string ErrorCode = "rim.error.code";
    public const string Command = "rim.command";
    public const string Workflow = "rim.workflow";
    public const string ExitCode = "rim.exit.code";
    public const string Target = "rim.target";
    public const string Scope = "rim.scope";
    public const string TestType = "rim.test.type";
    public const string Generation = "rim.generation";
    public const string StateChanged = "rim.state.changed";
    public const string Retry = "rim.retry";
    public const string ItemCount = "rim.count.items";
    public const string OutputChars = "rim.output.chars";
}

/// <summary>Small helpers used at the central RimLiaison execution boundaries.</summary>
public static class ProfilerActivity
{
    public static Activity? Start(
        string operation,
        string category,
        string? phase = null,
        string? target = null,
        string? scope = null,
        string? testType = null)
    {
        Activity? activity = EfficiencyProfiler.Active?.StartActivity(operation, category, phase);
        if (activity is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            SetLogicalTarget(activity, target);
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            activity.SetTag(EfficiencyProfilerTags.Scope, ProfilerValue.Hash(scope));
        }

        if (!string.IsNullOrWhiteSpace(testType))
        {
            activity.SetTag(
                EfficiencyProfilerTags.TestType,
                ProfilerValue.SemanticName(testType, "test"));
        }

        return activity;
    }

    public static async Task<T> ObserveAsync<T>(
        string operation,
        string category,
        Func<Task<T>> callback,
        Action<Activity?, T>? annotate = null,
        string? phase = null,
        string? target = null,
        string? scope = null,
        string? testType = null)
    {
        Activity? activity = Start(operation, category, phase, target, scope, testType);
        try
        {
            T result = await callback().ConfigureAwait(false);
            try
            {
                annotate?.Invoke(activity, result);
            }
            catch
            {
                // Annotation is optional evidence and cannot alter a command.
                SetOutcome(activity, "failure", "PROFILER_ANNOTATION_FAILED");
            }

            Stop(activity, "success");
            return result;
        }
        catch (OperationCanceledException)
        {
            Stop(activity, "cancelled", "RIMTEST_CANCELLED");
            throw;
        }
        catch
        {
            Stop(activity, "failure", "PROFILER_WRAPPED_OPERATION_FAILED");
            throw;
        }
    }

    public static void SetOutcome(
        Activity? activity,
        string outcome,
        string? errorCode = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(
            EfficiencyProfilerTags.Outcome,
            ProfilerValue.SemanticName(outcome, "unknown"));
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            activity.SetTag(
                EfficiencyProfilerTags.ErrorCode,
                ProfilerValue.ErrorCode(errorCode));
        }
    }

    public static void SetLogicalTarget(Activity? activity, string? value)
    {
        if (activity is not null && !string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(EfficiencyProfilerTags.Target, ProfilerValue.Hash(value));
        }
    }

    public static void SetGeneration(Activity? activity, int? generation)
    {
        if (activity is not null && generation is > 0)
        {
            activity.SetTag(EfficiencyProfilerTags.Generation, generation.Value);
        }
    }

    public static void SetStateChanged(Activity? activity, bool? changed)
    {
        if (activity is not null && changed.HasValue)
        {
            activity.SetTag(EfficiencyProfilerTags.StateChanged, changed.Value);
        }
    }

    public static void SetRetry(Activity? activity, int retryCount)
    {
        if (activity is not null && retryCount > 0)
        {
            activity.SetTag(EfficiencyProfilerTags.Retry, Math.Min(retryCount, 32));
        }
    }

    public static void SetCounts(Activity? activity, int? items = null, int? outputChars = null)
    {
        if (activity is null)
        {
            return;
        }

        if (items is >= 0)
        {
            activity.SetTag(EfficiencyProfilerTags.ItemCount, items.Value);
        }

        if (outputChars is >= 0)
        {
            activity.SetTag(EfficiencyProfilerTags.OutputChars, outputChars.Value);
        }
    }

    public static void Stop(
        Activity? activity,
        string defaultOutcome = "success",
        string? errorCode = null)
    {
        if (activity is null)
        {
            return;
        }

        try
        {
            if (activity.GetTagItem(EfficiencyProfilerTags.Outcome) is null)
            {
                SetOutcome(activity, defaultOutcome, errorCode);
            }
            else if (!string.IsNullOrWhiteSpace(errorCode))
            {
                activity.SetTag(EfficiencyProfilerTags.ErrorCode, ProfilerValue.ErrorCode(errorCode));
            }

            activity.Stop();
        }
        catch
        {
        }
    }

    internal static string DevBridgeOperation(IReadOnlyList<string> arguments)
    {
        int testIndex = -1;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "test", StringComparison.OrdinalIgnoreCase))
            {
                testIndex = index;
                break;
            }
        }

        if (testIndex >= 0 && testIndex + 2 < arguments.Count)
        {
            string operation = arguments[testIndex + 1].ToLowerInvariant();
            return operation switch
            {
                "recipe" when testIndex + 3 < arguments.Count =>
                    "devbridge.test.recipe." + arguments[testIndex + 2].ToLowerInvariant(),
                "begin" or "renew" or "end" => "devbridge.lifecycle." + operation,
                _ => "devbridge.test." + ProfilerValue.SemanticName(operation, "operation")
            };
        }

        if (arguments.Any(static value => string.Equals(value, "restart", StringComparison.OrdinalIgnoreCase)))
        {
            return "devbridge.lifecycle.restart";
        }

        return "devbridge.process";
    }
}

internal sealed class EfficiencyActivityProcessor : BaseProcessor<Activity>
{
    private readonly EfficiencyProfileAggregator aggregator = new();

    public override void OnEnd(Activity data)
    {
        aggregator.Observe(data);
    }

    internal EfficiencyProfileSnapshot Snapshot() => aggregator.Snapshot();
}

internal sealed class EfficiencyProfileAggregator
{
    private const int MaximumOperationGroups = 256;
    private const int MaximumPhaseGroups = 64;
    private const int MaximumGenerationValues = 8;
    private const int MaximumErrorCodes = 4;

    private readonly ConcurrentDictionary<string, OperationAggregate> operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PhaseAggregate> phases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CategoryAggregate> categories = new(StringComparer.Ordinal);
    private int operationSlots;
    private int phaseSlots;
    private int categorySlots;
    private long totalActivities;
    private long totalDurationTicks;
    private long totalFailures;
    private long totalCancelled;
    private long totalRetries;
    private long totalNoOps;
    private long totalItems;
    private long totalOutputChars;
    private long totalTargets;
    private long omittedOperationGroups;
    private long omittedPhaseGroups;
    private long omittedCategoryGroups;
    private long omittedGenerationValues;
    private long omittedErrorCodes;
    private long processorFailures;

    internal void Observe(Activity activity)
    {
        try
        {
            string operation = ProfilerValue.SemanticName(
                ReadString(activity, EfficiencyProfilerTags.Operation) ?? activity.OperationName,
                "operation");
            string category = ProfilerValue.SemanticName(
                ReadString(activity, EfficiencyProfilerTags.Category),
                "other");
            string phase = ProfilerValue.SemanticName(
                ReadString(activity, EfficiencyProfilerTags.Phase),
                category);
            string target = ProfilerValue.Hash(ReadString(activity, EfficiencyProfilerTags.Target));
            string scope = ProfilerValue.Hash(ReadString(activity, EfficiencyProfilerTags.Scope));
            string testType = ProfilerValue.SemanticName(
                ReadString(activity, EfficiencyProfilerTags.TestType),
                string.Empty);
            string fingerprint = ProfilerValue.OperationFingerprint(
                operation,
                category,
                phase,
                target,
                scope,
                testType);
            string outcome = ProfilerValue.SemanticName(
                ReadString(activity, EfficiencyProfilerTags.Outcome),
                "unknown");
            string? errorCode = ProfilerValue.ErrorCode(
                ReadString(activity, EfficiencyProfilerTags.ErrorCode));
            int? generation = ReadInt(activity, EfficiencyProfilerTags.Generation);
            int retry = Math.Max(0, ReadInt(activity, EfficiencyProfilerTags.Retry) ?? 0);
            int items = Math.Max(0, ReadInt(activity, EfficiencyProfilerTags.ItemCount) ?? 0);
            int outputChars = Math.Max(0, ReadInt(activity, EfficiencyProfilerTags.OutputChars) ?? 0);
            bool? stateChanged = ReadBool(activity, EfficiencyProfilerTags.StateChanged);
            long durationTicks = Math.Max(0, activity.Duration.Ticks);
            bool failed = IsFailure(outcome);
            bool cancelled = string.Equals(outcome, "cancelled", StringComparison.Ordinal);

            Interlocked.Increment(ref totalActivities);
            Interlocked.Add(ref totalDurationTicks, durationTicks);
            if (failed)
            {
                Interlocked.Increment(ref totalFailures);
            }

            if (cancelled)
            {
                Interlocked.Increment(ref totalCancelled);
            }

            if (retry > 0)
            {
                Interlocked.Add(ref totalRetries, retry);
            }

            if (stateChanged == false)
            {
                Interlocked.Increment(ref totalNoOps);
            }

            Interlocked.Add(ref totalItems, items);
            Interlocked.Add(ref totalOutputChars, outputChars);
            if (target.Length > 0)
            {
                Interlocked.Increment(ref totalTargets);
            }

            AddCategory(category, durationTicks, failed);
            AddPhase(phase, durationTicks, failed);

            if (!operations.TryGetValue(fingerprint, out OperationAggregate? aggregate))
            {
                int reservedSlot = Interlocked.Increment(ref operationSlots);
                if (operations.TryGetValue(fingerprint, out aggregate))
                {
                    Interlocked.Decrement(ref operationSlots);
                }
                else if (reservedSlot > MaximumOperationGroups)
                {
                    Interlocked.Increment(ref omittedOperationGroups);
                    return;
                }
                else
                {
                    OperationAggregate candidate = new(
                        fingerprint,
                        operation,
                        category,
                        phase,
                        target.Length == 0 ? null : "h-" + target,
                        scope.Length == 0 ? null : "h-" + scope,
                        string.IsNullOrEmpty(testType) ? null : testType);
                    if (operations.TryAdd(fingerprint, candidate))
                    {
                        aggregate = candidate;
                    }
                    else
                    {
                        Interlocked.Decrement(ref operationSlots);
                        if (!operations.TryGetValue(fingerprint, out aggregate))
                        {
                            Interlocked.Increment(ref omittedOperationGroups);
                            return;
                        }
                    }
                }
            }

            aggregate.Observe(
                durationTicks,
                outcome,
                errorCode,
                generation,
                retry,
                stateChanged,
                items,
                outputChars,
                ref omittedGenerationValues,
                ref omittedErrorCodes);
        }
        catch
        {
            Interlocked.Increment(ref processorFailures);
        }
    }

    internal EfficiencyProfileSnapshot Snapshot()
    {
        OperationEvidence[] operationEvidence = operations.Values
            .Select(static value => value.Snapshot())
            .OrderByDescending(static value => value.DurationTicks)
            .ThenByDescending(static value => value.Runs)
            .ThenBy(static value => value.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        PhaseEvidence[] phaseEvidence = phases.Values
            .Select(static value => value.Snapshot())
            .OrderByDescending(static value => value.DurationTicks)
            .ThenBy(static value => value.Phase, StringComparer.Ordinal)
            .ToArray();
        CategoryEvidence[] categoryEvidence = categories.Values
            .Select(static value => value.Snapshot())
            .OrderBy(static value => value.Category, StringComparer.Ordinal)
            .ToArray();
        return new EfficiencyProfileSnapshot(
            Interlocked.Read(ref totalActivities),
            Interlocked.Read(ref totalDurationTicks),
            Interlocked.Read(ref totalFailures),
            Interlocked.Read(ref totalCancelled),
            Interlocked.Read(ref totalRetries),
            Interlocked.Read(ref totalNoOps),
            Interlocked.Read(ref totalItems),
            Interlocked.Read(ref totalOutputChars),
            Interlocked.Read(ref totalTargets),
            Interlocked.Read(ref omittedOperationGroups),
            Interlocked.Read(ref omittedPhaseGroups),
            Interlocked.Read(ref omittedCategoryGroups),
            Interlocked.Read(ref omittedGenerationValues),
            Interlocked.Read(ref omittedErrorCodes),
            Interlocked.Read(ref processorFailures),
            operationEvidence,
            phaseEvidence,
            categoryEvidence);
    }

    private void AddCategory(string category, long durationTicks, bool failed)
    {
        if (!categories.TryGetValue(category, out CategoryAggregate? aggregate))
        {
            int reservedSlot = Interlocked.Increment(ref categorySlots);
            if (categories.TryGetValue(category, out aggregate))
            {
                Interlocked.Decrement(ref categorySlots);
            }
            else if (reservedSlot > MaximumPhaseGroups)
            {
                Interlocked.Increment(ref omittedCategoryGroups);
                return;
            }
            else
            {
                CategoryAggregate candidate = new(category);
                if (categories.TryAdd(category, candidate))
                {
                    aggregate = candidate;
                }
                else
                {
                    Interlocked.Decrement(ref categorySlots);
                    if (!categories.TryGetValue(category, out aggregate))
                    {
                        Interlocked.Increment(ref omittedCategoryGroups);
                        return;
                    }
                }
            }
        }

        aggregate.Observe(durationTicks, failed);
    }

    private void AddPhase(string phase, long durationTicks, bool failed)
    {
        if (!phases.TryGetValue(phase, out PhaseAggregate? aggregate))
        {
            int reservedSlot = Interlocked.Increment(ref phaseSlots);
            if (phases.TryGetValue(phase, out aggregate))
            {
                Interlocked.Decrement(ref phaseSlots);
            }
            else if (reservedSlot > MaximumPhaseGroups)
            {
                Interlocked.Increment(ref omittedPhaseGroups);
                return;
            }
            else
            {
                PhaseAggregate candidate = new(phase);
                if (phases.TryAdd(phase, candidate))
                {
                    aggregate = candidate;
                }
                else
                {
                    Interlocked.Decrement(ref phaseSlots);
                    if (!phases.TryGetValue(phase, out aggregate))
                    {
                        Interlocked.Increment(ref omittedPhaseGroups);
                        return;
                    }
                }
            }
        }

        aggregate.Observe(durationTicks, failed);
    }

    private static string? ReadString(Activity activity, string key) =>
        activity.GetTagItem(key) switch
        {
            string value => value,
            _ => null
        };

    private static int? ReadInt(Activity activity, string key)
    {
        object? value = activity.GetTagItem(key);
        return value switch
        {
            int integer => integer,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ => null
        };
    }

    private static bool? ReadBool(Activity activity, string key) =>
        activity.GetTagItem(key) is bool value ? value : null;

    private static bool IsFailure(string value) => value is
        "failure" or "failed" or "error" or "timeout" or "refused" or
        "infrastructure" or "testfailure" or "test-failure";

    internal static bool IsFailureForTests(string value) => IsFailure(value);
}

internal sealed class OperationAggregate
{
    private readonly ConcurrentDictionary<int, int> generations = new();
    private readonly ConcurrentDictionary<string, int> errorCodes = new(StringComparer.Ordinal);
    private int generationSlots;
    private int errorCodeSlots;
    private long runs;
    private long durationTicks;
    private long failures;
    private long cancelled;
    private long retries;
    private long noOpRuns;
    private long items;
    private long outputChars;

    internal OperationAggregate(
        string fingerprint,
        string operation,
        string category,
        string phase,
        string? targetHash,
        string? scopeHash,
        string? testType)
    {
        Fingerprint = fingerprint;
        Operation = operation;
        Category = category;
        Phase = phase;
        TargetHash = targetHash;
        ScopeHash = scopeHash;
        TestType = testType;
    }

    internal string Fingerprint { get; }
    internal string Operation { get; }
    internal string Category { get; }
    internal string Phase { get; }
    internal string? TargetHash { get; }
    internal string? ScopeHash { get; }
    internal string? TestType { get; }

    internal void Observe(
        long duration,
        string outcome,
        string? errorCode,
        int? generation,
        int retry,
        bool? stateChanged,
        int itemCount,
        int outputCharCount,
        ref long omittedGenerations,
        ref long omittedErrors)
    {
        Interlocked.Increment(ref runs);
        Interlocked.Add(ref durationTicks, duration);
        if (EfficiencyProfileAggregator.IsFailureForTests(outcome))
        {
            Interlocked.Increment(ref failures);
        }

        if (string.Equals(outcome, "cancelled", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref cancelled);
        }

        if (retry > 0)
        {
            Interlocked.Add(ref retries, retry);
        }

        if (stateChanged == false)
        {
            Interlocked.Increment(ref noOpRuns);
        }

        Interlocked.Add(ref items, itemCount);
        Interlocked.Add(ref outputChars, outputCharCount);
        if (generation is > 0)
        {
            if (generations.ContainsKey(generation.Value))
            {
                generations.AddOrUpdate(generation.Value, 1, static (_, value) => value + 1);
            }
            else if (Interlocked.Increment(ref generationSlots) <= 8)
            {
                if (!generations.TryAdd(generation.Value, 1))
                {
                    Interlocked.Decrement(ref generationSlots);
                    generations.AddOrUpdate(generation.Value, 1, static (_, value) => value + 1);
                }
            }
            else if (!generations.ContainsKey(generation.Value))
            {
                Interlocked.Increment(ref omittedGenerations);
            }
        }

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            if (errorCodes.ContainsKey(errorCode))
            {
                errorCodes.AddOrUpdate(errorCode, 1, static (_, value) => value + 1);
            }
            else if (Interlocked.Increment(ref errorCodeSlots) <= 4)
            {
                if (!errorCodes.TryAdd(errorCode, 1))
                {
                    Interlocked.Decrement(ref errorCodeSlots);
                    errorCodes.AddOrUpdate(errorCode, 1, static (_, value) => value + 1);
                }
            }
            else if (!errorCodes.ContainsKey(errorCode))
            {
                Interlocked.Increment(ref omittedErrors);
            }
        }
    }

    internal OperationEvidence Snapshot() => new(
        Fingerprint,
        Operation,
        Category,
        Phase,
        TargetHash,
        ScopeHash,
        TestType,
        Interlocked.Read(ref runs),
        Interlocked.Read(ref durationTicks),
        Interlocked.Read(ref failures),
        Interlocked.Read(ref cancelled),
        Interlocked.Read(ref retries),
        Interlocked.Read(ref noOpRuns),
        generations
            .OrderBy(static value => value.Key)
            .Select(static value => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["generation"] = value.Key,
                ["runs"] = value.Value
            })
            .ToArray(),
        generations
            .Where(static value => value.Value > 1)
            .OrderBy(static value => value.Key)
            .Select(static value => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["generation"] = value.Key,
                ["runs"] = value.Value
            })
            .ToArray(),
        errorCodes
            .OrderBy(static value => value.Key, StringComparer.Ordinal)
            .Select(static value => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = value.Key,
                ["runs"] = value.Value
            })
            .ToArray());
}

internal sealed class PhaseAggregate
{
    private long runs;
    private long durationTicks;
    private long failures;

    internal PhaseAggregate(string phase) => Phase = phase;

    internal string Phase { get; }

    internal void Observe(long duration, bool failed)
    {
        Interlocked.Increment(ref runs);
        Interlocked.Add(ref durationTicks, duration);
        if (failed)
        {
            Interlocked.Increment(ref failures);
        }
    }

    internal PhaseEvidence Snapshot() => new(
        Phase,
        Interlocked.Read(ref runs),
        Interlocked.Read(ref durationTicks),
        Interlocked.Read(ref failures));
}

internal sealed class CategoryAggregate
{
    private long runs;
    private long durationTicks;
    private long failures;

    internal CategoryAggregate(string category) => Category = category;

    internal string Category { get; }

    internal void Observe(long duration, bool failed)
    {
        Interlocked.Increment(ref runs);
        Interlocked.Add(ref durationTicks, duration);
        if (failed)
        {
            Interlocked.Increment(ref failures);
        }
    }

    internal CategoryEvidence Snapshot() => new(
        Category,
        Interlocked.Read(ref runs),
        Interlocked.Read(ref durationTicks),
        Interlocked.Read(ref failures));
}

internal sealed record EfficiencyProfileSnapshot(
    long TotalActivities,
    long TotalDurationTicks,
    long TotalFailures,
    long TotalCancelled,
    long TotalRetries,
    long TotalNoOps,
    long TotalItems,
    long TotalOutputChars,
    long TotalTargets,
    long OmittedOperationGroups,
    long OmittedPhaseGroups,
    long OmittedCategoryGroups,
    long OmittedGenerationValues,
    long OmittedErrorCodes,
    long ProcessorFailures,
    IReadOnlyList<OperationEvidence> Operations,
    IReadOnlyList<PhaseEvidence> Phases,
    IReadOnlyList<CategoryEvidence> Categories)
{
    internal Dictionary<string, object?> Summary(string category)
    {
        CategoryEvidence? value = Categories.FirstOrDefault(
            item => string.Equals(item.Category, category, StringComparison.Ordinal));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runs"] = value?.Runs ?? 0,
            ["cumulativeMs"] = value is null ? 0 : ToMilliseconds(value.DurationTicks),
            ["failures"] = value?.Failures ?? 0
        };
    }

    private static long ToMilliseconds(long ticks) =>
        Math.Max(0, (long)TimeSpan.FromTicks(Math.Max(0, ticks)).TotalMilliseconds);
}

internal sealed record OperationEvidence(
    string Fingerprint,
    string Operation,
    string Category,
    string Phase,
    string? TargetHash,
    string? ScopeHash,
    string? TestType,
    long Runs,
    long DurationTicks,
    long Failures,
    long Cancelled,
    long Retries,
    long NoOpRuns,
    IReadOnlyList<Dictionary<string, object?>> Generations,
    IReadOnlyList<Dictionary<string, object?>> RepeatedGenerations,
    IReadOnlyList<Dictionary<string, object?>> ErrorCodes);

internal sealed record PhaseEvidence(
    string Phase,
    long Runs,
    long DurationTicks,
    long Failures);

internal sealed record CategoryEvidence(
    string Category,
    long Runs,
    long DurationTicks,
    long Failures);

internal static class ProfilerValue
{
    internal static string SemanticName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string trimmed = value.Trim();
        var builder = new StringBuilder(Math.Min(trimmed.Length, 64));
        foreach (char character in trimmed)
        {
            if (builder.Length >= 64)
            {
                break;
            }

            builder.Append(
                character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '.' or '-' or '_'
                    ? char.ToLowerInvariant(character)
                    : '-');
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    internal static string ErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        bool stableCode = trimmed.Length <= 64 && trimmed.All(static character =>
            character is >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '_' or '-' or '.');
        return stableCode
            ? trimmed.ToLowerInvariant()
            : "h-" + Hash(trimmed);
    }

    internal static string Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string bounded = value.Length <= 256 ? value : value[..256];
        ulong hash = 14695981039346656037UL;
        foreach (char character in bounded)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    internal static string OperationFingerprint(
        string operation,
        string category,
        string phase,
        string target,
        string scope,
        string testType) =>
        Hash(string.Join('\u001f', operation, category, phase, target, scope, testType));
}
