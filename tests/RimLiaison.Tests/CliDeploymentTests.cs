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

            string support = Path.Combine(candidate.PackageRoot, "support", "fixture-support.dll");
            File.WriteAllText(support, "mutated dependency");
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _),
                "dependency mutation was accepted");
            File.WriteAllText(support, "support binary");

            string deps = Path.Combine(candidate.PackageRoot, "rimliaison.deps.json");
            File.WriteAllText(deps, "altered deps");
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _),
                "deps.json mutation was accepted");
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
            Assert(!NativeRimtestPackageService.Verify(candidate.PackageRoot, commit, out _),
                "missing closure manifest was accepted");

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
