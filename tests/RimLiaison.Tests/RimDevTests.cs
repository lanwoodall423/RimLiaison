using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using RimLiaison;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Provenance;
using RimLiaison.RimDev;

namespace RimLiaison.Tests;

public static class RimDevTests
{
    public static void CleanUpToDateRepo()
    {
        GitRepositoryStateSnapshot state = State("repo", "main", 0, 0, false);
        RimDevPolicyDecision sync = RimDevGitPolicy.DecideSync(state);
        RimDevPolicyDecision push = RimDevGitPolicy.DecidePush(state);
        Assert(sync.Allowed && sync.Action == "current", "A clean up-to-date repository should need no sync.");
        Assert(push.Allowed && push.Action == "current", "A clean up-to-date repository should need no push.");
    }

    public static void AheadOnlyRepo()
    {
        GitRepositoryStateSnapshot state = State("repo", "feature", 2, 0, false);
        Assert(RimDevGitPolicy.DecideSync(state).Action == "current", "An ahead-only repository should not be changed by sync.");
        RimDevPolicyDecision push = RimDevGitPolicy.DecidePush(state);
        Assert(push.Allowed && push.Action == "push", "An ahead-only repository should be pushable.");
    }

    public static void BehindOnlyFastForwardRepo()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 2, false));
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Sync);
        AssertEqual(0, exitCode);
        AssertEqual("ok", result.GetProperty("status").GetString());
        Assert(workspace.Git.Calls.Any(call => call.Arguments.Contains("--ff-only", StringComparer.Ordinal)), "Sync must use fast-forward-only merge.");
    }

    public static void DivergedRepo()
    {
        GitRepositoryStateSnapshot state = State("repo", "feature", 1, 1, false);
        RimDevPolicyDecision sync = RimDevGitPolicy.DecideSync(state);
        RimDevPolicyDecision push = RimDevGitPolicy.DecidePush(state);
        AssertEqual("GIT_DIVERGED", sync.ErrorCode);
        AssertEqual("GIT_DIVERGED", push.ErrorCode);
    }

    public static void DirtyRepo()
    {
        GitRepositoryStateSnapshot aheadDirty = State("repo", "feature", 1, 0, true);
        Assert(RimDevGitPolicy.DecidePush(aheadDirty).Allowed, "Committed ahead work may push while unrelated dirty files remain.");
        GitRepositoryStateSnapshot behindDirty = State("repo", "feature", 0, 1, true);
        AssertEqual("GIT_DIRTY_BEHIND", RimDevGitPolicy.DecideSync(behindDirty).ErrorCode);
    }

    public static void NoUpstream()
    {
        GitRepositoryStateSnapshot state = State("repo", "feature", null, null, false, upstream: null);
        AssertEqual("GIT_UPSTREAM_MISSING", RimDevGitPolicy.DecideSync(state).ErrorCode);
        AssertEqual("GIT_UPSTREAM_MISSING", RimDevGitPolicy.DecidePush(state).ErrorCode);
    }

    public static void GeneratedStateIsIgnored()
    {
        AssertEqual(
            RepositoryChangeClassificationKind.GeneratedTransient,
            RimDevGitReader.ClassifyPath("bin/Release/Repo.dll").Kind);
        AssertEqual(
            RepositoryChangeClassificationKind.GeneratedTransient,
            RimDevGitReader.ClassifyPath("obj/Release/Repo.pdb").Kind);
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/failure-handoffs/promotion.json"), "RimError handoff state must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/observability/events.json"), "RimDev observability state must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/profiles/run.json"), "RimDev profiles must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/qualification/latest.json"), "qualification output must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/qualification/qualified-toolchain-package.json"), "qualification packages must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimdev/validation-proofs/proof.json"), "Validation proofs must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimctx/index.sqlite"), "RimContext indexes must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath("TestResults/result.trx"), "Test result output must be generated.");
        Assert(RimDevGitReader.IsGeneratedPath(".rimerror/failure.json"), "RimError state must be generated.");
        Assert(!RimDevGitReader.IsGeneratedPath(".rimdev/stack.json"), "The stack manifest must remain meaningful configuration.");
        Assert(!RimDevGitReader.IsGeneratedPath("Source/Repo.cs"), "Source files must remain meaningful inputs.");
        Assert(!RimDevGitReader.IsGeneratedPath("random/location/Unknown.dll"), "An arbitrary tracked assembly must not be hidden by its extension.");
        Assert(!RimDevGitReader.IsGeneratedPath("random/location/Unknown.pdb"), "An arbitrary tracked symbol file must not be hidden by its extension.");
    }

    public static void MeaningfulChangeSummaryRetainsUnknownPaths()
    {
        GitRepositoryChange[] changes =
        [
            new(".rimdev/qualification/latest.json", "??", true, true),
            new(".rimdev/stack.json", "M", false, false),
            new("random/location/Unknown.dll", "??", false, false)
        ];

        Assert(RepositoryChangeClassificationPolicy.HasMeaningfulChanges(changes),
            "meaningful source changes must remain visible beside generated qualification output.");
        Assert(
            RepositoryChangeClassificationPolicy.MeaningfulPaths(changes)
                .SequenceEqual([".rimdev/stack.json", "random/location/Unknown.dll"]),
            "meaningful path evidence must retain source and unknown tracked paths.");
        Assert(
            RepositoryChangeClassificationPolicy.MeaningfulPaths(changes, maximum: 1)
                .SequenceEqual([".rimdev/stack.json"]),
            "meaningful path evidence must be bounded deterministically.");
        Assert(
            !RepositoryChangeClassificationPolicy.HasMeaningfulChanges(
                [new GitRepositoryChange(".rimdev/qualification/latest.json", "??", true, true)]),
            "generated qualification output alone must not be meaningful.");
    }

    public static void OwnerAwareClassificationAgreesAcrossConsumers()
    {
        var context = new RepositoryChangeClassificationContext(
            buildOwnedPaths: ["1.6/Assemblies/Fixture.dll"],
            trackedProductionPaths: ["Assemblies/MyMod.dll"]);

        AssertEqual(
            RepositoryChangeClassificationKind.BuildOwnedArtifact,
            RepositoryChangeClassificationPolicy.Classify("1.6/Assemblies/Fixture.dll", context).Kind);
        AssertEqual(
            RepositoryChangeClassificationKind.BuildOwnedArtifact,
            RimDevGitReader.ClassifyPath("1.6/Assemblies/Fixture.dll", context).Kind);
        AssertEqual(
            RepositoryChangeClassificationKind.TrackedProductionArtifact,
            RepositoryChangeClassificationPolicy.Classify("Assemblies/MyMod.dll", context).Kind);
        AssertEqual(
            RepositoryChangeClassificationKind.Unknown,
            RepositoryChangeClassificationPolicy.Classify("random/location/Unknown.dll").Kind);

        ValidationChangeAnalysis analysis = ValidationChangeAnalyzer.Analyze(
        [
            new GitRepositoryChange("bin/Release/Generated.dll", "M", false, true),
            new GitRepositoryChange(".rimdev/stack.json", "M", false, false),
            new GitRepositoryChange("random/location/Unknown.dll", "M", false, false)
        ]);
        Assert(analysis.GeneratedPaths.SequenceEqual(["bin/Release/Generated.dll"]), "Validation must share generated-path classification.");
        Assert(analysis.MeaningfulPaths.SequenceEqual([".rimdev/stack.json", "random/location/Unknown.dll"]), "Validation must retain meaningful and unknown tracked artifacts.");
        Assert(!ValidationChangeAnalyzer.IsGeneratedPath("random/location/Unknown.dll"), "Validation must not apply extension-global artifact rules.");
    }

    public static void GeneratedOnlyWorktreeIsNotReportedDirty()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        GitRepositoryStateSnapshot state = State(workspace.RepoPath, "main", 0, 0, true) with
        {
            Changes = [new GitRepositoryChange(".rimdev/observability/events.json", "M", false, true)]
        };
        workspace.SetState(state);
        workspace.ChangedPaths = [".rimdev/observability/events.json"];

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Status);

        AssertEqual(0, exitCode);
        AssertEqual("ready", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual(false, result.GetProperty("repositories")[0].GetProperty("dirty").GetBoolean());
    }

    public static void BuildFailure()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.BuildFailures.Add(workspace.RepoPath);
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Build);
        AssertEqual(1, exitCode);
        AssertEqual("failed", result.GetProperty("status").GetString());
        AssertEqual("RIMDEV_BUILD_FAILED", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
    }

    public static void TestFailure()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.TestFailures.Add(workspace.RepoPath);
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Test);
        AssertEqual(1, exitCode);
        AssertEqual("failed", result.GetProperty("status").GetString());
        AssertEqual("RIMDEV_TEST_FAILED", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
    }

    public static void InfrastructureBlockedTestCannotPush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.TestInfrastructureBlocks.Add(workspace.RepoPath);
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Infrastructure-blocked validation must not push.");
    }

    public static void FailedTestCannotPushDirectly()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.TestFailures.Add(workspace.RepoPath);
        Run(workspace, RimDevOperation.Test);
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "A failed test must not be bypassed by direct push.");
    }

    public static void InvalidatedEvidenceCannotPush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        string source = Path.Combine(workspace.RepoPath, "Source", "Repo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "v1");
        AssertEqual(0, Run(workspace, RimDevOperation.Test).ExitCode);
        File.WriteAllText(source, "v2");
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Invalidated evidence must not push.");
    }

    public static void MissingCanonicalEvidenceBlocksPush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Missing canonical evidence must block publication.");
    }

    public static void PassingProcessWithoutCanonicalEvidenceIsBlocked()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.SuppressCanonicalEvidence = true;
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Test);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual("RIMDEV_CANONICAL_TEST_EVIDENCE_MISSING", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
    }


    public static void BuildEvidenceAloneCannotAuthorizePush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        string identity = Path.GetFullPath(workspace.RepoPath).ToLowerInvariant();
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        Directory.CreateDirectory(workspace.StateDirectory);
        File.WriteAllText(
            Path.Combine(workspace.StateDirectory, hash + ".json"),
            JsonSerializer.Serialize(new
            {
                repositoryPath = workspace.RepoPath,
                projectPath = "Repo.csproj",
                configuration = "Release",
                headSha = "head",
                changedPathsFingerprint = "legacy-build-inputs",
                identityPaths = new[] { "Source/Repo.cs" },
                outputPath = Path.Combine(workspace.RepoPath, "bin", "Release", "Repo.dll"),
                outputSha256 = new string('a', 64),
                builtAtUtc = DateTimeOffset.UtcNow,
                schemaVersion = "rimdev-build-evidence/v1"
            }));

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Build evidence must not satisfy the canonical test-evidence requirement.");
    }
    public static void DocumentationOnlyChangeCanPush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["README.md"];
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(0, exitCode);
        AssertEqual("pushed", result.GetProperty("repositories")[0].GetProperty("status").GetString());
    }

    public static void DeploymentFailure()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        (int buildExitCode, _) = Run(workspace, RimDevOperation.Build);
        AssertEqual(0, buildExitCode);
        Directory.CreateDirectory(Path.Combine(workspace.DeploymentRoot, "Repo.dll"));
        (int deployExitCode, JsonElement result) = Run(workspace, RimDevOperation.Deploy);
        AssertEqual(3, deployExitCode);
        AssertEqual("RIMDEV_DEPLOYMENT_CONFIGURATION_MISSING", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
        (int pushExitCode, JsonElement push) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, pushExitCode);
        AssertEqual("blocked", push.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "A deployment failure must not be followed by a push.");
    }

    public static void SafePush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, true));
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(0, exitCode);
        AssertEqual("pushed", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Safe push should invoke git push.");
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Contains("--force", StringComparer.Ordinal)), "Safe push must never force-push.");
    }

    public static void RejectedNonFastForwardPush()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 0, 1, false));
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Push);
        AssertEqual(3, exitCode);
        AssertEqual("GIT_NON_FAST_FORWARD", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "A non-fast-forward push must be rejected before git push.");
    }

    public static void MergeCandidateWithPassingChecks()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 0, 0, false, head: "head"));
        workspace.PullRequests.Candidates = [new(
            17,
            "Ready",
            "feature",
            "main",
            "head",
            "base",
            false,
            "MERGEABLE",
            ["SUCCESS"],
            "https://example.test/pr/17")];
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Merge, confirm: true);
        AssertEqual(0, exitCode);
        AssertEqual(true, result.GetProperty("mergePerformed").GetBoolean());
        AssertEqual(1, workspace.PullRequests.MergeCalls);
    }

    public static void RejectedMergeChecks()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 0, 0, false, head: "head"));
        workspace.PullRequests.Candidates = [new(
            18,
            "Pending",
            "feature",
            "main",
            "head",
            "base",
            false,
            "MERGEABLE",
            ["IN_PROGRESS"],
            null)];
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Merge, confirm: true);
        AssertEqual(3, exitCode);
        AssertEqual("MERGE_CHECKS_NOT_PASSING", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
        AssertEqual(0, workspace.PullRequests.MergeCalls);
    }

    public static void AllNeverMerges()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false, head: "head"));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.PullRequests.Candidates = [new(
            19,
            "Ready",
            "feature",
            "main",
            "head",
            "base",
            false,
            "MERGEABLE",
            ["SUCCESS"],
            null)];
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);
        AssertEqual(0, exitCode);
        AssertEqual(false, result.GetProperty("mergePerformed").GetBoolean());
        AssertEqual("ready", result.GetProperty("repositories")[0].GetProperty("merge").GetString());
        AssertEqual(0, workspace.PullRequests.MergeCalls);
    }

    public static void NoChangesAllAvoidsUnnecessaryWork()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);

        AssertEqual(0, exitCode);
        AssertEqual("ok", result.GetProperty("status").GetString());
        AssertEqual("ok", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(result.GetProperty("repositories")[0].GetProperty("summary").GetString()!.Contains("deploy: no affected build inputs", StringComparison.Ordinal), "The no-change summary should report skipped deployment.");
        AssertEqual(0, workspace.Process.BuildCalls);
        AssertEqual(0, workspace.Process.TestCalls);
        AssertEqual(0, workspace.PullRequests.MergeCalls);
        AssertEqual(1, workspace.Git.Calls.Count(call => call.Arguments.Count > 0 && call.Arguments[0] == "fetch"));
        AssertEqual(8, workspace.Git.Calls.Count(call => call.Arguments.Count > 0 && (call.Arguments[0] is "diff" or "diff-tree" or "ls-files")));
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "A no-change workflow must not push.");
        Assert(!File.Exists(Path.Combine(workspace.DeploymentRoot, "Repo.dll")), "A no-change workflow must not deploy an old artifact.");
        AssertEqual(1, workspace.PullRequests.FindCalls);
    }

    public static void FailedHumanSummaryUsesCanonicalStatus()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.Process.BuildFailures.Add(workspace.RepoPath);

        var stdout = new StringWriter();
        var workflow = new RimDevWorkflow(
            workspace.Git,
            workspace.Process,
            workspace.PullRequests,
            workspace.States,
            workspace.StateDirectory);
        int exitCode = workflow.RunAsync(
                new RimDevRunOptions(RimDevOperation.Build, workspace.Root, false, false, workspace.StateDirectory),
                stdout,
                new StringWriter())
            .GetAwaiter()
            .GetResult();

        string output = stdout.ToString();
        AssertEqual(1, exitCode);
        Assert(output.Contains("rimdev build: FAIL", StringComparison.Ordinal), "Human output should use FAIL for an operation failure.");
        Assert(output.Contains("Next: A build failed.", StringComparison.Ordinal), "Human output should provide a direct next action for a failed build.");
    }

    public static void MergeRequiresExactSourceIdentity()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 0, 0, false, head: "head"));
        workspace.PullRequests.Candidates = [new(
            23,
            "Missing source identity",
            "feature",
            "main",
            null,
            "base",
            false,
            "MERGEABLE",
            ["SUCCESS"],
            null)];

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Merge, confirm: true);

        AssertEqual(3, exitCode);
        AssertEqual("MERGE_SOURCE_STALE", result.GetProperty("repositories")[0].GetProperty("errorCode").GetString());
        AssertEqual(0, workspace.PullRequests.MergeCalls);
    }

    public static void PartialMultiRepositoryFailure()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Good", "Broken");
        workspace.SetState(State(Path.Combine(workspace.Root, "Good"), "main", 0, 0, false));
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Status);
        AssertEqual(3, exitCode);
        AssertEqual(2, result.GetProperty("repositories").GetArrayLength());
        Assert(result.GetProperty("repositories").EnumerateArray().Any(value => value.GetProperty("status").GetString() == "blocked"), "A failed repository must be summarized as blocked.");
    }

    public static void AffectedOnlyBuildAndTestSelection()
    {
        using TestWorkspace clean = TestWorkspace.Create("Repo");
        clean.SetState(State(clean.RepoPath, "main", 0, 0, false));
        clean.ChangedPaths = ["README.md"];
        (int buildExitCode, JsonElement build) = Run(clean, RimDevOperation.Build);
        (int testExitCode, JsonElement test) = Run(clean, RimDevOperation.Test);
        AssertEqual(0, buildExitCode);
        AssertEqual(0, testExitCode);
        AssertEqual("skip", build.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual("skip", test.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual(0, clean.Process.BuildCalls);
        AssertEqual(0, clean.Process.TestCalls);

        using TestWorkspace affected = TestWorkspace.Create("Repo");
        affected.SetState(State(affected.RepoPath, "main", 0, 0, false));
        affected.ChangedPaths = ["Source/Repo.cs"];
        Run(affected, RimDevOperation.Build);
        Run(affected, RimDevOperation.Test);
        AssertEqual(1, affected.Process.BuildCalls);
        AssertEqual(1, affected.Process.TestCalls);
    }

    public static void DependencyChangesSelectDownstreamInOrder()
    {
        using TestWorkspace buildWorkspace = TestWorkspace.Create("Consumer", "Framework");
        buildWorkspace.ConfigureDependencies(("Consumer", ["Framework"]));
        buildWorkspace.SetState(State(Path.Combine(buildWorkspace.Root, "Consumer"), "main", 0, 0, false));
        buildWorkspace.SetState(State(Path.Combine(buildWorkspace.Root, "Framework"), "main", 0, 0, false));
        buildWorkspace.SetChangedPaths("Framework", "Source/Framework.cs");

        (int buildExitCode, JsonElement build) = Run(buildWorkspace, RimDevOperation.Build);

        AssertEqual(0, buildExitCode);
        AssertEqual(2, buildWorkspace.Process.BuildCalls);
        AssertSequence(
            [Path.Combine(buildWorkspace.Root, "Framework"), Path.Combine(buildWorkspace.Root, "Consumer")],
            buildWorkspace.Process.BuildRepositories);
        Assert(build.GetProperty("repositories").EnumerateArray().All(value => value.GetProperty("status").GetString() == "pass"), "A changed framework should select and build its downstream consumer.");

        using TestWorkspace testWorkspace = TestWorkspace.Create("Consumer", "Framework");
        testWorkspace.ConfigureDependencies(("Consumer", ["Framework"]));
        testWorkspace.SetState(State(Path.Combine(testWorkspace.Root, "Consumer"), "main", 0, 0, false));
        testWorkspace.SetState(State(Path.Combine(testWorkspace.Root, "Framework"), "main", 0, 0, false));
        testWorkspace.SetChangedPaths("Framework", "Source/Framework.cs");

        (int testExitCode, JsonElement test) = Run(testWorkspace, RimDevOperation.Test);

        AssertEqual(0, testExitCode);
        AssertEqual(2, testWorkspace.Process.TestCalls);
        AssertSequence(
            [Path.Combine(testWorkspace.Root, "Framework"), Path.Combine(testWorkspace.Root, "Consumer")],
            testWorkspace.Process.TestRepositories);
        Assert(test.GetProperty("repositories").EnumerateArray().All(value => value.GetProperty("status").GetString() == "pass"), "A changed framework should select downstream consumer tests.");
    }

    public static void OneChangedLeafSelectsOnlyThatRepository()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Leaf", "Other");
        workspace.SetState(State(Path.Combine(workspace.Root, "Leaf"), "main", 0, 0, false));
        workspace.SetState(State(Path.Combine(workspace.Root, "Other"), "main", 0, 0, false));
        workspace.SetChangedPaths("Leaf", "Source/Leaf.cs");

        (int buildExitCode, JsonElement build) = Run(workspace, RimDevOperation.Build);
        (int testExitCode, JsonElement test) = Run(workspace, RimDevOperation.Test);

        AssertEqual(0, buildExitCode);
        AssertEqual(0, testExitCode);
        AssertEqual(1, workspace.Process.BuildCalls);
        AssertEqual(Path.Combine(workspace.Root, "Leaf"), workspace.Process.BuildRepositories[0]);
        AssertEqual(1, workspace.Process.TestCalls);
        AssertEqual(Path.Combine(workspace.Root, "Leaf"), workspace.Process.TestRepositories[0]);
        Assert(build.GetProperty("repositories").EnumerateArray().Any(value => value.GetProperty("name").GetString() == "Other" && value.GetProperty("status").GetString() == "skip"), "An unrelated repository should be skipped.");
        Assert(test.GetProperty("repositories").EnumerateArray().Any(value => value.GetProperty("name").GetString() == "Other" && value.GetProperty("status").GetString() == "skip"), "An unrelated repository test should be skipped.");
    }

    public static void DirtySyncPreservesWorkAndContinues()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Blocked", "Ready");
        workspace.SetState(State(Path.Combine(workspace.Root, "Blocked"), "feature", 0, 1, true));
        workspace.SetState(State(Path.Combine(workspace.Root, "Ready"), "main", 0, 1, false));
        string sentinel = Path.Combine(workspace.Root, "Blocked", "user-work.txt");
        File.WriteAllText(sentinel, "keep this work");

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Sync);

        AssertEqual(3, exitCode);
        AssertEqual("blocked", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual("synchronized", result.GetProperty("repositories")[1].GetProperty("status").GetString());
        AssertEqual("keep this work", File.ReadAllText(sentinel));
        AssertEqual(1, workspace.Git.Calls.Count(call => call.Arguments.Contains("--ff-only", StringComparer.Ordinal)));
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Contains("reset", StringComparer.Ordinal) || call.Arguments.Contains("--force", StringComparer.Ordinal)), "Dirty sync must not reset or force-update a repository.");
    }

    public static void AllFailureBlocksOnlyFailedDeployment()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Broken", "Good");
        workspace.SetState(State(Path.Combine(workspace.Root, "Broken"), "main", 0, 0, false));
        workspace.SetState(State(Path.Combine(workspace.Root, "Good"), "main", 0, 0, false));
        workspace.SetChangedPaths("Broken", "Source/Broken.cs");
        workspace.SetChangedPaths("Good", "Source/Good.cs");
        workspace.Process.TestFailures.Add(Path.Combine(workspace.Root, "Broken"));

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);

        AssertEqual(1, exitCode);
        JsonElement[] repositories = result.GetProperty("repositories").EnumerateArray().ToArray();
        JsonElement broken = repositories.Single(value => value.GetProperty("name").GetString() == "Broken");
        JsonElement good = repositories.Single(value => value.GetProperty("name").GetString() == "Good");
        AssertEqual("fail", broken.GetProperty("status").GetString());
        AssertEqual("ok", good.GetProperty("status").GetString());
        Assert(broken.GetProperty("summary").GetString()!.Contains("deploy: Deployment was skipped", StringComparison.Ordinal), "A failed test must block deployment for that repository.");
        Assert(good.GetProperty("summary").GetString()!.Contains("deploy: deployment was proven", StringComparison.Ordinal), "An unrelated passing repository should retain its successful deployment evidence.");
        Assert(result.GetProperty("nextActions").EnumerateArray().Any(value => value.GetString()!.Contains("test failed", StringComparison.OrdinalIgnoreCase)), "The failed component should have a direct next action.");
    }

    public static void FailedDependencyBlocksDependentPublication()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Consumer", "Framework");
        workspace.ConfigureDependencies(("Consumer", ["Framework"]));
        workspace.SetState(State(Path.Combine(workspace.Root, "Consumer"), "feature", 1, 0, false));
        workspace.SetState(State(Path.Combine(workspace.Root, "Framework"), "feature", 1, 0, false));
        workspace.SetChangedPaths("Consumer", "Source/Consumer.cs");
        workspace.SetChangedPaths("Framework", "Source/Framework.cs");
        workspace.Process.TestFailures.Add(Path.Combine(workspace.Root, "Framework"));
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);
        AssertEqual(1, exitCode);
        JsonElement consumer = result.GetProperty("repositories").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "Consumer");
        AssertEqual("fail", result.GetProperty("repositories").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "Framework")
            .GetProperty("status").GetString());
        AssertEqual("blocked", consumer.GetProperty("status").GetString());
        Assert(!workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "A failed dependency must prevent dependent publication.");
    }

    public static void TrustworthyTestEvidenceIsReused()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "main", 0, 0, false));
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        (int firstExitCode, _) = Run(workspace, RimDevOperation.Test);
        AssertEqual(0, firstExitCode);
        int callsAfterFirstRun = workspace.Process.TestCalls;
        (int secondExitCode, JsonElement result) = Run(workspace, RimDevOperation.Test);
        AssertEqual(0, secondExitCode);
        AssertEqual(callsAfterFirstRun, workspace.Process.TestCalls);
        AssertEqual("reused", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        int callsBeforePush = workspace.Process.TestCalls;
        (int pushExitCode, JsonElement push) = Run(workspace, RimDevOperation.Push);
        AssertEqual(0, pushExitCode);
        AssertEqual("pushed", push.GetProperty("repositories")[0].GetProperty("status").GetString());
        AssertEqual(callsBeforePush, workspace.Process.TestCalls);
    }

    public static void LegacyTestEvidenceCannotAuthorizeReuse()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        string identity = Path.GetFullPath(workspace.RepoPath).ToLowerInvariant();
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        Directory.CreateDirectory(workspace.StateDirectory);
        File.WriteAllText(
            Path.Combine(workspace.StateDirectory, hash + ".test.json"),
            JsonSerializer.Serialize(new
            {
                repositoryPath = workspace.RepoPath,
                headSha = "head",
                sourceIdentity = "legacy-source-identity",
                deployed = new[] { "legacy-artifact" },
                recordedAtUtc = DateTimeOffset.UtcNow,
                schemaVersion = "rimdev-test-evidence/v1"
            }));

        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.Test);
        AssertEqual(0, exitCode);
        AssertEqual(1, workspace.Process.TestCalls);
        AssertEqual("pass", result.GetProperty("repositories")[0].GetProperty("status").GetString());
    }

    public static void CanonicalEvidenceIsReusedByAll()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 1, 0, false));
        workspace.ChangedPaths = ["Source/Repo.cs"];
        workspace.RecordPassingValidation(workspace.RepoPath);
        (int exitCode, JsonElement result) = Run(workspace, RimDevOperation.All);
        AssertEqual(0, exitCode);
        AssertEqual(0, workspace.Process.TestCalls);
        AssertEqual("ok", result.GetProperty("repositories")[0].GetProperty("status").GetString());
        Assert(workspace.Git.Calls.Any(call => call.Arguments.Count > 0 && call.Arguments[0] == "push"), "Canonical reusable evidence should permit All to push without rerunning tests.");
    }


    public static void MergeConfirmationDefaultsToNo()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature/ui-overhaul", 0, 0, false, head: "head"));
        workspace.PullRequests.Candidates = [new(
            42,
            "UI overhaul",
            "feature/ui-overhaul",
            "main",
            "head",
            "base",
            false,
            "MERGEABLE",
            ["SUCCESS"],
            null)];

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var workflow = new RimDevWorkflow(
            workspace.Git,
            workspace.Process,
            workspace.PullRequests,
            workspace.States,
            workspace.StateDirectory);
        int exitCode = workflow.RunAsync(
                new RimDevRunOptions(
                    RimDevOperation.Merge,
                    workspace.Root,
                    Confirm: false,
                    Json: false,
                    StateDirectory: workspace.StateDirectory,
                    Input: new StringReader("\n")),
                stdout,
                stderr)
            .GetAwaiter()
            .GetResult();

        string output = stdout.ToString();
        AssertEqual(3, exitCode);
        AssertEqual(0, workspace.PullRequests.MergeCalls);
        Assert(output.Contains("Repository: Repo", StringComparison.Ordinal), "Merge confirmation should identify the repository.");
        Assert(output.Contains("PR: #42", StringComparison.Ordinal), "Merge confirmation should identify the pull request.");
        Assert(output.Contains("From: feature/ui-overhaul", StringComparison.Ordinal), "Merge confirmation should identify the source branch.");
        Assert(output.Contains("Into: main", StringComparison.Ordinal), "Merge confirmation should identify the target branch.");
        Assert(output.Contains("Checks: PASS", StringComparison.Ordinal), "Merge confirmation should show passing checks.");
        Assert(output.Contains("Merge this work into main? [y/N]", StringComparison.Ordinal), "Merge confirmation should default to No.");
        Assert(output.Contains("No merge was performed", StringComparison.Ordinal), "Declining a merge should be explicit.");
    }

    public static void MultipleMergeCandidatesRequireExplicitSelection()
    {
        using TestWorkspace workspace = TestWorkspace.Create("Repo");
        workspace.SetState(State(workspace.RepoPath, "feature", 0, 0, false, head: "head"));
        workspace.PullRequests.Candidates = [
            new(41, "First choice", "feature", "main", "head", "base", false, "MERGEABLE", ["SUCCESS"], null),
            new(42, "Second choice", "feature", "main", "head", "base", false, "MERGEABLE", ["SUCCESS"], null)];

        var stdout = new StringWriter();
        var workflow = new RimDevWorkflow(
            workspace.Git,
            workspace.Process,
            workspace.PullRequests,
            workspace.States,
            workspace.StateDirectory);
        int exitCode = workflow.RunAsync(
                new RimDevRunOptions(
                    RimDevOperation.Merge,
                    workspace.Root,
                    Confirm: false,
                    Json: false,
                    StateDirectory: workspace.StateDirectory,
                    Input: new StringReader("\n")),
                stdout,
                new StringWriter())
            .GetAwaiter()
            .GetResult();

        string output = stdout.ToString();
        AssertEqual(3, exitCode);
        AssertEqual(0, workspace.PullRequests.MergeCalls);
        Assert(output.Contains("More than one pull request", StringComparison.Ordinal), "Ambiguous candidates should be shown before any merge action.");
        Assert(output.Contains("No merge was selected", StringComparison.Ordinal), "Ambiguous merge selection should default to no action.");
    }

    public static void CliNoArgumentAndHelpAreBeginnerFriendly()
    {
        var menu = new StringWriter();
        int menuExitCode = CliApplication.Run(["rimdev"], menu, new StringWriter());
        AssertEqual(0, menuExitCode);
        Assert(menu.ToString().Contains("rimdev status", StringComparison.Ordinal), "rimdev with no operation should show the beginner menu.");

        var help = new StringWriter();
        int helpExitCode = CliApplication.Run(["rimdev", "help"], help, new StringWriter());
        AssertEqual(0, helpExitCode);
        string helpText = help.ToString();
        Assert(helpText.Contains("Check GitHub for newer work", StringComparison.Ordinal), "rimdev help should explain sync in plain language.");
        Assert(helpText.Contains("Default is No", StringComparison.Ordinal), "rimdev help should explain safe merge confirmation.");
        Assert(helpText.Contains("local work", StringComparison.Ordinal), "rimdev help should explain the local-work safety rule.");
    }

    public static void WindowsWrapperForwardsArgumentsFromAnotherFolder()
    {
        string repositoryRoot = FindRepositoryRoot();
        string wrapper = Path.Combine(repositoryRoot, "rimdev.cmd");
        Assert(File.Exists(wrapper), "The root rimdev.cmd wrapper must exist.");

        string temporary = Path.Combine(Path.GetTempPath(), "rimdev launcher test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                WorkingDirectory = temporary,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "/d /c call \"" + wrapper + "\" help"
            };

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the wrapper process.");
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new InvalidOperationException("The wrapper did not finish within 30 seconds.");
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            AssertEqual(0, process.ExitCode);
            Assert(output.Contains("RimDev help", StringComparison.Ordinal), "The wrapper should forward the help operation from another folder.");
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); } catch (Exception) { }
        }
    }

    private static (int ExitCode, JsonElement Result) Run(
        TestWorkspace workspace,
        RimDevOperation operation,
        bool confirm = false)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var workflow = new RimDevWorkflow(
            workspace.Git,
            workspace.Process,
            workspace.PullRequests,
            workspace.States,
            workspace.StateDirectory,
            workspace.Observability);
        int exitCode = workflow.RunAsync(
                new RimDevRunOptions(operation, workspace.Root, confirm, true, workspace.StateDirectory),
                stdout,
                stderr)
            .GetAwaiter()
            .GetResult();
        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        return (exitCode, document.RootElement.Clone());
    }

    private static GitRepositoryStateSnapshot State(
        string root,
        string? branch,
        int? ahead,
        int? behind,
        bool dirty,
        string? head = "head",
        string? upstream = "origin/main") =>
        new(
            root,
            "git:test-" + root,
            branch,
            head,
            upstream is null ? null : "remote",
            ahead,
            behind,
            dirty,
            [],
            null,
            upstream);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "rimdev.cmd")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the RimLiaison repository root.");
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
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }

    private static void AssertSequence(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        AssertEqual(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertEqual(expected[index], actual[index]);
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root, string stateDirectory, IReadOnlyList<string> repositories)
        {
            Root = root;
            StateDirectory = stateDirectory;
            Repositories = repositories;
            DeploymentRoot = Path.Combine(root, "deploy");
            Directory.CreateDirectory(DeploymentRoot);
            States = new FixedStateProvider();
            Observability = new AgentObservabilityStore();
            Git = new FakeGitClient(this);
            Process = new FakeProcessRunner(this);
            PullRequests = new FakePullRequestProvider();
        }

        public string Root { get; }
        public string StateDirectory { get; }
        public IReadOnlyList<string> Repositories { get; }
        public string RepoPath => Path.Combine(Root, Repositories[0]);
        public string DeploymentRoot { get; }
        public FixedStateProvider States { get; }
        public AgentObservabilityStore Observability { get; }
        public FakeGitClient Git { get; }
        public FakeProcessRunner Process { get; }
        public FakePullRequestProvider PullRequests { get; }

        public string[] ChangedPaths { get; set; } = [];
        public Dictionary<string, string[]> ChangedPathsByRepository { get; } = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string[]> DependencyMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static TestWorkspace Create(params string[] repositories)
        {
            string root = Path.Combine(Path.GetTempPath(), "rimdev-tests-" + Guid.NewGuid().ToString("N"));
            string stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(root);
            var workspace = new TestWorkspace(root, stateDirectory, repositories);
            foreach (string repository in repositories)
            {
                string path = Path.Combine(root, repository);
                Directory.CreateDirectory(path);
                Directory.CreateDirectory(Path.Combine(path, ".rimdev"));
                File.WriteAllText(
                    Path.Combine(path, ".rimdev", "stack.json"),
                    "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"" + repository + "\",\"catalog\":\"TestCatalog/rimtest.catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}");
                File.WriteAllText(
                    Path.Combine(path, repository + ".csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>" + repository + "</AssemblyName></PropertyGroup></Project>");
            }

            Directory.CreateDirectory(Path.Combine(root, ".rimdev"));
            workspace.WriteConfiguration();
            return workspace;
        }

        public void ConfigureDependencies(params (string Repository, string[] DependsOn)[] dependencies)
        {
            foreach ((string repository, string[] dependsOn) in dependencies)
            {
                DependencyMap[repository] = dependsOn;
            }

            WriteConfiguration();
        }

        public void SetChangedPaths(string repository, params string[] paths) =>
            ChangedPathsByRepository[Path.GetFullPath(Path.Combine(Root, repository))] = paths;

        public string[] ChangedPathsFor(string repositoryPath) =>
            ChangedPathsByRepository.TryGetValue(Path.GetFullPath(repositoryPath), out string[]? paths)
                ? paths
                : ChangedPaths;

        private void WriteConfiguration()
        {
            var config = new
            {
                schemaVersion = RimDevSchemas.Workspace,
                deploymentRoot = "deploy",
                repositories = Repositories.Select(repository => new
                {
                    path = repository,
                    dependsOn = DependencyMap.TryGetValue(repository, out string[]? dependsOn)
                        ? dependsOn
                        : Array.Empty<string>(),
                    deploymentTarget = repository + ".dll"
                }).ToArray()
            };
            File.WriteAllText(Path.Combine(Root, ".rimdev", "workspace.json"), JsonSerializer.Serialize(config));
        }

        public void SetState(GitRepositoryStateSnapshot state)
        {
            string path = Path.GetFullPath(state.RootPath);
            States.Results[path] = new GitRepositoryStateResult(true, state);
        }


        public void RecordPassingValidation(string repositoryPath)
        {
            string root = Path.GetFullPath(repositoryPath);
            GitRepositoryStateSnapshot state = States.Results[root].State!;
            string modId = Path.GetFileName(root);
            IReadOnlyList<GitRepositoryChange> changes = ChangedPathsFor(root)
                .Select(path => new GitRepositoryChange(path, "M", false, RimDevGitReader.IsGeneratedPath(path)))
                .ToArray();
            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["catalog"] = Path.GetFullPath(Path.Combine(root, "TestCatalog/rimtest.catalog.json")),
                ["devBridgeProject"] = "unknown",
                ["fallbackSuite"] = "smoke"
            };
            using var run = new AgentObservabilityRun("rimdev-test-" + Guid.NewGuid().ToString("N"), Observability);
            AgentObservabilitySession agent = run.CreateAgent(modId, modId);
            using IDisposable activation = agent.Activate();
            agent.Start("fake affected validation");
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.SuiteCompleted,
                "Fake affected validation completed.",
                new
                {
                    selectedTests = new[] { "smoke" },
                    artifactFreshness = new
                    {
                        generation = 1,
                        builtArtifactSha256 = "built",
                        deployedArtifactSha256 = "deployed",
                        evaluationStatus = "FRESH"
                    }
                });
            ValidationPublicationCheck check = ValidationPublicationChecker.Evaluate(
                state,
                changes,
                Observability,
                modId,
                configuration);
            ValidationEvidenceRecord evidence = ValidationEvidenceRecord.Create(
                check.CurrentIdentity,
                "pass",
                DateTimeOffset.UtcNow);
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.ValidationEvidenceRecorded,
                "Fake immutable validation evidence recorded.",
                new { validationEvidence = evidence });
        }

        public void Dispose()
        {
            Observability.Dispose();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class FixedStateProvider : IGitRepositoryStateProvider
    {
        public Dictionary<string, GitRepositoryStateResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<GitRepositoryStateResult> ReadAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Results.TryGetValue(Path.GetFullPath(rootPath), out GitRepositoryStateResult? result)
                ? result
                : new GitRepositoryStateResult(false, ErrorCode: "GIT_TEST_STATE_MISSING", Error: "No fake state was configured."));
    }

    private sealed class FakeGitClient : IRimDevGitClient
    {
        private readonly TestWorkspace workspace;

        public FakeGitClient(TestWorkspace workspace)
        {
            this.workspace = workspace;
        }

        public List<(string Repository, IReadOnlyList<string> Arguments)> Calls { get; } = [];
        public HashSet<string> FetchFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<RimDevGitResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add((repositoryPath, arguments.ToArray()));
            string command = arguments.Count == 0 ? string.Empty : arguments[0];
            if (command == "remote")
            {
                return Task.FromResult(new RimDevGitResult(true, 0, "origin\n", string.Empty));
            }

            if (command is "diff" or "diff-tree" or "ls-files")
            {
                string[] changedPaths = workspace.ChangedPathsFor(repositoryPath);
                string output = string.Join('\0', changedPaths) + (changedPaths.Length > 0 ? "\0" : string.Empty);
                return Task.FromResult(new RimDevGitResult(true, 0, output, string.Empty));
            }

            if (command == "fetch" && FetchFailures.Contains(repositoryPath))
            {
                return Task.FromResult(RimDevGitResult.Failure("GIT_FETCH_FAILED", "fake fetch failure"));
            }

            if (command == "rev-parse")
            {
                return Task.FromResult(new RimDevGitResult(true, 0, "base\n", string.Empty));
            }

            return Task.FromResult(new RimDevGitResult(true, 0, string.Empty, string.Empty));
        }
    }

    private sealed class FakeProcessRunner : IRimDevProcessRunner
    {
        private readonly TestWorkspace workspace;

        public FakeProcessRunner(TestWorkspace workspace)
        {
            this.workspace = workspace;
        }
        public HashSet<string> BuildFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TestFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TestInfrastructureBlocks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool SuppressCanonicalEvidence { get; set; }
        public int BuildCalls { get; private set; }
        public int TestCalls { get; private set; }
        public List<string> BuildRepositories { get; } = [];
        public List<string> TestRepositories { get; } = [];
        public Task<RimDevProcessResult> RunAsync(string workingDirectory, string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.Contains("affected", StringComparer.Ordinal))
            {
                TestCalls++;
                TestRepositories.Add(workingDirectory);
                if (TestFailures.Contains(workingDirectory))
                {
                    return Task.FromResult(new RimDevProcessResult(1, "{\"status\":\"fail\"}", string.Empty));
                }
                if (TestInfrastructureBlocks.Contains(workingDirectory))
                {
                    return Task.FromResult(new RimDevProcessResult(2, "{\"status\":\"blocked\"}", "fake infrastructure refusal"));
                }

                if (!SuppressCanonicalEvidence)
                {
                    workspace.RecordPassingValidation(workingDirectory);
                }

                return Task.FromResult(new RimDevProcessResult(0, "{\"status\":\"pass\",\"artifactFreshness\":{\"loadedArtifactFreshnessProven\":true}}", string.Empty));
            }

            if (arguments.Count > 0 && arguments[0] == "build")
            {
                BuildCalls++;
                BuildRepositories.Add(workingDirectory);
                if (BuildFailures.Contains(workingDirectory))
                {
                    return Task.FromResult(new RimDevProcessResult(1, string.Empty, "fake build failure"));
                }

                string project = arguments.Count > 1 ? arguments[1] : string.Empty;
                string name = Path.GetFileNameWithoutExtension(project);
                string outputDirectory = Path.Combine(Path.GetDirectoryName(project) ?? workingDirectory, "bin", "Release");
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, name + ".dll"), "fake output");
                return Task.FromResult(new RimDevProcessResult(0, "build succeeded", string.Empty));
            }

            return Task.FromResult(new RimDevProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FakePullRequestProvider : IRimDevPullRequestProvider
    {
        public IReadOnlyList<RimDevPullRequest> Candidates { get; set; } = [];
        public int FindCalls { get; private set; }
        public int MergeCalls { get; private set; }

        public Task<RimDevPullRequestQueryResult> FindAsync(string repositoryPath, string branch, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult(new RimDevPullRequestQueryResult(true, Candidates));
        }

        public Task<RimDevProcessResult> MergeAsync(string repositoryPath, RimDevPullRequest pullRequest, CancellationToken cancellationToken = default)
        {
            MergeCalls++;
            return Task.FromResult(new RimDevProcessResult(0, string.Empty, string.Empty));
        }
    }
}
