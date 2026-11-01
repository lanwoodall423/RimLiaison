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


    public static void NativeManagedArtifactDeclarationIsFixedAndInert()
    {
        Assert(ManagedNativeRimtestArtifactBuildCommand.TryParse(
                [ManagedNativeRimtestArtifactBuildCommand.Name, "--json"]),
            "native artifact builder must accept its only fixed command");
        foreach (string[] invalid in new[]
                 {
                     new[] { ManagedNativeRimtestArtifactBuildCommand.Name },
                     new[] { ManagedNativeRimtestArtifactBuildCommand.Name, "--install" },
                     new[] { ManagedNativeRimtestArtifactBuildCommand.Name, "--json", "--force" },
                     new[] { ManagedNativeRimtestArtifactBuildCommand.Name,
                         "C:\\RimWorld", "--json" }
                 })
            Assert(!ManagedNativeRimtestArtifactBuildCommand.TryParse(invalid),
                "native artifact builder must reject parameters, paths and install switches");

        using StringWriter blockedOutput = new();
        int blockedExit = ManagedNativeRimtestArtifactBuildCommand.RunAsync(
            [ManagedNativeRimtestArtifactBuildCommand.Name, "--install"],
            blockedOutput, CancellationToken.None).GetAwaiter().GetResult();
        using JsonDocument blocked = JsonDocument.Parse(blockedOutput.ToString());
        Assert(blockedExit != 0 &&
               blocked.RootElement.GetProperty("status").GetString() == "blocked" &&
               blocked.RootElement.GetProperty("code").GetString() ==
                   "RIMTEST_ARTIFACT_ARGUMENTS_INVALID" &&
               !blocked.RootElement.GetProperty("installed").GetBoolean() &&
               !blocked.RootElement.GetProperty("pathConfigured").GetBoolean(),
            "unsupported native artifact command must fail before any work or installation");

        string[] publish = ManagedNativeRimtestArtifactBuildCommand.PublishArguments(
            Path.Combine("fixture", "published"));
        Assert(publish.Contains("net8.0") && publish.Contains("win-x64") &&
               publish.Contains("-p:UseAppHost=true") &&
               publish.Contains("--self-contained") &&
               publish.Contains("false") &&
               !publish.Any(arg => arg.Contains("install", StringComparison.OrdinalIgnoreCase)),
            "native artifact publisher must keep a fixed net8 Windows apphost shape");

        DirectoryInfo? root = new(Environment.CurrentDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "RimLiaison.sln")))
            root = root.Parent;
        Assert(root is not null, "tracked RimLiaison manifest must be discoverable");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root!.FullName, ".projectnexus", "project.json")));
        JsonElement capabilities = manifest.RootElement.GetProperty("capabilities");
        JsonElement[] operations = capabilities.GetProperty("localOperations")
            .EnumerateArray().ToArray();
        Assert(operations.Length == 2 &&
               operations[0].GetProperty("operationId").GetString() ==
                   "native.rimtest-artifact-build" &&
               operations[0].GetProperty("sideEffectClass").GetString() ==
                   "artifact-generation" &&
               operations[0].GetProperty("requirements")
                   .GetProperty("platform").GetString() == "win32",
            "native local operation must be exclusively artifact generation");

        JsonElement command = operations[0].GetProperty("command");
        string[] arguments = command.GetProperty("args").EnumerateArray()
            .Select(element => element.GetString()!).ToArray();
        Assert(command.GetProperty("executable").GetString() == "dotnet" &&
               command.GetProperty("cwd").GetString() == "." &&
               !arguments.Contains("--no-restore") &&
               arguments.SequenceEqual(new[]
               {
                   "run", "--project", "src/RimLiaison.Cli/RimLiaison.Cli.csproj",
                   "--configuration", "Release", "--",
                   ManagedNativeRimtestArtifactBuildCommand.Name, "--json"
               }),
            "managed artifact operation must restore clean workspaces with fixed args and no caller paths");

        JsonElement[] outputs = operations[0].GetProperty("outputs")
            .EnumerateArray().ToArray();
        Assert(outputs.Length == 2 &&
               outputs[0].GetProperty("path").GetString() ==
                   ManagedNativeRimtestArtifactBuildCommand.ArchivePath &&
               outputs[0].GetProperty("maxBytes").GetInt64() == 134217728 &&
               outputs[1].GetProperty("path").GetString() ==
                   ManagedNativeRimtestArtifactBuildCommand.ReceiptPath &&
               outputs[1].GetProperty("maxBytes").GetInt64() == 16384,
            "managed artifact outputs must be only the fixed bounded archive and receipt");

        string[] inputs = operations[0].GetProperty("inputs").EnumerateArray()
            .Select(element => element.GetString()!).ToArray();
        Assert(inputs.Contains("global.json") &&
               inputs.Contains("src/RimLiaison.Cli/Toolchain/CliDeploymentManifest.cs"),
            "managed operation trust must bind SDK pin and artifact producer source");

        JsonElement audit = operations[1];
        JsonElement auditCommand = audit.GetProperty("command");
        string[] auditArgs = auditCommand.GetProperty("args")
            .EnumerateArray().Select(x => x.GetString()!).ToArray();
        JsonElement[] auditOutputs = audit.GetProperty("outputs")
            .EnumerateArray().ToArray();
        Assert(audit.GetProperty("operationId").GetString() ==
                   "native.rimtest-capture-verify" &&
               audit.GetProperty("sideEffectClass").GetString() ==
                   "artifact-generation" &&
               auditCommand.GetProperty("executable").GetString() == "dotnet" &&
               auditArgs.SequenceEqual(new[]
               {
                   "run", "--project", "src/RimLiaison.Cli/RimLiaison.Cli.csproj",
                   "--configuration", "Release", "--",
                   NativeRimtestCapturedArtifactAuditCommand.Name, "--json"
               }) &&
               auditOutputs.Length == 1 &&
               auditOutputs[0].GetProperty("path").GetString() ==
                   NativeRimtestCapturedArtifactAuditCommand.OutputPath &&
               auditOutputs[0].GetProperty("maxBytes").GetInt32() == 16384,
            "capture verification must be fixed, PN-managed and bounded");
        Assert(NativeRimtestCapturedArtifactAuditCommand.TryParse(
                   [NativeRimtestCapturedArtifactAuditCommand.Name, "--json"]) &&
               !NativeRimtestCapturedArtifactAuditCommand.TryParse(
                   [NativeRimtestCapturedArtifactAuditCommand.Name, "--install"]) &&
               !NativeRimtestCapturedArtifactAuditCommand.TryParse(
                   [NativeRimtestCapturedArtifactAuditCommand.Name,
                       "C:\\RimWorld", "--json"]),
            "capture audit must reject caller-selected paths and host actions");
    }

    public static void NativeCandidateInstallCopiesVerifiedClosureWithoutPATHChanges()
    {
        (string root, string source, string published, string packages) = NativePackageFixture();
        try
        {
            string commit = new('a', 40);
            var git = new FixtureNativeGitState(source, commit);
            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                    source, published, commit, packages, git)
                .GetAwaiter().GetResult();
            string accountRoot = Path.Combine(root, "Rimtest");
            string copiedCandidates = Path.Combine(accountRoot, "CandidatePackages");
            Directory.CreateDirectory(copiedCandidates);
            string copiedCandidate = Path.Combine(copiedCandidates, "rimtest-" + commit);
            CopyFixtureTree(candidate.PackageRoot, copiedCandidate);

            string sourceManifest = Path.Combine(copiedCandidate,
                CliDeploymentManifestService.FileName);
            string sourceManifestHash = ToolchainFileHash.Sha256(sourceManifest);
            string[] sourceFiles = Directory.EnumerateFiles(
                copiedCandidate, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            string[] args =
            [
                NativeRimtestCandidateInstallCommand.Name,
                commit, "--json"
            ];

            static (int Exit, string Status, string? Code, JsonElement Details) Install(
                string[] inputs, string accountRoot)
            {
                using StringWriter output = new();
                int exit = NativeRimtestCandidateInstallCommand.RunForTests(
                    inputs, output, accountRoot);
                using JsonDocument json = JsonDocument.Parse(output.ToString());
                JsonElement data = json.RootElement;
                Assert(!data.GetProperty("qualified").GetBoolean() &&
                       !data.GetProperty("pathConfigured").GetBoolean() &&
                       !data.GetProperty("pnCheckPassed").GetBoolean() &&
                       !data.GetProperty("independentlyAttestedBuild").GetBoolean(),
                    "candidate copy cannot claim production, PATH, PN or source-to-build attestation");
                return (exit, data.GetProperty("status").GetString()!,
                    data.TryGetProperty("code", out JsonElement code)
                        ? code.GetString() : null,
                    data.Clone());
            }

            Assert(NativeRimtestCandidateInstallCommand.TryParse(args,
                       out string? parsed) && parsed == commit,
                "installer requires an exact canonical source revision");
            foreach (string[] malformed in new[]
                     {
                         new[] { NativeRimtestCandidateInstallCommand.Name, commit },
                         new[] { NativeRimtestCandidateInstallCommand.Name,
                             commit.ToUpperInvariant(), "--json" },
                         new[] { NativeRimtestCandidateInstallCommand.Name,
                             commit, "--force" },
                         new[] { NativeRimtestCandidateInstallCommand.Name,
                             commit, "--json", "--replace" },
                         new[] { NativeRimtestCandidateInstallCommand.Name,
                             "not-a-commit", "--json" }
                     })
            {
                var invalid = Install(malformed, accountRoot);
                Assert(invalid.Exit == CliExitCodes.ConservativeSelection &&
                       invalid.Code == "RIMTEST_INSTALL_ARGUMENTS_INVALID",
                    "malformed or overwrite-like candidate installation must fail closed");
            }

            var copied = Install(args, accountRoot);
            string installed = Path.Combine(accountRoot, "Installed", "rimtest-" + commit);
            string installedExe = Path.Combine(installed, "rimtest.exe");
            Assert(copied.Exit == CliExitCodes.Success &&
                   copied.Status == "installed-unbound-candidate" &&
                   copied.Details.GetProperty("installedPath").GetString() == installed &&
                   copied.Details.GetProperty("executablePath").GetString() == installedExe &&
                   copied.Details.GetProperty("manifestSha256").GetString() == sourceManifestHash &&
                   NativeRimtestPackageService.Verify(installed, commit, out _) &&
                   ToolchainFileHash.Sha256(sourceManifest) == sourceManifestHash &&
                   Directory.EnumerateFiles(
                       copiedCandidate, "*", SearchOption.AllDirectories)
                       .OrderBy(path => path, StringComparer.Ordinal)
                       .SequenceEqual(sourceFiles),
                "copy must retain a full matching source-bound package without mutating candidate");
            Assert(!Directory.EnumerateDirectories(
                       Path.Combine(accountRoot, "Installed"), ".rimtest-install-stage-*").Any(),
                "successful install must not retain an incomplete staging directory");

            string sourceExe = Path.Combine(copiedCandidate, "rimtest.exe");
            string installedExeHash = ToolchainFileHash.Sha256(installedExe);
            var duplicate = Install(args, accountRoot);
            Assert(duplicate.Code == "RIMTEST_INSTALL_VERSION_ALREADY_EXISTS" &&
                   ToolchainFileHash.Sha256(installedExe) == installedExeHash,
                "a second call cannot overwrite the existing installed version");

            var nonexistent = Install(
                [NativeRimtestCandidateInstallCommand.Name, new string('b', 40), "--json"],
                accountRoot);
            Assert(nonexistent.Code == "RIMTEST_INSTALL_CANDIDATE_MISSING",
                "unknown source candidate must not create a new installed version");

            string support = Path.Combine(copiedCandidate, "support", "fixture-support.dll");
            File.WriteAllText(support, "tampered transitive binary");
            var corrupted = Install(args, accountRoot);
            Assert(corrupted.Code == "RIMTEST_INSTALL_CANDIDATE_INVALID" &&
                   ToolchainFileHash.Sha256(installedExe) == installedExeHash,
                "changed source dependency must be rejected before any copy or overwrite");
            File.WriteAllText(support, "support binary");

            string missing = Path.Combine(copiedCandidate, "rimtest-dev-runner.json");
            File.Delete(missing);
            var missingMetadata = Install(args, accountRoot);
            Assert(missingMetadata.Code == "RIMTEST_INSTALL_CANDIDATE_INVALID",
                "missing source-binding metadata must block installation");

            string workspace = Path.Combine(root, "NatureTiles-workspace");
            Directory.CreateDirectory(workspace);
            using StringWriter pathReport = new();
            int pathExit = NativeRimtestPathPreflightCommand.RunForTests(
                [NativeRimtestPathPreflightCommand.Name, commit, "--json"],
                pathReport, Path.Combine(accountRoot, "Installed"), installed, workspace);
            using JsonDocument pathResult = JsonDocument.Parse(pathReport.ToString());
            Assert(pathExit == CliExitCodes.Success &&
                   pathResult.RootElement.GetProperty("status").GetString() ==
                   "local-path-preflight-only" &&
                   !pathResult.RootElement.GetProperty("pnCheckPassed").GetBoolean(),
                "verified installed fixture must satisfy local PATH inspection without granting PN success");

            NativeRimtestRunner.Dispatch? blocked = NativeRimtestRunner.Prepare(
                installedExe, args, workspace);
            Assert(blocked is
            { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
                "native development apphost must not expose installation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyFixtureTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public static void NativePublisherResolvesOnlyExplicitOrKnownWindowsDotnetHosts()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-host-fixture-" + Guid.NewGuid().ToString("N"));
        string explicitHost = Path.Combine(root, "PinnedSdk", "dotnet.exe");
        string explicitRoot = Path.Combine(root, "OtherSdk");
        string rootHost = Path.Combine(explicitRoot, "dotnet.exe");
        string programFiles = Path.Combine(root, "ProgramFiles");
        string accountLocal = Path.Combine(root, "LocalAppData");
        string machineHost = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        string accountHost = Path.Combine(
            accountLocal, "Microsoft", "dotnet", "dotnet.exe");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            explicitHost,
            rootHost,
            machineHost,
            accountHost
        };

        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   explicitHost, explicitRoot, programFiles, accountLocal,
                   available.Contains) == explicitHost,
            "an explicitly pinned DOTNET_HOST_PATH must take precedence over other hosts");
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   null, explicitRoot, programFiles, accountLocal,
                   available.Contains) == rootHost,
            "DOTNET_ROOT must take precedence over standard host discovery");
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   null, null, programFiles, accountLocal,
                   available.Contains) == machineHost,
            "a conventional Program Files host must be usable without explicit environment variables");
        var userOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            accountHost
        };
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   null, null, programFiles, accountLocal,
                   userOnly.Contains) == accountHost,
            "a conventional account-local .NET host must be the fallback when machine host is absent");

        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   Path.Combine(root, "invalid", "dotnet.exe"), explicitRoot,
                   programFiles, accountLocal, available.Contains) is null,
            "an invalid explicit host must not silently fall back to another binary");
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   null, "relative-sdk", programFiles, accountLocal,
                   available.Contains) is null,
            "an invalid DOTNET_ROOT must fail closed instead of searching unrelated paths");
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   null, null, programFiles, accountLocal,
                   _ => false) is null,
            "a missing or rejected SDK host must not be treated as installed");
        Assert(NativeRimtestSourcePublishCommand.ResolvePinnedDotnetHost(
                   "dotnet", null, programFiles, accountLocal,
                   available.Contains) is null,
            "a bare PATH executable must never be executed as the pinned SDK host");
    }

    public static void NativeRimtestSourcePublishIsPinnedAndNeverInstalls()
    {
        (string root, string source, string publishedFixture, string packages) =
            NativePackageFixture();
        try
        {
            string commit = new('a', 40);
            string sdkHost = Path.Combine(root, "dotnet.exe");
            string scratch = Path.Combine(root, "PublishScratch");
            File.WriteAllText(sdkHost, "test-only SDK host fixture");
            string project = Path.Combine(source, "src", "RimLiaison.Cli",
                "RimLiaison.Cli.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            File.WriteAllText(Path.Combine(source, "global.json"),
                "{\"sdk\":{\"version\":\"10.0.403\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}");

            string[] valid =
            [
                NativeRimtestSourcePublishCommand.Name,
                source,
                commit,
                "--json"
            ];
            Assert(NativeRimtestSourcePublishCommand.IsRequested(valid) &&
                   NativeRimtestSourcePublishCommand.TryParse(valid,
                       out NativeRimtestSourcePublishCommand.Request? parsed) &&
                   parsed is not null && parsed.SourceRoot == source &&
                   parsed.SourceCommit == commit,
                "source publish must require a fixed command, absolute source and lowercase commit");

            foreach (string[] malformed in new[]
                     {
                         new[] { NativeRimtestSourcePublishCommand.Name, source, commit },
                         new[] { NativeRimtestSourcePublishCommand.Name, "relative", commit, "--json" },
                         new[] { NativeRimtestSourcePublishCommand.Name, source,
                             commit.ToUpperInvariant(), "--json" },
                         new[] { NativeRimtestSourcePublishCommand.Name, source, commit, "--install" },
                         new[] { NativeRimtestSourcePublishCommand.Name, source, commit, "--json", "--force" }
                     })
                Assert(!NativeRimtestSourcePublishCommand.TryParse(malformed, out _),
                    "publish route accepted invalid or installer-like arguments");

            int sdkChecks = 0;
            int fixedPublishes = 0;
            async Task<NativeRimtestPublishResult> FakePublisher(
                string exe, string cwd, IReadOnlyList<string> args, CancellationToken token)
            {
                await Task.Yield();
                Assert(exe == sdkHost && cwd == source,
                    "the fixed SDK host and clean source cwd must not drift");
                if (args.Count == 1 && args[0] == "--version")
                {
                    sdkChecks++;
                    return new NativeRimtestPublishResult(0, "10.0.403");
                }
                string[] words = args.ToArray();
                Assert(words.SequenceEqual(
                [
                    "publish", "src/RimLiaison.Cli/RimLiaison.Cli.csproj",
                    "--configuration", "Release",
                    "--framework", "net8.0",
                    "--runtime", "win-x64",
                    "--self-contained", "false",
                    "-p:UseAppHost=true", "--output", words[12], "--nologo"
                ]), "publish command must not admit arbitrary project, RID, flags or output");
                fixedPublishes++;
                string output = words[12];
                foreach (string from in Directory.EnumerateFiles(
                             publishedFixture, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(publishedFixture, from);
                    string destination = Path.Combine(output, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(from, destination);
                }
                return new NativeRimtestPublishResult(0, "fixture publish completed");
            }

            static (int Exit, string Status, string? Code, bool Qualified) Invoke(
                string[] args,
                string sdkHost,
                string scratch,
                string packages,
                FixtureNativeGitState sourceState,
                Func<string, string, IReadOnlyList<string>, CancellationToken,
                    Task<NativeRimtestPublishResult>> fake)
            {
                using StringWriter output = new();
                int exit = NativeRimtestSourcePublishCommand.RunForTestsAsync(
                    args, output, sdkHost, scratch, packages, sourceState, fake)
                    .GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(output.ToString());
                JsonElement data = doc.RootElement;
                Assert(!data.GetProperty("qualified").GetBoolean() &&
                       !data.GetProperty("installed").GetBoolean() &&
                       !data.GetProperty("pnCheckPassed").GetBoolean() &&
                       !data.GetProperty("independentlyAttestedBuild").GetBoolean(),
                    "candidate source publisher must never report PN, installation or attestation success");
                return (exit, data.GetProperty("status").GetString()!,
                    data.TryGetProperty("code", out JsonElement code)
                        ? code.GetString() : null,
                    data.GetProperty("qualified").GetBoolean());
            }

            var git = new FixtureNativeGitState(source, commit);
            var candidate = Invoke(valid, sdkHost, scratch, packages, git, FakePublisher);
            Assert(candidate.Exit == CliExitCodes.Success &&
                   candidate.Status == "candidate-only" &&
                   candidate.Code is null &&
                   sdkChecks == 1 && fixedPublishes == 1 &&
                   git.ReadCount == 4 &&
                   !Directory.EnumerateDirectories(scratch).Any(),
                "source-pinned publish must verify Git four times, stage only the candidate and clean scratch");
            Assert(NativeRimtestPackageService.Verify(
                       Path.Combine(packages, "rimtest-" + commit), commit, out _),
                "positive fixed publish fixture must produce a full-closure candidate");

            var repeat = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit), FakePublisher);
            Assert(repeat.Status == "blocked" &&
                   repeat.Code == "RIMTEST_PACKAGE_VERSION_ALREADY_EXISTS" &&
                   !Directory.EnumerateDirectories(scratch).Any(),
                "source publisher may never overwrite the existing versioned package");

            var dirty = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit) { Dirty = true },
                FakePublisher);
            Assert(dirty.Status == "blocked" &&
                   dirty.Code == "RIMTEST_PUBLISH_SOURCE_UNVERIFIED",
                "dirty Git source must stop before the publisher runs");

            var moving = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit) { ChangeAfterFirst = true },
                FakePublisher);
            Assert(moving.Status == "blocked" &&
                   moving.Code == "RIMTEST_PUBLISH_SOURCE_UNVERIFIED" &&
                   !Directory.EnumerateDirectories(scratch).Any(),
                "a source HEAD change during publish must block and clean only temporary output");

            Task<NativeRimtestPublishResult> WrongSdk(
                string exe, string cwd, IReadOnlyList<string> args, CancellationToken token) =>
                Task.FromResult(new NativeRimtestPublishResult(0, "8.0.424"));
            var sdkMismatch = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit), WrongSdk);
            Assert(sdkMismatch.Code == "RIMTEST_PUBLISH_SDK_PIN_MISMATCH",
                "publisher must reject the former .NET 8 SDK before any build");

            // Catch a future source global.json migration without a matching
            // publisher migration: the checked-in SDK pin is authoritative.
            string pinPath = Path.Combine(source, "global.json");
            string pinnedManifest = File.ReadAllText(pinPath);
            int publisherInvocationsBeforeStalePin = sdkChecks + fixedPublishes;
            try
            {
                File.WriteAllText(pinPath, pinnedManifest.Replace(
                    "\"10.0.403\"", "\"8.0.424\"", StringComparison.Ordinal));
                var staleSourcePin = Invoke(valid, sdkHost, scratch, packages,
                    new FixtureNativeGitState(source, commit), FakePublisher);
                Assert(staleSourcePin.Code == "RIMTEST_PUBLISH_SDK_PIN_MISMATCH" &&
                       sdkChecks + fixedPublishes == publisherInvocationsBeforeStalePin,
                    "a stale global SDK pin must block before any compiler process");
            }
            finally
            {
                File.WriteAllText(pinPath, pinnedManifest);
            }

            Task<NativeRimtestPublishResult> BrokenBuild(
                string exe, string cwd, IReadOnlyList<string> args, CancellationToken token) =>
                Task.FromResult(args.Count == 1
                    ? new NativeRimtestPublishResult(0, "10.0.403")
                    : new NativeRimtestPublishResult(1, "compiler failed"));
            var failed = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit), BrokenBuild);
            Assert(failed.Code == "RIMTEST_PUBLISH_DOTNET_FAILED" &&
                   !Directory.EnumerateDirectories(scratch).Any(),
                "failed fixed publish must report blocked without residual scratch");

            Task<NativeRimtestPublishResult> EmptyOutput(
                string exe, string cwd, IReadOnlyList<string> args, CancellationToken token) =>
                Task.FromResult(new NativeRimtestPublishResult(
                    0, args.Count == 1 ? "10.0.403" : "not actually published"));
            var incomplete = Invoke(valid, sdkHost, scratch, packages,
                new FixtureNativeGitState(source, commit), EmptyOutput);
            Assert(incomplete.Code == "RIMTEST_PACKAGE_PUBLISH_INCOMPLETE" &&
                   !Directory.EnumerateDirectories(scratch).Any(),
                "missing published files must block even if child process falsely returned success");

            var missingHost = Invoke(valid, Path.Combine(root, "missing-dotnet.exe"),
                scratch, packages, new FixtureNativeGitState(source, commit),
                FakePublisher);
            Assert(missingHost.Code == "RIMTEST_PUBLISH_PINNED_HOST_UNAVAILABLE",
                "unavailable explicitly pinned dotnet host must not fall back to PATH");

            NativeRimtestRunner.Dispatch? denied = NativeRimtestRunner.Prepare(
                Path.Combine(root, "rimtest.exe"), valid, source);
            Assert(denied is
            { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
                "native rimtest apphost must not expose source publishing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeChildPreflightOnlyReturnsVerifiedCandidateForFutureChild()
    {
        (string root, string source, string publish, string packages) =
            NativePackageFixture();
        string commit = new('a', 40);
        try
        {
            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                        source, publish, commit, packages,
                        new FixtureNativeGitState(source, commit))
                    .GetAwaiter().GetResult();
            string runner = Path.Combine(candidate.PackageRoot, "rimtest.exe");
            string manifest = Path.Combine(
                candidate.PackageRoot, CliDeploymentManifestService.FileName);
            string[] cmd =
            [
                NativeRimtestChildPreflightCommand.Name,
                commit, candidate.PackageSha256, "--json"
            ];

            static (int Exit, JsonDocument Json) Run(
                IReadOnlyList<string> args, string packages)
            {
                using StringWriter output = new();
                int exit = NativeRimtestChildPreflightCommand.RunForTests(
                    args, output, packages);
                JsonDocument result = JsonDocument.Parse(output.ToString());
                foreach (string flag in new[]
                         {
                             "installed", "pathConfigured", "qualified",
                             "pnCheckPassed", "gameLaunchAuthorized",
                             "hostMutationAuthorized", "childProcessAuthorized"
                         })
                    Assert(!result.RootElement.GetProperty(flag).GetBoolean(),
                        "child identity preflight must never grant " + flag);
                return (exit, result);
            }

            Assert(NativeRimtestChildPreflightCommand.TryParse(
                       cmd, out string? sourceSha, out string? packageSha) &&
                   sourceSha == commit && packageSha == candidate.PackageSha256,
                "child preflight must accept only a canonical source and package hash");
            string prior = SnapshotTree(root);
            var (exit, positive) = Run(cmd, packages);
            using (positive)
            {
                JsonElement doc = positive.RootElement;
                Assert(exit == CliExitCodes.Success &&
                       doc.GetProperty("status").GetString() ==
                           "candidate-child-preflight-only" &&
                       doc.GetProperty("sourceCommit").GetString() == commit &&
                       doc.GetProperty("packageSha256").GetString() ==
                           candidate.PackageSha256 &&
                       doc.GetProperty("manifestSha256").GetString() ==
                           ToolchainFileHash.Sha256(manifest) &&
                       doc.GetProperty("runnerExeSha256").GetString() ==
                           ToolchainFileHash.Sha256(runner) &&
                       doc.GetProperty("runnerExecutablePath").GetString() ==
                           runner && SnapshotTree(root) == prior,
                    "full-closure candidate must produce read-only identity for a later child");
            }

            foreach (string[] malformed in new[]
                     {
                         cmd[..^1],
                         new[] { cmd[0], commit.ToUpperInvariant(), cmd[2], "--json" },
                         new[] { cmd[0], commit, cmd[2].ToUpperInvariant(), "--json" },
                         new[] { cmd[0], commit, cmd[2], "--install" },
                         new[] { cmd[0], commit, cmd[2], "--json", "--force" },
                         new[] { cmd[0], @"C:\RimWorld", cmd[2], "--json" },
                     })
            {
                var result = Run(malformed, packages);
                using (result.Json)
                    Assert(result.Exit == CliExitCodes.ConservativeSelection &&
                           result.Json.RootElement.GetProperty("code").GetString() ==
                               "RIMTEST_CHILD_ARGUMENTS_INVALID",
                        "noncanonical or host-changing flags must be rejected");
            }
            var absent = Run(cmd, Path.Combine(root, "absent"));
            using (absent.Json)
                Assert(absent.Exit == CliExitCodes.ConservativeSelection &&
                       absent.Json.RootElement.GetProperty("code").GetString() ==
                           "RIMTEST_CHILD_CANDIDATE_INVALID",
                    "missing candidate cannot supply executable identity");
            var mismatch = Run(
                [cmd[0], commit, new string('b', 64), "--json"], packages);
            using (mismatch.Json)
                Assert(mismatch.Exit == CliExitCodes.ConservativeSelection &&
                       mismatch.Json.RootElement.GetProperty("code").GetString() ==
                           "RIMTEST_CHILD_PACKAGE_HASH_MISMATCH",
                    "wrong package hash must fail even with valid native source");
            var foreign = Run(
                [cmd[0], new string('b', 40), cmd[2], "--json"], packages);
            using (foreign.Json)
                Assert(foreign.Exit == CliExitCodes.ConservativeSelection,
                    "wrong source package must be unavailable");

            string deps = Path.Combine(
                candidate.PackageRoot, "rimliaison.deps.json");
            File.AppendAllText(deps, "tampered");
            var tampered = Run(cmd, packages);
            using (tampered.Json)
                Assert(tampered.Exit == CliExitCodes.ConservativeSelection &&
                       tampered.Json.RootElement.GetProperty("code").GetString() ==
                           "RIMTEST_CHILD_CANDIDATE_INVALID",
                    "modified native runtime dependency must fail closed");

            // The only files written in this test are in the owned fixture tree.
            Assert(!Directory.Exists(Path.Combine(root, "Installed")) &&
                   !Directory.EnumerateDirectories(packages,
                       ".rimtest-import-*").Any(),
                "read-only preflight cannot install, stage or change PATH");
            NativeRimtestRunner.Dispatch? denied = NativeRimtestRunner.Prepare(
                runner, cmd, source);
            Assert(denied is not null && !denied.Allowed &&
                   denied.ErrorCode == "DEV_RUNNER_COMMAND_NOT_ALLOWED",
                "the restricted runner must not expose this normal CLI command");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeRimtestPathPreflightRejectsMissingShadowedAndCorruptExecutables()
    {
        (string root, string source, string publish, string packages) = NativePackageFixture();
        try
        {
            string commit = new('a', 40);
            var git = new FixtureNativeGitState(source, commit);
            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                        source, publish, commit, packages, git)
                    .GetAwaiter().GetResult();

            // The test alone copies already-hashed fixture files into a
            // synthetic future installed-version directory. The diagnostic
            // below must not mutate any package or environment variable.
            string installedParent = Path.Combine(root, "Installed");
            string installed = Path.Combine(installedParent, "rimtest-" + commit);
            Directory.CreateDirectory(installed);
            foreach (string from in Directory.EnumerateFiles(
                         candidate.PackageRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(candidate.PackageRoot, from);
                string to = Path.Combine(installed, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }

            string cwd = Path.Combine(root, "NatureTiles-workspace");
            Directory.CreateDirectory(cwd);
            string rogue = Path.Combine(root, "rogue-PATH");
            Directory.CreateDirectory(rogue);

            string[] command =
            [
                NativeRimtestPathPreflightCommand.Name,
                commit,
                "--json"
            ];
            static (int Exit, string Status, string? Error) Inspect(
                string[] args, string installedParent, string? path, string cwd)
            {
                using StringWriter output = new();
                int result = NativeRimtestPathPreflightCommand.RunForTests(
                    args, output, installedParent, path, cwd);
                using JsonDocument json = JsonDocument.Parse(output.ToString());
                JsonElement root = json.RootElement;
                Assert(!root.GetProperty("qualified").GetBoolean() &&
                       !root.GetProperty("approvedInstallation").GetBoolean() &&
                       !root.GetProperty("pnCheckPassed").GetBoolean(),
                    "PATH inspection must never claim approved installation or PN verification");
                return (
                    result,
                    root.GetProperty("status").GetString()!,
                    root.TryGetProperty("code", out JsonElement code)
                        ? code.GetString()
                        : null);
            }

            Assert(NativeRimtestPathPreflightCommand.TryParse(command,
                       out string? parsed) && parsed == commit &&
                   !NativeRimtestPathPreflightCommand.IsRequested(["dev-check", "--json"]),
                "read-only path preflight must be a distinct exact normal CLI command");

            string manifest = Path.Combine(installed,
                CliDeploymentManifestService.FileName);
            string expectedHash = ToolchainFileHash.Sha256(manifest);
            string[] initialFiles = Directory.EnumerateFiles(
                installed, "*", SearchOption.AllDirectories).Order().ToArray();
            var pass = Inspect(command, installedParent, installed, cwd);
            Assert(pass.Exit == CliExitCodes.Success &&
                   pass.Status == "local-path-preflight-only" &&
                   pass.Error is null &&
                   ToolchainFileHash.Sha256(manifest) == expectedHash &&
                   Directory.EnumerateFiles(
                       installed, "*", SearchOption.AllDirectories)
                       .Order().SequenceEqual(initialFiles),
                "exact installed-path fixture must be inspectable without modification");

            string wrongCommit = new('b', 40);
            foreach (string[] invalid in new[]
                     {
                         new[] { NativeRimtestPathPreflightCommand.Name, commit },
                         new[] { NativeRimtestPathPreflightCommand.Name, commit, "--install" },
                         new[] { NativeRimtestPathPreflightCommand.Name, commit,
                             "--json", "--force" },
                         new[] { NativeRimtestPathPreflightCommand.Name,
                             commit.ToUpperInvariant(), "--json" },
                         new[] { NativeRimtestPathPreflightCommand.Name,
                             "unverified-source", "--json" }
                     })
            {
                var rejected = Inspect(invalid, installedParent, installed, cwd);
                Assert(rejected.Exit == CliExitCodes.ConservativeSelection &&
                       rejected.Status == "blocked" &&
                       rejected.Error == "RIMTEST_PATH_ARGUMENTS_INVALID",
                    "PATH preflight accepted invalid or install-like commands");
            }

            Assert(Inspect(command, installedParent, null, cwd).Error ==
                   "RIMTEST_PATH_NOT_CONFIGURED" &&
                   Inspect(command, installedParent,
                       Path.Combine(root, "not-installed"), cwd).Error ==
                   "RIMTEST_PATH_NOT_CONFIGURED" &&
                   Inspect(command, installedParent,
                       ";" + installed, cwd).Error ==
                   "RIMTEST_PATH_ENTRY_INVALID" &&
                   Inspect(command, installedParent,
                       "relative-path" + Path.PathSeparator + installed, cwd).Error ==
                   "RIMTEST_PATH_ENTRY_INVALID",
                "missing or nonabsolute PATH must fail closed");

            var wrongRevision = Inspect(
                [NativeRimtestPathPreflightCommand.Name, wrongCommit, "--json"],
                installedParent, installed, cwd);
            Assert(wrongRevision.Status == "blocked" &&
                   wrongRevision.Error == "RIMTEST_PATH_NOT_CONFIGURED",
                "source commit cannot be substituted for the installed directory revision");

            File.WriteAllText(Path.Combine(rogue, "rimtest.exe"), "untrusted executable");
            Assert(Inspect(command, installedParent,
                       string.Join(Path.PathSeparator.ToString(), new[] { rogue, installed }),
                       cwd).Error == "RIMTEST_PATH_SHADOWED" &&
                   Inspect(command, installedParent,
                       string.Join(Path.PathSeparator.ToString(), new[] { installed, rogue }),
                       cwd).Error == "RIMTEST_PATH_AMBIGUOUS",
                "untrusted earlier or later native runner on PATH must block");
            string cwdShadow = Path.Combine(cwd, "rimtest.exe");
            File.WriteAllText(cwdShadow, "shadow apphost");
            Assert(Inspect(command, installedParent, installed, cwd).Error ==
                   "RIMTEST_PATH_CWD_SHADOW",
                "NatureTiles working directory must not shadow the approved native executable");
            File.Delete(cwdShadow);

            string support = Path.Combine(installed, "support", "fixture-support.dll");
            File.WriteAllText(support, "tampered dependency");
            Assert(Inspect(command, installedParent, installed, cwd).Error ==
                   "RIMTEST_PATH_PACKAGE_INVALID",
                "changed dependency must block runnable-PATH preflight");
            File.WriteAllText(support, "support binary");
            Assert(Inspect(command, installedParent, installed, cwd).Exit ==
                   CliExitCodes.Success,
                "restored package fixture must reverify");

            string installedExe = Path.Combine(installed, "rimtest.exe");
            File.Delete(installedExe);
            File.WriteAllText(Path.Combine(installed, "rimtest.cmd"), "@echo off");
            Assert(Inspect(command, installedParent, installed, cwd).Error ==
                   "RIMTEST_PATH_EXECUTABLE_MISSING",
                "a legacy rimtest.cmd alias must never satisfy native Windows EXE resolution");

            NativeRimtestRunner.Dispatch? denied = NativeRimtestRunner.Prepare(
                Path.Combine(candidate.PackageRoot, "rimtest.exe"), command, cwd);
            Assert(denied is
            { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
                "native apphost must not expose installation or PATH-preflight commands");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeRimtestCandidateVerificationIsReadOnlyAndBoundToExactPackage()
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

            static (int Exit, string Status, string? Code, string? PackageSha) Check(
                string[] arguments, string accountPackages)
            {
                using StringWriter output = new();
                int exit = NativeRimtestPackageVerificationCommand.RunForTests(
                    arguments, output, accountPackages);
                using JsonDocument data = JsonDocument.Parse(output.ToString());
                JsonElement json = data.RootElement;
                Assert(!json.GetProperty("qualified").GetBoolean() &&
                       !json.GetProperty("installed").GetBoolean() &&
                       !json.GetProperty("independentlyAttestedBuild").GetBoolean(),
                    "a package inspection must never claim qualification, installation or source-to-build attestation");
                return (
                    exit,
                    json.GetProperty("status").GetString()!,
                    json.TryGetProperty("code", out JsonElement code) ? code.GetString() : null,
                    json.TryGetProperty("packageSha256", out JsonElement hash) ? hash.GetString() : null
                );
            }

            string[] valid =
            [
                NativeRimtestPackageVerificationCommand.Name,
                candidate.PackageRoot,
                commit,
                "--json"
            ];
            Assert(NativeRimtestPackageVerificationCommand.IsRequested(valid) &&
                   NativeRimtestPackageVerificationCommand.TryParse(valid,
                       out NativeRimtestPackageVerificationCommand.Request? parsed) &&
                   parsed is not null &&
                   parsed.PackageRoot == candidate.PackageRoot &&
                   parsed.SourceCommit == commit,
                "read-only candidate verification must require exact absolute path and source identity");

            string manifestPath = Path.Combine(
                candidate.PackageRoot, CliDeploymentManifestService.FileName);
            string originalManifestHash = ToolchainFileHash.Sha256(manifestPath);
            string[] originalFiles = Directory.EnumerateFiles(
                candidate.PackageRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var result = Check(valid, packages);
            Assert(result.Exit == CliExitCodes.Success &&
                   result.Status == "candidate-verified" &&
                   result.Code is null &&
                   result.PackageSha == candidate.PackageSha256,
                "valid staged candidate must be independently read-only verifiable");
            Assert(Directory.EnumerateFiles(
                       candidate.PackageRoot, "*", SearchOption.AllDirectories)
                       .OrderBy(path => path, StringComparer.Ordinal)
                       .SequenceEqual(originalFiles) &&
                   ToolchainFileHash.Sha256(manifestPath) == originalManifestHash,
                "verification must leave the candidate package untouched");

            foreach (string[] invalid in new[]
                     {
                         new[] { NativeRimtestPackageVerificationCommand.Name,
                             "relative-package", commit, "--json" },
                         new[] { NativeRimtestPackageVerificationCommand.Name,
                             candidate.PackageRoot, commit.ToUpperInvariant(), "--json" },
                         new[] { NativeRimtestPackageVerificationCommand.Name,
                             candidate.PackageRoot, commit, "--install" },
                         new[] { NativeRimtestPackageVerificationCommand.Name,
                             candidate.PackageRoot, commit },
                         new[] { NativeRimtestPackageVerificationCommand.Name,
                             candidate.PackageRoot, commit, "--json", "--force" }
                     })
            {
                var denial = Check(invalid, packages);
                Assert(denial.Exit == CliExitCodes.ConservativeSelection &&
                       denial.Status == "blocked" &&
                       denial.Code == "RIMTEST_PACKAGE_VERIFY_ARGUMENTS_INVALID",
                    "invalid candidate verification arguments must fail closed without touching files");
            }

            var wrongCommit = Check(
                [NativeRimtestPackageVerificationCommand.Name,
                 candidate.PackageRoot, new string('b', 40), "--json"], packages);
            Assert(wrongCommit.Status == "blocked" &&
                   wrongCommit.Code == "RIMTEST_PACKAGE_LOCATION_MISMATCH",
                "a source revision different from the exact versioned package path must be rejected");

            var wrongParent = Check(valid, Path.Combine(root, "different-account-root"));
            Assert(wrongParent.Status == "blocked" &&
                   wrongParent.Code == "RIMTEST_PACKAGE_LOCATION_MISMATCH",
                "a package outside the approved account-local candidate root must be rejected");

            string dependency = Path.Combine(candidate.PackageRoot, "support", "fixture-support.dll");
            File.WriteAllText(dependency, "changed dependency");
            var tampered = Check(valid, packages);
            Assert(tampered.Status == "blocked" &&
                   tampered.Code == "RIMTEST_PACKAGE_VERIFICATION_FAILED",
                "a modified transitive dependency must fail read-only verification");

            File.WriteAllText(dependency, "support binary");
            File.Delete(Path.Combine(candidate.PackageRoot, "rimtest-dev-runner.json"));
            var missing = Check(valid, packages);
            Assert(missing.Status == "blocked" &&
                   missing.Code == "RIMTEST_PACKAGE_VERIFICATION_FAILED",
                "a missing native-runner source/hash sidecar must fail verification");

            NativeRimtestRunner.Dispatch? native = NativeRimtestRunner.Prepare(
                Path.Combine(candidate.PackageRoot, "rimtest.exe"),
                valid, root);
            Assert(native is { Allowed: false, ErrorCode: "DEV_RUNNER_COMMAND_NOT_ALLOWED" },
                "the native development runner must not expose verification or packaging commands");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    public static void NativePackagedWindowsApphostFailsClosedWithoutLaunchingGame()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string builtCli = Path.Combine(
            FindSourceRoot(), "src", "RimLiaison.Cli", "bin", "Release", "net8.0");
        string builtApphost = Path.Combine(builtCli, "rimliaison.exe");
        Assert(File.Exists(builtApphost) &&
               File.Exists(Path.Combine(builtCli, "rimliaison.dll")) &&
               File.Exists(Path.Combine(builtCli, "rimliaison.deps.json")) &&
               File.Exists(Path.Combine(builtCli, "rimliaison.runtimeconfig.json")),
            "native smoke requires the actual built Release CLI, never a fake apphost");

        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-native-exe-smoke-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "fixture-source");
        string candidateParent = Path.Combine(root, "candidate-packages");
        string invalidMod = Path.Combine(root, "not-a-mod");
        string validMod = Path.Combine(root, "fixture-mod");
        string sourceCommit = new('a', 40);
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(candidateParent);
            Directory.CreateDirectory(invalidMod);
            Directory.CreateDirectory(Path.Combine(validMod, "About"));
            File.WriteAllText(Path.Combine(validMod, "About", "About.xml"),
                "<ModMetaData><name>Native smoke fixture</name><packageId>fixture.native</packageId></ModMetaData>");

            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                    source, builtCli, sourceCommit, candidateParent,
                    new FixtureNativeGitState(source, sourceCommit))
                .GetAwaiter().GetResult();
            string runner = Path.Combine(candidate.PackageRoot, "rimtest.exe");
            string assembly = Path.Combine(candidate.PackageRoot, "rimliaison.dll");
            string sidecar = Path.Combine(candidate.PackageRoot, "rimtest-dev-runner.json");
            Assert(NativeRimtestPackageService.Verify(
                       candidate.PackageRoot, sourceCommit, out _) &&
                   File.Exists(runner) &&
                   !File.Exists(Path.Combine(candidate.PackageRoot, "rimliaison.exe")) &&
                   ToolchainFileHash.Sha256(runner) ==
                       ToolchainFileHash.Sha256(builtApphost),
                "native smoke must use the actual compiled Windows apphost in a verified, restricted package");

            // Prove positive package/source binding only in-process. Never run
            // a valid dev-check child: doing so could launch RimWorld.
            NativeRimtestRunner.Dispatch? valid = NativeRimtestRunner.Prepare(
                runner, ["dev-check", "--json"], validMod);
            Assert(valid is { Allowed: true } &&
                   valid.SourceCommit == sourceCommit &&
                   valid.RunnerExeSha256 == ToolchainFileHash.Sha256(runner) &&
                   valid.CliAssemblySha256 == ToolchainFileHash.Sha256(assembly) &&
                   valid.Args.SequenceEqual(["dev-check", validMod, "--json"]),
                "the real native package must bind exact hashes and workspace without executing a game check");

            string originalTree = SnapshotTree(root);

            static void AssertBlockedNative(
                string executable, string cwd, string[] arguments, string errorCode)
            {
                // The in-process safety precondition prevents a future gate
                // regression from turning a smoke probe into a live game run.
                NativeRimtestRunner.Dispatch? admission = NativeRimtestRunner.Prepare(
                    executable, arguments, cwd);
                Assert(admission is { Allowed: false } &&
                       admission.ErrorCode == errorCode,
                    "unsafe native smoke arguments unexpectedly passed the in-process admission gate");
                PromotionChildProcessResult child = ToolchainPromotionService
                    .RunJsonCommandAsync(executable, arguments, CancellationToken.None,
                        workingDirectory: cwd, processTimeout: TimeSpan.FromSeconds(12))
                    .GetAwaiter().GetResult();
                Assert(child.ExitCode == CliExitCodes.ConservativeSelection &&
                       !child.TimedOut && !child.Cancelled &&
                       child.StartError is null &&
                       string.IsNullOrWhiteSpace(child.Stderr) &&
                       child.Stdout.Length is > 0 and < 4096,
                    "real native apphost must start, terminate promptly and return bounded blocked evidence");
                using JsonDocument result = JsonDocument.Parse(child.Stdout);
                Assert(result.RootElement.GetProperty("schemaVersion").GetString() ==
                           "rimtest-dev-runner/v1" &&
                       result.RootElement.GetProperty("status").GetString() == "blocked" &&
                       result.RootElement.GetProperty("code").GetString() == errorCode,
                    "real native apphost returned an unexpected blocked-command identity");
            }

            AssertBlockedNative(runner, invalidMod,
                ["qualification", "promote", "--json"], "DEV_RUNNER_COMMAND_NOT_ALLOWED");
            AssertBlockedNative(runner, invalidMod,
                ["dev-check", "--json"], "DEV_RUNNER_WORKSPACE_INVALID");

            // The only probe from a syntactically valid mod workspace has
            // deliberately corrupted provenance. Admission must reject it
            // in-process BEFORE the executable is launched.
            string originalSidecar = File.ReadAllText(sidecar);
            string corrupted = originalSidecar.Replace(
                sourceCommit, new string('b', 40), StringComparison.Ordinal);
            Assert(corrupted != originalSidecar,
                "fixture source identity must be present in the sidecar");
            try
            {
                File.WriteAllText(sidecar, corrupted);
                AssertBlockedNative(runner, validMod,
                    ["dev-check", "--json"], "DEV_RUNNER_PROVENANCE_INVALID");
            }
            finally
            {
                File.WriteAllText(sidecar, originalSidecar);
            }

            Assert(NativeRimtestPackageService.Verify(
                       candidate.PackageRoot, sourceCommit, out _) &&
                   SnapshotTree(root) == originalTree,
                "native smoke must restore exact package bytes and leave fixture directories untouched");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeArchivePublicationWaitsForCleanSourceAndPreservesBytes()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-archive-publication-" +
                Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "fixture");
        string scratch = Path.Combine(root, "obj");
        string staged = Path.Combine(scratch, "rimtest-native-candidate.zip");
        string output = Path.Combine(root, "artifacts", "rimtest-native-candidate.zip");
        try
        {
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(scratch);
            File.WriteAllText(Path.Combine(input, "fixture.txt"), "immutable source fixture");
            System.IO.Compression.ZipFile.CreateFromDirectory(
                input, staged, System.IO.Compression.CompressionLevel.Optimal,
                includeBaseDirectory: false);
            long expectedBytes = new FileInfo(staged).Length;
            string expectedSha256 = ToolchainFileHash.Sha256(staged);
            int sourceChecks = 0;

            NativeRimtestArchivePublication.PublishAfterSourceCheckAsync(
                    staged, output, expectedBytes, expectedSha256,
                    _ =>
                    {
                        sourceChecks++;
                        Assert(File.Exists(staged) && !Path.Exists(output) &&
                               !Directory.Exists(Path.GetDirectoryName(output)),
                            "Git source cleanliness must be checked before any artifact output is published");
                        return Task.FromResult(true);
                    }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(sourceChecks == 1 && !File.Exists(staged) &&
                   File.Exists(output) &&
                   new FileInfo(output).Length == expectedBytes &&
                   ToolchainFileHash.Sha256(output) == expectedSha256,
                "archive publication must move the exact verified ZIP only after source admission");

            string blockedStage = Path.Combine(scratch, "unverified.zip");
            string blockedOutput = Path.Combine(root, "artifacts", "blocked.zip");
            File.Copy(output, blockedStage);
            int rejectedChecks = 0;
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestArchivePublication.PublishAfterSourceCheckAsync(
                    blockedStage, blockedOutput, expectedBytes, expectedSha256,
                    _ =>
                    {
                        rejectedChecks++;
                        Assert(!File.Exists(blockedOutput),
                            "failed source admission must precede archive publication");
                        return Task.FromResult(false);
                    }, CancellationToken.None).GetAwaiter().GetResult(),
                "dirty source must fail before emitting any archive");
            Assert(rejectedChecks == 1 && File.Exists(blockedStage) &&
                   !Path.Exists(blockedOutput) &&
                   ToolchainFileHash.Sha256(blockedStage) == expectedSha256,
                "rejected source admission must preserve staged bytes without output");

            string corruptOutput = Path.Combine(root, "artifacts", "corrupt.zip");
            int corruptChecks = 0;
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestArchivePublication.PublishAfterSourceCheckAsync(
                    blockedStage, corruptOutput, expectedBytes, new string('0', 64),
                    _ =>
                    {
                        corruptChecks++;
                        return Task.FromResult(true);
                    }, CancellationToken.None).GetAwaiter().GetResult(),
                "incorrect staged ZIP hash must be rejected before checking source or publishing");
            Assert(corruptChecks == 0 && !Path.Exists(corruptOutput),
                "unverified archive must never publish");

            string mutatedOutput = Path.Combine(root, "artifacts", "mutated.zip");
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestArchivePublication.PublishAfterSourceCheckAsync(
                    blockedStage, mutatedOutput, expectedBytes, expectedSha256,
                    _ =>
                    {
                        File.AppendAllText(blockedStage, "unexpected mutation");
                        return Task.FromResult(true);
                    }, CancellationToken.None).GetAwaiter().GetResult(),
                "ZIP changed during source check must fail before publication");
            Assert(!Path.Exists(mutatedOutput) &&
                   File.Exists(output) &&
                   ToolchainFileHash.Sha256(output) == expectedSha256,
                "rejected mutation must not alter the previously verified output");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeCapturedArtifactImportVerifiesPnProofAndStagesOnlyCandidate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        (string root, string runner, _, _) = CreateNativeRunnerFixture();
        string commit = new('a', 40);
        string operationId = Guid.NewGuid().ToString("D");
        string operations = Path.Combine(root, "pn-operations");
        // Match ProjectNexus's actual outputCaptureRoot=<id>.outputs and
        // declared output path artifacts/<filename>, not duplicated outputs.
        string captured = Path.Combine(operations, operationId + ".outputs",
            "artifacts");
        string legacyDuplicated = Path.Combine(operations,
            operationId + ".outputs", "outputs", "artifacts");
        Assert(!Path.GetFullPath(captured).Equals(
            Path.GetFullPath(legacyDuplicated), StringComparison.OrdinalIgnoreCase),
            "actual captured output directory must not contain duplicated outputs");
        string zip = Path.Combine(captured, "rimtest-native-candidate.zip");
        string receipt = Path.Combine(captured, "rimtest-native-build.json");
        string record = Path.Combine(operations, operationId + ".json");
        string candidates = Path.Combine(root, "CandidatePackages");
        string rejectedCandidates = Path.Combine(root, "RejectedCandidates");
        string manifest = Path.Combine(Path.GetDirectoryName(runner)!,
            CliDeploymentManifestService.FileName);
        using JsonDocument package = JsonDocument.Parse(File.ReadAllText(manifest));
        string packageSha = package.RootElement.GetProperty("packageSha256").GetString()!;
        try
        {
            Directory.CreateDirectory(captured);
            System.IO.Compression.ZipFile.CreateFromDirectory(
                Path.GetDirectoryName(runner)!, zip,
                System.IO.Compression.CompressionLevel.Optimal, false);
            string zipHash = ToolchainFileHash.Sha256(zip);
            string manifestHash = ToolchainFileHash.Sha256(manifest);
            string runnerHash = ToolchainFileHash.Sha256(runner);
            string assemblyHash = ToolchainFileHash.Sha256(
                Path.Combine(Path.GetDirectoryName(runner)!, "rimliaison.dll"));

            void WriteReceipt(bool smoked = true)
            {
                File.WriteAllText(receipt, JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimtest-pn-artifact/v1",
                    status = "source-build-only",
                    sourceCommit = commit,
                    sdkVersion = NativeRimtestSourcePublishCommand.PinnedSdk,
                    targetFramework = "net8.0",
                    runtimeIdentifier = "win-x64",
                    archive = "artifacts/rimtest-native-candidate.zip",
                    archiveSha256 = zipHash,
                    archiveBytes = new FileInfo(zip).Length,
                    archiveSmokePassed = smoked,
                    archiveSmokeMethod = NativeRimtestArchiveSmoke.Method,
                    archiveSmokeProbeCount = NativeRimtestArchiveSmoke.ExpectedProbeCount,
                    packageSha256 = packageSha,
                    manifestSha256 = manifestHash,
                    runnerExeSha256 = runnerHash,
                    cliAssemblySha256 = assemblyHash,
                    qualified = false,
                    installed = false,
                    pathConfigured = false,
                    pnCheckPassed = false
                }));
            }

            void WriteRecord(string status = "passed", bool sourceUnchanged = true,
                string? sourceHead = null)
            {
                File.WriteAllText(record, JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    operationId,
                    projectId = "rimliaison",
                    kind = "managed-local-operation",
                    capabilityId = "local_operation.run",
                    localOperationId = "native.rimtest-artifact-build",
                    localOperationSideEffectClass = "artifact-generation",
                    localOperationDefinitionId = "sha256:" + new string('b', 64),
                    status,
                    exitCode = 0,
                    sourceUnchanged,
                    sourceHead = sourceHead ?? commit,
                    outputEvidence = new[]
                    {
                        new
                        {
                            path = "artifacts/rimtest-native-candidate.zip",
                            size = new FileInfo(zip).Length,
                            sha256 = ToolchainFileHash.Sha256(zip),
                            evidencePath = "outputs/artifacts/rimtest-native-candidate.zip"
                        },
                        new
                        {
                            path = "artifacts/rimtest-native-build.json",
                            size = new FileInfo(receipt).Length,
                            sha256 = ToolchainFileHash.Sha256(receipt),
                            evidencePath = "outputs/artifacts/rimtest-native-build.json"
                        }
                    }
                }));
            }

            (int Exit, string Status, string? Code) Import(
                string[] args, string outputRoot)
            {
                using StringWriter output = new();
                int exit = NativeRimtestManagedArtifactImportCommand.RunForTests(
                    args, output, operations, outputRoot);
                using JsonDocument data = JsonDocument.Parse(output.ToString());
                JsonElement json = data.RootElement;
                Assert(!json.GetProperty("installed").GetBoolean() &&
                       !json.GetProperty("qualified").GetBoolean() &&
                       !json.GetProperty("pathConfigured").GetBoolean() &&
                       !json.GetProperty("pnCheckPassed").GetBoolean() &&
                       !json.GetProperty("independentlyAttestedBuild").GetBoolean(),
                    "captured artifact import must never claim installation or PN qualification");
                return (exit, json.GetProperty("status").GetString()!,
                    json.TryGetProperty("code", out JsonElement code)
                        ? code.GetString() : null);
            }

            string[] args =
            [
                NativeRimtestManagedArtifactImportCommand.Name,
                operationId, commit, zipHash, "--json"
            ];
            WriteReceipt();
            WriteRecord();

            // New read-only validation branch uses all original PN evidence,
            // checks ZIP entry hashes and manifest closure, but never creates
            // an account candidate directory or executes a native apphost.
            string[] auditArgs =
            [
                NativeRimtestManagedArtifactImportCommand.Name,
                operationId, commit, zipHash, "--verify-json"
            ];
            using StringWriter auditOutput = new();
            int auditExit = NativeRimtestManagedArtifactImportCommand.RunForTests(
                auditArgs, auditOutput, operations, rejectedCandidates);
            using JsonDocument auditJson = JsonDocument.Parse(auditOutput.ToString());
            JsonElement proof = auditJson.RootElement;
            Assert(auditExit == CliExitCodes.Success &&
                   proof.GetProperty("status").GetString() == "zip-closure-verified" &&
                   proof.GetProperty("sourceBuildEvidenceMatched").GetBoolean() &&
                   proof.GetProperty("zipClosureVerified").GetBoolean() &&
                   proof.GetProperty("verifiedFileCount").GetInt32() >= 6 &&
                   !proof.GetProperty("qualified").GetBoolean() &&
                   !proof.GetProperty("pnCheckPassed").GetBoolean() &&
                   !Directory.Exists(rejectedCandidates),
                "complete capture ZIP must verify in memory without any account import");

            using StringWriter mismatchedOutput = new();
            int mismatchedExit = NativeRimtestManagedArtifactImportCommand.RunForTests(
                auditArgs, mismatchedOutput, operations, rejectedCandidates,
                expectedDefinition: "sha256:" + new string('0', 64));
            using JsonDocument mismatchedJson =
                JsonDocument.Parse(mismatchedOutput.ToString());
            Assert(mismatchedExit == CliExitCodes.ConservativeSelection &&
                   mismatchedJson.RootElement.GetProperty("status").GetString() ==
                       "blocked" &&
                   !Directory.Exists(rejectedCandidates),
                "wrong exact PN local-operation definition must block without account writes");

            byte[] originalZip = File.ReadAllBytes(zip);
            File.AppendAllText(zip, "mutated content");
            using StringWriter changedOutput = new();
            int changedExit = NativeRimtestManagedArtifactImportCommand.RunForTests(
                auditArgs, changedOutput, operations, rejectedCandidates);
            Assert(changedExit == CliExitCodes.ConservativeSelection &&
                   !Directory.Exists(rejectedCandidates),
                "changed archive after capture must block without account writes");
            File.WriteAllBytes(zip, originalZip);

            // The fixed production wrapper never accepts the fixture producer
            // as the pinned live operation, but its one declared workspace
            // result is written without creating a candidate or importing.
            string auditWorkspace = Path.Combine(root, "audit-workspace");
            using StringWriter fixedOutput = new();
            int fixedExit = NativeRimtestCapturedArtifactAuditCommand.RunForTests(
                [NativeRimtestCapturedArtifactAuditCommand.Name, "--json"],
                fixedOutput, operations, auditWorkspace);
            using JsonDocument fixedJson = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(auditWorkspace,
                    NativeRimtestCapturedArtifactAuditCommand.OutputPath)));
            Assert(fixedExit == CliExitCodes.ConservativeSelection &&
                   fixedJson.RootElement.GetProperty("status").GetString() ==
                       "blocked" &&
                   !fixedJson.RootElement.GetProperty("qualified").GetBoolean() &&
                   !Directory.Exists(Path.Combine(auditWorkspace,
                       "never-used-candidate-root")) &&
                   !Directory.Exists(rejectedCandidates),
                "fixed read-only audit must refuse synthetic producer without import");

            Assert(NativeRimtestManagedArtifactImportCommand.TryParse(
                args, out string? op, out string? source, out string? archiveSha) &&
                op == operationId && source == commit && archiveSha == zipHash,
                "fixed import request must parse only canonical operation and hash identities");
            foreach (string[] malformed in new[]
                     {
                         args[..^1],
                         new[] { args[0], operationId.ToUpperInvariant(), commit, zipHash, "--json" },
                         new[] { args[0], operationId, commit.ToUpperInvariant(), zipHash, "--json" },
                         new[] { args[0], operationId, commit, zipHash, "--install" },
                         new[] { args[0], operationId, commit, zipHash, "--json", "--force" },
                         new[] { args[0], "..\\outside", commit, zipHash, "--json" }
                     })
            {
                var denied = Import(malformed, rejectedCandidates);
                Assert(denied.Exit == CliExitCodes.ConservativeSelection &&
                       denied.Code == "RIMTEST_IMPORT_ARGUMENTS_INVALID" &&
                       !Directory.Exists(rejectedCandidates),
                    "noncanonical import arguments must fail before account mutation");
            }

            var imported = Import(args, candidates);
            string final = Path.Combine(candidates, "rimtest-" + commit);
            Assert(imported.Exit == CliExitCodes.Success &&
                   imported.Status == "candidate-imported" &&
                   NativeRimtestPackageService.Verify(final, commit, out _) &&
                   ToolchainFileHash.Sha256(
                       Path.Combine(final, CliDeploymentManifestService.FileName)) ==
                       manifestHash &&
                   !Directory.Exists(Path.Combine(root, "Installed")) &&
                   !Directory.EnumerateDirectories(candidates,
                       ".rimtest-import-*").Any(),
                "only a fully verified candidate may be imported atomically; no installation");
            Assert(Import(args, candidates).Code ==
                       "RIMTEST_IMPORT_VERSION_ALREADY_EXISTS" &&
                   NativeRimtestPackageService.Verify(final, commit, out _),
                "import must never overwrite an existing version");

            WriteRecord(status: "failed");
            Assert(Import(args, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_OPERATION_UNVERIFIED" &&
                   !Directory.Exists(rejectedCandidates),
                "failed ProjectNexus operation must not produce an account candidate");
            WriteRecord(sourceUnchanged: false);
            Assert(Import(args, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_OPERATION_UNVERIFIED",
                "source-mutated operation must not be imported");
            WriteRecord(sourceHead: new string('c', 40));
            Assert(Import(args, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_OPERATION_UNVERIFIED",
                "wrong source HEAD must not be imported");

            WriteReceipt(smoked: false);
            WriteRecord();
            Assert(Import(args, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_RECEIPT_UNVERIFIED",
                "archive lacking actual smoke proof must not be imported");

            WriteReceipt();
            WriteRecord();
            File.AppendAllText(zip, "tampered capture");
            Assert(Import(args, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_ARCHIVE_HASH_MISMATCH",
                "altered operation output ZIP must fail before extraction");

            File.Delete(zip);
            using (var unsafeZip = System.IO.Compression.ZipFile.Open(
                zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    unsafeZip.CreateEntry("../outside.txt").Open());
                writer.Write("traversal content");
            }
            zipHash = ToolchainFileHash.Sha256(zip);
            WriteReceipt();
            WriteRecord();
            string[] unsafeArgs =
            [
                NativeRimtestManagedArtifactImportCommand.Name,
                operationId, commit, zipHash, "--json"
            ];
            Assert(Import(unsafeArgs, rejectedCandidates).Code ==
                       "RIMTEST_IMPORT_ZIP_UNSAFE" &&
                   !Directory.Exists(rejectedCandidates) &&
                   !File.Exists(Path.Combine(root, "outside.txt")) &&
                   NativeRimtestPackageService.Verify(final, commit, out _),
                "ZIP traversal must be rejected without writes or mutation to a prior candidate");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static void NativeEmittedZipSmokeVerifiesExactArchiveWithoutRunningGame()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string builtCli = Path.Combine(
            FindSourceRoot(), "src", "RimLiaison.Cli", "bin", "Release", "net8.0");
        Assert(File.Exists(Path.Combine(builtCli, "rimliaison.exe")),
            "archive smoke needs the actual Release native apphost");

        string root = Path.Combine(
            Path.GetTempPath(), "rimtest-zip-smoke-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string packages = Path.Combine(root, "packages");
        string sourceCommit = new('a', 40);
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(packages);
            NativeRimtestPackageCandidate candidate =
                NativeRimtestPackageService.StageForTestsAsync(
                    source, builtCli, sourceCommit, packages,
                    new FixtureNativeGitState(source, sourceCommit))
                .GetAwaiter().GetResult();

            string zip = Path.Combine(root, "emitted-candidate.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(
                candidate.PackageRoot, zip,
                System.IO.Compression.CompressionLevel.Optimal,
                includeBaseDirectory: false);
            string archiveSha256 = ToolchainFileHash.Sha256(zip);
            string originalTree = SnapshotTree(root);

            int probes = NativeRimtestArchiveSmoke.VerifyAsync(
                    zip, archiveSha256, sourceCommit, candidate.PackageSha256,
                    Path.Combine(root, "owned-archive-smoke"), CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(probes == NativeRimtestArchiveSmoke.ExpectedProbeCount &&
                   NativeRimtestArchiveSmoke.Method ==
                       "zip-extracted-blocked-apphost/v1" &&
                   ToolchainFileHash.Sha256(zip) == archiveSha256 &&
                   NativeRimtestPackageService.Verify(
                       candidate.PackageRoot, sourceCommit, out _) &&
                   SnapshotTree(root) == originalTree,
                "extracted exact ZIP must pass bounded native rejection probes without changing source or package");

            string badScratch = Path.Combine(root, "bad-hash-smoke");
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestArchiveSmoke.VerifyAsync(
                    zip, new string('0', 64), sourceCommit,
                    candidate.PackageSha256, badScratch, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "wrong archive SHA must fail before any extraction or child launch");
            Assert(!Directory.Exists(badScratch) &&
                   SnapshotTree(root) == originalTree,
                "mismatching archive identity must not leave scratch or mutate input");

            string badSourceScratch = Path.Combine(root, "bad-source-smoke");
            AssertThrows<InvalidDataException>(() =>
                NativeRimtestArchiveSmoke.VerifyAsync(
                    zip, archiveSha256, new string('b', 40),
                    candidate.PackageSha256, badSourceScratch, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "mismatching source revision must block before any executable is launched");
            Assert(!Directory.Exists(badSourceScratch) &&
                   SnapshotTree(root) == originalTree,
                "rejected source identity must leave all fixture bytes unchanged");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
