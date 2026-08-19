using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RimLiaison;
using RimLiaison.Profiling;

namespace RimLiaison.Tests;

internal static class ProfilerTests
{
    public static void AggregatesCompactSchema()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 3; index++)
            {
                Activity? activity = profiler.StartActivity(
                    "test.execution",
                    "testing",
                    "test");
                ProfilerActivity.SetCounts(activity, items: 1, outputChars: 12);
                Stop(activity);
            }
        }));

        AssertEqual(
            EfficiencyProfiler.SchemaVersion,
            profile.RootElement.GetProperty("schema").GetString());
        AssertEqual("RimLiaison process only", profile.RootElement
            .GetProperty("coverage").GetProperty("boundary").GetString());
        Assert(profile.RootElement.GetProperty("usefulWork").GetProperty("activities").GetInt64() >= 4,
            "root and child activities should be aggregated");
        Assert(profile.RootElement.GetProperty("operationCounts").GetArrayLength() > 0,
            "operation evidence should be present");
    }

    public static void GroupsRepeatedOperations()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 4; index++)
            {
                Activity? activity = profiler.StartActivity(
                    "recipe.run",
                    "testing",
                    "recipe");
                ProfilerActivity.SetLogicalTarget(activity, "recipe.alpha");
                Stop(activity);
            }
        }));

        JsonElement repeated = Find(
            profile.RootElement.GetProperty("repeatedOperations"),
            "operation",
            "recipe.run");
        AssertEqual(4L, repeated.GetProperty("runs").GetInt64());
        Assert(repeated.GetProperty("fingerprint").GetString()?.Length == 16,
            "semantic fingerprints should be compact");
    }

    public static void GroupsFailuresAndRetries()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 2; index++)
            {
                Activity? activity = profiler.StartActivity(
                    "devbridge.test.recipe.run",
                    "devbridge",
                    "child-process");
                ProfilerActivity.SetLogicalTarget(activity, "recipe.alpha");
                ProfilerActivity.SetRetry(activity, 1);
                ProfilerActivity.SetOutcome(
                    activity,
                    "failure",
                    "DEVBRIDGE_TIMEOUT");
                ProfilerActivity.Stop(activity);
            }
        }));

        JsonElement failure = Find(
            profile.RootElement.GetProperty("retryFailureGroups"),
            "operation",
            "devbridge.test.recipe.run");
        AssertEqual(2L, failure.GetProperty("failures").GetInt64());
        AssertEqual(2L, failure.GetProperty("retries").GetInt64());
        Assert(!profile.RootElement.ToString().Contains("exception payload", StringComparison.OrdinalIgnoreCase),
            "failure groups must not retain diagnostic payloads");
    }

    public static void RecordsUnchangedGenerations()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 3; index++)
            {
                Activity? activity = profiler.StartActivity(
                    "readiness.check",
                    "lifecycle",
                    "generation");
                ProfilerActivity.SetGeneration(activity, 17);
                ProfilerActivity.SetStateChanged(activity, false);
                Stop(activity);
            }
        }));

        JsonElement unchanged = Find(
            profile.RootElement.GetProperty("unchangedStateRepetition"),
            "operation",
            "readiness.check");
        JsonElement generation = unchanged.GetProperty("generations")[0];
        AssertEqual(17, generation.GetProperty("generation").GetInt32());
        AssertEqual(3, generation.GetProperty("runs").GetInt32());
        Assert(profile.RootElement.GetProperty("noOpEvidence").GetArrayLength() > 0,
            "unchanged state should be represented as no-op evidence");
    }

    public static void RedactsRawValues()
    {
        const string secret = "PROMPT secret payload and hidden exception";
        string json = Capture(profiler =>
        {
            Activity? activity = profiler.StartActivity(
                "safe.operation",
                "testing",
                "test");
            activity?.SetTag("raw.payload", secret);
            activity?.SetTag(
                EfficiencyProfilerTags.ErrorCode,
                "System.Exception: " + secret);
            ProfilerActivity.SetLogicalTarget(activity, secret);
            ProfilerActivity.Stop(activity, "failure", "System.Exception: " + secret);
        });

        Assert(!json.Contains(secret, StringComparison.Ordinal),
            "raw payloads must not be present in the profile");
        Assert(!json.Contains("raw.payload", StringComparison.Ordinal),
            "arbitrary activity tags must not be serialized");
        Assert(json.Contains("h-", StringComparison.Ordinal),
            "non-semantic diagnostic values should be represented only by a hash");
    }

    public static void FingerprintsAreDeterministic()
    {
        string first = Capture(profiler => EmitSemanticActivity(profiler, "target.alpha"));
        string second = Capture(profiler => EmitSemanticActivity(profiler, "target.alpha"));
        using JsonDocument firstProfile = Parse(first);
        using JsonDocument secondProfile = Parse(second);
        string? firstFingerprint = Find(
            firstProfile.RootElement.GetProperty("operationCounts"),
            "operation",
            "semantic.operation").GetProperty("fingerprint").GetString();
        string? secondFingerprint = Find(
            secondProfile.RootElement.GetProperty("operationCounts"),
            "operation",
            "semantic.operation").GetProperty("fingerprint").GetString();
        AssertEqual(firstFingerprint, secondFingerprint);
    }

    public static void PrioritizesSpecializedEvidence()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 16; index++)
            {
                for (int run = 0; run < 2; run++)
                {
                    Activity? activity = profiler.StartActivity(
                        "baseline.operation",
                        "testing",
                        "test");
                    ProfilerActivity.SetLogicalTarget(activity, "baseline-" + index);
                    StopWithDuration(activity, 100, "failure", "BASELINE_FAILURE");
                }
            }

            for (int run = 0; run < 3; run++)
            {
                Activity? activity = profiler.StartActivity(
                    "priority.operation",
                    "testing",
                    "test");
                ProfilerActivity.SetLogicalTarget(activity, "priority");
                StopWithDuration(activity, 1, "failure", "PRIORITY_FAILURE");
            }
        }));

        Find(
            profile.RootElement.GetProperty("repeatedOperations"),
            "operation",
            "priority.operation");
        JsonElement failure = Find(
            profile.RootElement.GetProperty("retryFailureGroups"),
            "operation",
            "priority.operation");
        AssertEqual(3L, failure.GetProperty("failures").GetInt64());
    }

    public static void BoundsSectionsAndOutput()
    {
        string directory = CreateTempDirectory();
        try
        {
            string json;
            using (EfficiencyProfiler profiler = EfficiencyProfiler.Start(directory))
            {
                for (int index = 0; index < 1000; index++)
                {
                    Activity? activity = profiler.StartActivity(
                        "synthetic.operation",
                        "synthetic",
                        "large-input");
                    ProfilerActivity.SetLogicalTarget(activity, "target-" + index);
                    ProfilerActivity.SetCounts(activity, items: 1, outputChars: 32);
                    Stop(activity);
                }

                profiler.Complete(0);
                json = profiler.BuildProfileJson();
            }

            Assert(Encoding.UTF8.GetByteCount(json) <= EfficiencyProfiler.MaximumProfileBytes,
                "profile must obey the hard byte bound");
            using JsonDocument profile = Parse(json);
            Assert(profile.RootElement.GetProperty("operationCounts").GetArrayLength() <= 16,
                "operation evidence must be top-N bounded");

            for (int index = 0; index < 25; index++)
            {
                using EfficiencyProfiler profiler = EfficiencyProfiler.Start(directory);
                profiler.Complete(0);
            }

            string[] retained = Directory.GetFiles(directory, "rimliaison-*.json");
            Assert(retained.Length <= EfficiencyProfiler.MaximumRetainedProfiles,
                "profile retention must be bounded");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    public static void PreservesOverflowTotals()
    {
        using JsonDocument profile = Parse(Capture(profiler =>
        {
            for (int index = 0; index < 20; index++)
            {
                Activity? repeated = profiler.StartActivity(
                    "overflow.operation",
                    "synthetic",
                    "overflow");
                ProfilerActivity.SetLogicalTarget(repeated, "same-target");
                ProfilerActivity.SetGeneration(repeated, index + 1);
                ProfilerActivity.SetOutcome(repeated, "failure", "ERR_" + index);
                ProfilerActivity.Stop(repeated);
            }

            for (int index = 0; index < 400; index++)
            {
                Activity? activity = profiler.StartActivity(
                    "unique.operation",
                    "synthetic",
                    "overflow");
                ProfilerActivity.SetLogicalTarget(activity, "unique-target-" + index);
                ProfilerActivity.SetGeneration(activity, index + 1);
                ProfilerActivity.SetOutcome(activity, "failure", "ERR_" + index);
                ProfilerActivity.Stop(activity);
            }
        }));

        JsonElement overflow = profile.RootElement.GetProperty("overflow");
        Assert(overflow.GetProperty("operationGroupsOmitted").GetInt64() > 0,
            "omitted operation groups must be counted");
        Assert(overflow.GetProperty("generationValuesOmitted").GetInt64() > 0,
            "omitted generation evidence must be counted");
        Assert(overflow.GetProperty("errorCodesOmitted").GetInt64() > 0,
            "omitted error evidence must be counted");
    }

    public static void FailuresDoNotAlterCommandResults()
    {
        string directory = CreateTempDirectory();
        string blockingPath = Path.Combine(directory, "profile-target");
        File.WriteAllText(blockingPath, "not a directory");
        int commandResult = 37;
        try
        {
            using EfficiencyProfiler profiler = EfficiencyProfiler.Start(blockingPath);
            profiler.Complete(commandResult);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Profiler failure escaped the wrapped command.",
                exception);
        }

        AssertEqual(37, commandResult);
        TryDelete(directory);
    }

    public static void PreservesCliOutputContracts()
    {
        string directory = CreateTempDirectory();
        string? previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    ["--help"],
                    stdout,
                    stderr)
                .GetAwaiter()
                .GetResult();
            AssertEqual(0, exitCode);
            using JsonDocument help = JsonDocument.Parse(stdout.ToString());
            Assert(help.RootElement.TryGetProperty("progressiveDisclosure", out _),
                "help stdout should retain the existing JSON contract");
            AssertEqual(string.Empty, stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = previous!;
            TryDelete(directory);
        }
    }

    public static void EmitsSuccessAndFailureProfiles()
    {
        (int successCode, int successProfiles) = RunCliWithProfiles(["--help"]);
        (int failureCode, int failureProfiles) = RunCliWithProfiles(["not-a-command"]);
        AssertEqual(0, successCode);
        Assert(successProfiles == 1, "successful CLI invocation should emit one profile");
        Assert(failureCode != 0, "invalid CLI invocation should retain a failure result");
        Assert(failureProfiles == 1, "failed CLI invocation should emit one profile");
    }

    private static void EmitSemanticActivity(
        EfficiencyProfiler profiler,
        string target)
    {
        Activity? activity = profiler.StartActivity(
            "semantic.operation",
            "testing",
            "phase");
        ProfilerActivity.SetLogicalTarget(activity, target);
        ProfilerActivity.SetGeneration(activity, 11);
        Stop(activity);
    }

    private static string Capture(
        Action<EfficiencyProfiler> emit,
        int exitCode = 0,
        bool cancelled = false)
    {
        string directory = CreateTempDirectory();
        try
        {
            using EfficiencyProfiler profiler = EfficiencyProfiler.Start(directory);
            emit(profiler);
            profiler.Complete(exitCode, cancelled);
            return profiler.BuildProfileJson();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static JsonElement Find(
        JsonElement array,
        string property,
        string expected)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.TryGetProperty(property, out JsonElement value) &&
                string.Equals(value.GetString(), expected, StringComparison.Ordinal))
            {
                return item;
            }
        }

        throw new InvalidOperationException(
            $"Could not find {property}={expected} in profiler evidence.");
    }

    private static void Stop(Activity? activity) =>
        ProfilerActivity.Stop(activity, "success");

    private static void StopWithDuration(
        Activity? activity,
        int durationMilliseconds,
        string outcome,
        string errorCode)
    {
        if (activity is not null)
        {
            activity.SetEndTime(
                activity.StartTimeUtc.AddMilliseconds(durationMilliseconds));
        }

        ProfilerActivity.Stop(activity, outcome, errorCode);
    }

    private static (int ExitCode, int ProfileCount) RunCliWithProfiles(
        IReadOnlyList<string> args)
    {
        string directory = CreateTempDirectory();
        string? previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            int exitCode = CliApplication.RunAsync(
                    args.ToArray(),
                    new StringWriter(),
                    new StringWriter())
                .GetAwaiter()
                .GetResult();
            string profileDirectory = Path.Combine(directory, ".rimdev", "profiles");
            int profileCount = Directory.Exists(profileDirectory)
                ? Directory.GetFiles(profileDirectory, "rimliaison-*.json").Length
                : 0;
            return (exitCode, profileCount);
        }
        finally
        {
            Environment.CurrentDirectory = previous!;
            TryDelete(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-profiler-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected {expected}; got {actual}.");
        }
    }
}
