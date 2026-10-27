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
            Assert(!CliDeploymentManifestService.Verify(
                    root,
                    manifestPath,
                    manifestHash,
                    manifest.PackageSha256,
                    out _,
                    out string? missingError) &&
                missingError?.Contains(
                    "CLI deployment closure mismatch: unexpected=[], missing=[dependency.json]",
                    StringComparison.Ordinal) == true,
                "missing CLI dependency must report bounded missing-file evidence");
            File.WriteAllText(Path.Combine(root, "dependency.json"), "substituted");
            Assert(!CliDeploymentManifestService.Verify(
                    root,
                    manifestPath,
                    manifestHash,
                    manifest.PackageSha256,
                    out _,
                    out _), "substituted CLI dependency must fail hash verification");
            File.WriteAllText(Path.Combine(root, "dependency.json"), "dependency");
            File.WriteAllText(Path.Combine(root, "extra.dll"), "unlisted");
            Assert(!CliDeploymentManifestService.Verify(
                    root,
                    manifestPath,
                    manifestHash,
                    manifest.PackageSha256,
                    out _,
                    out string? unexpectedError) &&
                unexpectedError?.Contains(
                    "CLI deployment closure mismatch: unexpected=[extra.dll], missing=[]",
                    StringComparison.Ordinal) == true,
                "unlisted CLI file must report bounded unexpected-file evidence");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }


    public static void NativeRimtestCandidatePackagingCommandIsStrictAndInertOnBadInput()
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rimtest-source-fixture"));
        string published = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rimtest-publish-fixture"));
        string commit = new('a', 40);
        string[] valid =
        [
            NativeRimtestPackageCandidateCommand.Name,
            source,
            published,
            commit,
            "--json"
        ];

        Assert(NativeRimtestPackageCandidateCommand.IsRequested(valid) &&
               NativeRimtestPackageCandidateCommand.TryParse(valid,
                   out NativeRimtestPackageCandidateCommand.Request? parsed) &&
               parsed is not null &&
               parsed.SourceRoot == source &&
               parsed.PublishedCliRoot == published &&
               parsed.SourceCommit == commit,
            "explicit candidate packaging must require two absolute paths and a canonical source revision");
        Assert(!NativeRimtestPackageCandidateCommand.IsRequested(
                   ["dev-check", "--json"]),
            "ordinary dev-check must not enter the candidate packaging route");

        foreach (string[] denied in new[]
                 {
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, published, commit },
                     new[] { NativeRimtestPackageCandidateCommand.Name, "relative", published, commit, "--json" },
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, "relative", commit, "--json" },
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, published,
                         commit.ToUpperInvariant(), "--json" },
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, published,
                         "not-a-source-revision", "--json" },
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, published,
                         commit, "--install" },
                     new[] { NativeRimtestPackageCandidateCommand.Name, source, published,
                         commit, "--json", "--force" }
                 })
        {
            Assert(!NativeRimtestPackageCandidateCommand.TryParse(denied, out _),
                "candidate packaging accepted incomplete, relative, uncanonical, or install arguments");
        }

        using StringWriter output = new();
        int exitCode = NativeRimtestPackageCandidateCommand.RunAsync(
                [NativeRimtestPackageCandidateCommand.Name, "relative", published, commit, "--json"],
                output,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        using JsonDocument rejected = JsonDocument.Parse(output.ToString());
        JsonElement result = rejected.RootElement;
        Assert(exitCode == CliExitCodes.ConservativeSelection &&
               result.GetProperty("status").GetString() == "blocked" &&
               result.GetProperty("code").GetString() == "RIMTEST_CANDIDATE_ARGUMENTS_INVALID" &&
               !result.GetProperty("qualified").GetBoolean() &&
               !result.GetProperty("installed").GetBoolean(),
            "invalid package requests must report bounded blocked JSON without invoking staging");

        NativeRimtestRunner.Dispatch? native = NativeRimtestRunner.Prepare(
            Path.Combine(Path.GetTempPath(), "rimtest.exe"), valid, source);
        Assert(native is { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
            "the restricted native runner must never expose candidate packaging or installation");
    }

    public static void NativeRimtestRunnerBindsInvocationWorkspaceOnly()
    {
        (string root, string runner, string workspace, string alternate) = CreateNativeRunnerFixture();
        try
        {
            NativeRimtestRunner.Dispatch? implicitBinding = NativeRimtestRunner.Prepare(
                runner, ["dev-check", "--json"], workspace);
            Assert(implicitBinding is { Allowed: true } &&
                   implicitBinding.Args.SequenceEqual(["dev-check", workspace, "--json"]) &&
                   implicitBinding.SourceCommit == new string('a', 40) &&
                   implicitBinding.CliAssemblySha256?.Length == 64 &&
                   implicitBinding.RunnerExeSha256?.Length == 64,
                "native runner did not bind to its invocation workspace and exact package hashes");

            string relative = Path.GetRelativePath(workspace, alternate);
            NativeRimtestRunner.Dispatch? explicitBinding = NativeRimtestRunner.Prepare(
                runner, ["dev-check", relative, "--json"], workspace);
            Assert(explicitBinding is { Allowed: true } &&
                   explicitBinding.Args.SequenceEqual(["dev-check", alternate, "--json"]),
                "native runner did not honor a deliberately explicit mod workspace");

            Assert(NativeRimtestRunner.Prepare(
                       Path.Combine(root, "rimliaison.exe"),
                       ["qualification", "promote", "--json"], workspace) is null &&
                   NativeRimtestRunner.Prepare(
                       Path.Combine(root, "rimtest.cmd"),
                       ["dev-check", "--json"], workspace) is null,
                "ordinary production CLI or legacy batch alias was re-routed as a native runner");

            foreach (string[] forbidden in new[]
                     {
                         new[] { "qualification", "promote", "--json" },
                         new[] { "dev-check", "--experimental", "--json" },
                         new[] { "dev-check", workspace, alternate, "--json" },
                         new[] { "dev-check", "--json", "--json" },
                         new[] { "dev-check" },
                         new[] { "dev-check", "--json", "--help" },
                     })
            {
                NativeRimtestRunner.Dispatch? blocked = NativeRimtestRunner.Prepare(
                    runner, forbidden, workspace);
                Assert(blocked is { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
                    "native dev runner admitted a non-dev-check or ambiguous invocation");
            }

            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], root) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_WORKSPACE_INVALID" },
                "runner silently selected its own package/source directory as a mod workspace");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeRimtestRunnerRejectsMissingOrChangedPackage()
    {
        (string root, string runner, string workspace, _) = CreateNativeRunnerFixture();
        try
        {
            NativeRimtestRunner.Dispatch? valid = NativeRimtestRunner.Prepare(
                runner, ["dev-check", "--json"], workspace);
            Assert(valid is { Allowed: true }, "controlled native package fixture did not verify");

            string metadata = Path.Combine(Path.GetDirectoryName(runner)!, "rimtest-dev-runner.json");
            string assembly = Path.Combine(Path.GetDirectoryName(runner)!, "rimliaison.dll");
            string originalManifest = File.ReadAllText(metadata);
            string originalExe = File.ReadAllText(runner);

            File.WriteAllText(runner, "tampered native apphost");
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "changed runner executable was treated as verified");
            File.WriteAllText(runner, originalExe);

            File.WriteAllText(assembly, "tampered CLI assembly");
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "changed CLI assembly was treated as verified");
            File.WriteAllText(assembly, "fixture CLI assembly");

            File.WriteAllText(metadata, "{ malformed JSON");
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "malformed runner provenance was accepted");

            File.WriteAllText(metadata, originalManifest[..^1] +
                ",\"sourceCommit\":\"" + new string('b', 40) + "\"}");
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "duplicate manifest key was accepted");

            File.WriteAllText(metadata, originalManifest.Replace(
                new string('a', 40), new string('A', 40), StringComparison.Ordinal));
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "noncanonical claimed source commit was accepted");

            File.Delete(metadata);
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "missing provenance sidecar was accepted");

            File.WriteAllText(metadata, originalManifest);
            Assert(NativeRimtestRunner.Prepare(runner, ["dev-check", "--json"], workspace) is
            { Allowed: true },
                "valid original runner package did not recover after fixture restoration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string Root, string Runner, string Workspace, string Alternate)
        CreateNativeRunnerFixture()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-runner-fixture-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "package");
        string workspace = Path.Combine(root, "mod-a");
        string alternate = Path.Combine(root, "mod-b");
        Directory.CreateDirectory(package);
        foreach (string mod in new[] { workspace, alternate })
        {
            Directory.CreateDirectory(Path.Combine(mod, "About"));
            File.WriteAllText(
                Path.Combine(mod, "About", "About.xml"),
                "<ModMetaData><name>Fixture</name><packageId>fixture.mod</packageId></ModMetaData>");
        }

        string runner = Path.Combine(package, "rimtest.exe");
        string assembly = Path.Combine(package, "rimliaison.dll");
        File.WriteAllText(runner, "fixture native apphost");
        File.WriteAllText(assembly, "fixture CLI assembly");
        File.WriteAllText(Path.Combine(package, "rimliaison.deps.json"), "{}");
        File.WriteAllText(Path.Combine(package, "rimliaison.runtimeconfig.json"), "{}");

        static string Hash(string path)
        {
            using FileStream input = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input))
                .ToLowerInvariant();
        }

        File.WriteAllText(
            Path.Combine(package, "rimtest-dev-runner.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "rimtest-dev-runner/v1",
                sourceCommit = new string('a', 40),
                runnerExeSha256 = Hash(runner),
                cliAssemblySha256 = Hash(assembly),
            }));
        CliDeploymentManifestService.Write(package, new string('a', 40), "net8.0");
        return (root, runner, workspace, alternate);
    }

    public static void NativeRunnerCandidatePackageVerifiesFullClosure()
    {
        (string root, string source, string publish, string packages) = NativePackageFixture();
        try
        {
            string commit = new('a', 40);
            var state = new FixtureNativeGitState(source, commit);
            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                        source, publish, commit, packages, state)
                    .GetAwaiter().GetResult();

            Assert(state.ReadCount == 2 && candidate.SourceCommit == commit &&
                   candidate.PackageRoot == Path.Combine(packages, "rimtest-" + commit),
                "native candidate publish must bind two exact clean source observations");
            Assert(File.Exists(Path.Combine(candidate.PackageRoot, "rimtest.exe")) &&
                   !File.Exists(Path.Combine(candidate.PackageRoot, "rimliaison.exe")) &&
                   File.Exists(Path.Combine(candidate.PackageRoot, "rimliaison.dll")) &&
                   File.Exists(Path.Combine(candidate.PackageRoot, "rimliaison.deps.json")) &&
                   File.Exists(Path.Combine(candidate.PackageRoot, "rimliaison.runtimeconfig.json")) &&
                   File.Exists(Path.Combine(candidate.PackageRoot, "support", "fixture-support.dll")),
                "native candidate must include dependency closure but not unrestricted CLI apphost");
            Assert(NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _) &&
                   candidate.PackageSha256.Length == 64 &&
                   candidate.ManifestSha256.Length == 64,
                "complete native candidate package did not verify");

            // Cross-phase contract: the package generated by 01B must satisfy
            // the restricted native apphost's 01A launch-time checks. This
            // exercises verification only, not execution of the fixture binary.
            string modWorkspace = Path.Combine(root, "NatureTiles-fixture");
            Directory.CreateDirectory(Path.Combine(modWorkspace, "About"));
            File.WriteAllText(Path.Combine(modWorkspace, "About", "About.xml"),
                "<ModMetaData><name>Fixture</name><packageId>fixture.mod</packageId></ModMetaData>");
            string nativeExe = Path.Combine(candidate.PackageRoot, "rimtest.exe");
            NativeRimtestRunner.Dispatch? ready = NativeRimtestRunner.Prepare(
                nativeExe, ["dev-check", "--json"], modWorkspace);
            Assert(ready is { Allowed: true } &&
                   ready.Args.SequenceEqual(["dev-check", modWorkspace, "--json"]) &&
                   ready.SourceCommit == commit,
                "packaged native runner and launch-time closure contract disagree");

            string support = Path.Combine(candidate.PackageRoot, "support", "fixture-support.dll");
            File.WriteAllText(support, "mutated dependency");
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _) &&
                   NativeRimtestRunner.Prepare(nativeExe, ["dev-check", "--json"], modWorkspace) is
                   { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "mutated transitive dependency was admitted at native runner launch");
            File.WriteAllText(support, "support binary");

            string deps = Path.Combine(candidate.PackageRoot, "rimliaison.deps.json");
            File.WriteAllText(deps, "altered deps");
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _) &&
                   NativeRimtestRunner.Prepare(nativeExe, ["dev-check", "--json"], modWorkspace) is
                   { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "deps.json mutation was admitted at native runner launch");
            File.WriteAllText(deps, "{}");

            string sidecar = Path.Combine(candidate.PackageRoot, "rimtest-dev-runner.json");
            string originalSidecar = File.ReadAllText(sidecar);
            File.WriteAllText(sidecar, originalSidecar.Replace(
                new string('a', 40), new string('b', 40), StringComparison.Ordinal));
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _),
                "source identity mutation was accepted");
            File.WriteAllText(sidecar, originalSidecar);

            Assert(!NativeRimtestPackageService.Verify(
                       candidate.PackageRoot, new string('c', 40), out _),
                "mismatched expected source identity was accepted");
            Assert(NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _),
                "valid restored native candidate package did not verify");
            File.Delete(Path.Combine(candidate.PackageRoot,
                CliDeploymentManifestService.FileName));
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _) &&
                   NativeRimtestRunner.Prepare(nativeExe, ["dev-check", "--json"], modWorkspace) is
                   { Allowed: false, ErrorCode: "DEV_RUNNER_PROVENANCE_INVALID" },
                "missing closure manifest was admitted at native runner launch");

            AssertThrows<IOException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, packages, state).GetAwaiter().GetResult(),
                "existing immutable candidate version was overwritten");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeRunnerCandidateRejectsUnverifiedSource()
    {
        (string root, string source, string publish, string packages) = NativePackageFixture();
        try
        {
            string commit = new('a', 40);
            var dirty = new FixtureNativeGitState(source, commit) { Dirty = true };
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, packages, dirty).GetAwaiter().GetResult(),
                "dirty source was admitted");

            var incorrect = new FixtureNativeGitState(source, new string('b', 40));
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, packages, incorrect).GetAwaiter().GetResult(),
                "wrong Git HEAD was admitted");

            var movingHead = new FixtureNativeGitState(source, commit) { ChangeAfterFirst = true };
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, packages, movingHead).GetAwaiter().GetResult(),
                "source HEAD changed between preflight and non-replacing publication");
            Assert(!Directory.EnumerateDirectories(packages).Any(),
                "rejected source change left a staged or published candidate behind");

            var clean = new FixtureNativeGitState(source, commit);
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, new string('A', 40), packages, clean)
                    .GetAwaiter().GetResult(),
                "noncanonical source SHA was admitted");
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, source, clean).GetAwaiter().GetResult(),
                "package destination inside source was accepted");

            File.Delete(Path.Combine(publish, "rimliaison.runtimeconfig.json"));
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestPackageService.StageForTestsAsync(
                    source, publish, commit, packages, clean).GetAwaiter().GetResult(),
                "incomplete published CLI was accepted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string Root, string Source, string Published, string Packages) NativePackageFixture()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-package-fixture-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string published = Path.Combine(root, "published");
        string packages = Path.Combine(root, "candidate-packages");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(published);
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(Path.Combine(published, "support"));
        File.WriteAllText(Path.Combine(published, "rimliaison.exe"), "fixture apphost");
        File.WriteAllText(Path.Combine(published, "rimliaison.dll"), "fixture CLI assembly");
        File.WriteAllText(Path.Combine(published, "rimliaison.deps.json"), "{}");
        File.WriteAllText(Path.Combine(published, "rimliaison.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(published, "support", "fixture-support.dll"),
            "support binary");
        return (root, source, published, packages);
    }

    private sealed class FixtureNativeGitState(string root, string head)
        : RimLiaison.Git.IGitRepositoryStateProvider
    {
        public bool Dirty { get; set; }
        public bool ChangeAfterFirst { get; set; }
        public int ReadCount { get; private set; }

        public Task<RimLiaison.Git.GitRepositoryStateResult> ReadAsync(
            string rootPath, CancellationToken cancellationToken = default) =>
            ReadWorktreeAsync(rootPath, cancellationToken);

        public Task<RimLiaison.Git.GitRepositoryStateResult> ReadWorktreeAsync(
            string rootPath, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            string currentHead = ChangeAfterFirst && ReadCount > 1
                ? new string('b', 40)
                : head;
            var snapshot = new RimLiaison.Git.GitRepositoryStateSnapshot(
                root, "git:test-fixture", null, currentHead, null, null, null,
                Dirty, []);
            return Task.FromResult(new RimLiaison.Git.GitRepositoryStateResult(
                true, snapshot));
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
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

    public static void PublishedSelfCheckIsReadOnlyAndWorkspaceIndependent()
    {
        string sourceRoot = FindSourceRoot();
        string executable = Path.Combine(
            sourceRoot,
            "src",
            "RimLiaison.Cli",
            "bin",
            "Release",
            "net8.0",
            "rimliaison.exe");
        string workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-self-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            string before = SnapshotTree(workingDirectory);
            PromotionChildProcessResult result = ToolchainPromotionService.RunJsonCommandAsync(
                    executable,
                    ["self-check", "--json"],
                    CancellationToken.None,
                    workingDirectory: workingDirectory)
                .GetAwaiter()
                .GetResult();
            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            Assert(result.ExitCode == 0 &&
                document.RootElement.GetProperty("schemaVersion").GetString() ==
                    "rimliaison-self-check/v1" &&
                document.RootElement.GetProperty("status").GetString() == "ready" &&
                string.IsNullOrWhiteSpace(result.Stderr) &&
                before == SnapshotTree(workingDirectory),
                "published self-check must be ready and leave its workspace unchanged");
            Assert(!Directory.Exists(Path.Combine(workingDirectory, ".rimdev")),
                "published self-check must not create .rimdev state");
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    public static void PublishedSelfCheckWorksInReadOnlyDirectory()
    {
        string sourceRoot = FindSourceRoot();
        string executable = Path.Combine(
            sourceRoot,
            "src",
            "RimLiaison.Cli",
            "bin",
            "Release",
            "net8.0",
            "rimliaison.exe");
        string readOnlyDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert(Directory.Exists(readOnlyDirectory), "Windows directory was not available for read-only probe");
        PromotionChildProcessResult result = ToolchainPromotionService.RunJsonCommandAsync(
                executable,
                ["self-check", "--json"],
                CancellationToken.None,
                workingDirectory: readOnlyDirectory)
            .GetAwaiter()
            .GetResult();
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert(result.ExitCode == 0 &&
            document.RootElement.GetProperty("schemaVersion").GetString() ==
                "rimliaison-self-check/v1" &&
            document.RootElement.GetProperty("status").GetString() == "ready" &&
            string.IsNullOrWhiteSpace(result.Stderr),
            "published self-check must not require a writable working directory");
    }
    public static void ChildProcessEnvironmentOverridesAreApplied()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-child-environment-" + Guid.NewGuid().ToString("N"));
        string script = Path.Combine(root, "print-environment.cmd");
        const string variable = "RIMTEST_DEVBRIDGE_ROOT";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                script,
                "@echo off\r\necho {\"value\":\"%" + variable + "%\"}\r\n");
            Environment.SetEnvironmentVariable(variable, "C:\\stale\\DevBridge2");
            PromotionChildProcessResult result = ToolchainPromotionService.RunJsonCommandAsync(
                    script,
                    [],
                    CancellationToken.None,
                    environmentVariablesToRemove: new HashSet<string> { variable })
                .GetAwaiter()
                .GetResult();
            Assert(
                result.ExitCode == 0 &&
                result.Stdout.Contains("\"value\":\"\"", StringComparison.Ordinal),
                "child process environment overrides must remove stale source-checkout values");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }


    private static string FindSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RimLiaison.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("RimLiaison source root could not be found");
    }

    private static string SnapshotTree(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    return Directory.Exists(path)
                        ? "D:" + relative
                        : "F:" + relative + "\0" + ToolchainFileHash.Sha256(path);
                })
                .OrderBy(value => value, StringComparer.Ordinal));

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
