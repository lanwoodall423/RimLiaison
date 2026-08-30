using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimLiaison.Recovery;

namespace RimLiaison.DevBridge;

/// <summary>
/// Internal production transaction boundary.  It owns build, package, deployment,
/// generation, readiness, lease, and cleanup orchestration without delegating to
/// the legacy PowerShell transaction consumer.
/// </summary>
public sealed class InternalDevelopmentTransactionService : IDevBridgeModDevelopmentAdapter
{
    private const int MaxDescriptorBytes = 128 * 1024;
    private readonly IDevBridgeProcessTransport transport;
    private readonly DevBridgeModDevelopmentAdapterOptions options;

    public InternalDevelopmentTransactionService(
        IDevBridgeProcessTransport transport,
        DevBridgeModDevelopmentAdapterOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.RootPath) ||
            string.IsNullOrWhiteSpace(options.DeploymentRoot))
        {
            throw new ArgumentException("The internal transaction requires coordinator and deployment roots.", nameof(options));
        }
    }

    public Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        CancellationToken cancellationToken = default) =>
        RunAsync(project, repositoryRoot, sourceFingerprint, workflowId, null, cancellationToken);

    public async Task<DevBridgeModDevelopmentResult> RunAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        string? workflowId,
        DevBridgeModDevelopmentExecutionContext? executionContext,
        CancellationToken cancellationToken = default)
    {
        string transactionId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryRoot) ||
            string.IsNullOrWhiteSpace(sourceFingerprint))
        {
            return Failure(project, workflowId, transactionId,
                "RIMTEST_DEVBRIDGE_PROJECT_MISSING",
                "The internal development transaction requires project, repository root, and source fingerprint.");
        }

        string sourceRoot;
        try
        {
            sourceRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Failure(project, workflowId, transactionId,
                "RIMTEST_REPOSITORY_ROOT_INVALID", "The repository root is invalid.");
        }

        ProjectOwnedDescriptorMaterialization? materialization = null;
        string descriptorPath;
        DevBridgeDevelopmentDescriptor? descriptor = null;
        try
        {
            if (options.DescriptorPath is null)
            {
                materialization = ProjectOwnedDescriptorMaterializer.Materialize(
                    project,
                    sourceRoot,
                    options.DeploymentRoot,
                    out string? metadataCode,
                    out string? metadataError);
                if (materialization is null)
                {
                    return Failure(project, workflowId, transactionId,
                        metadataCode ?? "PROJECT_METADATA_MISSING", metadataError);
                }
                descriptorPath = materialization.DescriptorPath;
                descriptor = materialization.Descriptor;
            }
            else
            {
                descriptorPath = Path.GetFullPath(options.DescriptorPath);
                descriptor = ReadDescriptor(descriptorPath, project);
            }

            if (string.Equals(descriptor.DeploymentRole, "tooling-only", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(project, workflowId, transactionId,
                    "DEVBRIDGE_TOOLING_ONLY_NOT_DEPLOYABLE",
                    "Tooling-only projects are not eligible for production deployment.");
            }

            ValidationRecipeResolutionResult recipeResolution =
                ValidationRecipeResolver.Resolve(
                    project,
                    descriptor.TestRecipe,
                    sourceRoot,
                    descriptor.TestRecipePath,
                    options.RootPath);
            if (!recipeResolution.IsSuccess || recipeResolution.Recipe is null)
            {
                return Failure(project, workflowId, transactionId,
                    recipeResolution.ErrorCode ?? "PROJECT_RECIPE_NOT_FOUND",
                    recipeResolution.Error ?? "The declared development recipe could not be resolved.");
            }
            if (!string.IsNullOrWhiteSpace(descriptor.RecipeSha256) &&
                !string.Equals(descriptor.RecipeSha256, recipeResolution.Recipe.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(project, workflowId, transactionId,
                    "PROJECT_RECIPE_HASH_MISMATCH",
                    "The resolved validation recipe changed after metadata materialization.");
            }

            string transactionRoot = Path.Combine(Path.GetTempPath(), "RimLiaison-development-" + transactionId);
            string stagingRoot = Path.Combine(transactionRoot, "staging");
            string intermediateRoot = Path.Combine(transactionRoot, "obj");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(intermediateRoot);

            DevBridgeRecipeAdapter recipeAdapter = new(transport, ToBridgeOptions(recipeResolution.Recipe.Path));
            DevBridgeRecipeShowResult show = await recipeAdapter.ShowAsync(descriptor.TestRecipe, cancellationToken).ConfigureAwait(false);
            if (!show.Status.IsSuccess || show.Definition is null)
            {
                return Failure(project, workflowId, transactionId,
                    show.Status.ErrorCode ?? "DEVBRIDGE_RECIPE_SHOW_FAILED",
                    show.Status.Error ?? "The declared development recipe could not be loaded.", show.Status.Outcome);
            }
            if (!RecipeContainsProject(show.Definition.Value, project))
            {
                return Failure(project, workflowId, transactionId,
                    "DEVELOPMENT_RECIPE_PROJECT_MISMATCH",
                    $"Recipe '{descriptor.TestRecipe}' does not request project '{project}'.");
            }
            DevBridgeRecipePlanResult plan = await recipeAdapter.PlanAsync(descriptor.TestRecipe, cancellationToken).ConfigureAwait(false);
            if (!plan.Status.IsSuccess || plan.Plan is null)
            {
                return Failure(project, workflowId, transactionId,
                    plan.Status.ErrorCode ?? "DEVBRIDGE_RECIPE_PLAN_FAILED",
                    plan.Status.Error ?? "The declared development recipe could not be planned.", plan.Status.Outcome);
            }

            DevBridgeProcessResult statusBeforeBuild = await InvokeAsync(["status", "--json"], options.Timeout, workflowId, cancellationToken).ConfigureAwait(false);
            if (!TrySuccess(statusBeforeBuild, out JsonElement statusRoot, out DevBridgeAdapterStatus? statusFailure))
            {
                return Failure(project, workflowId, transactionId,
                    statusFailure!.ErrorCode ?? "DEVBRIDGE_STATUS_FAILED",
                    statusFailure.Error ?? "DevBridge status was unavailable.", statusFailure.Outcome);
            }
            string? rimWorldRoot = GetString(statusRoot, "rimworldRoot") ?? GetString(statusRoot, "rimWorldRoot");
            if (string.IsNullOrWhiteSpace(rimWorldRoot) || !Directory.Exists(rimWorldRoot))
            {
                return Failure(project, workflowId, transactionId,
                    "RIMWORLD_DIR_UNRESOLVED",
                    "DevBridge did not resolve an existing RimWorld installation root.");
            }
            string expectedArtifact = SafeCombine(stagingRoot, descriptor.ExpectedAssembly);
            string buildProject = Path.GetFullPath(Path.Combine(sourceRoot, descriptor.SourceProject));
            string buildWorkingDirectory = Path.GetDirectoryName(buildProject) ?? sourceRoot;
            string[] buildArguments =
            [
                "build", buildProject,
                "--configuration", descriptor.Configuration,
                "--output", stagingRoot,
                "--nologo",
                "-p:BaseIntermediateOutputPath=" + Path.Combine(intermediateRoot, "base") + Path.DirectorySeparatorChar,
                "-p:IntermediateOutputPath=" + Path.Combine(intermediateRoot, "intermediate") + Path.DirectorySeparatorChar,
                "-p:MSBuildProjectExtensionsPath=" + Path.Combine(intermediateRoot, "extensions") + Path.DirectorySeparatorChar,
                "-p:OutputPath=" + stagingRoot + Path.DirectorySeparatorChar,
                "-p:RIMWORLD_DIR=" + Path.GetFullPath(rimWorldRoot),
                "-p:RIMWORLD_ROOT=" + Path.GetFullPath(rimWorldRoot)
            ];
            DevBridgeProcessResult build = await transport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    "dotnet",
                    buildWorkingDirectory,
                    buildArguments,
                    options.Timeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    DevBridgeProcessEnvironment.ForWorkflow(workflowId),
                    "build:" + project), cancellationToken).ConfigureAwait(false);
            if (build.Cancelled)
            {
                return Failure(project, workflowId, transactionId, "RIMTEST_CANCELLED", "The development build was cancelled.", DevBridgeOutcomeKind.Cancelled);
            }
            if (build.TimedOut)
            {
                return Failure(project, workflowId, transactionId, "DEVELOPMENT_BUILD_TIMEOUT", "The development build exceeded its bounded timeout.", DevBridgeOutcomeKind.InfrastructureFailure, BuildDiagnostics(build, buildProject, stagingRoot, transactionId, workflowId));
            }
            if (build.ExitCode is not 0 || build.StartError is not null)
            {
                return Failure(project, workflowId, transactionId, "DEVELOPMENT_BUILD_FAILED", "The declared project build failed.", DevBridgeOutcomeKind.TestFailure, BuildDiagnostics(build, buildProject, stagingRoot, transactionId, workflowId));
            }
            if (!File.Exists(expectedArtifact))
            {
                return Failure(project, workflowId, transactionId, "DEVELOPMENT_ARTIFACT_MISSING", "The build did not produce the expected assembly.", DevBridgeOutcomeKind.TestFailure, BuildDiagnostics(build, buildProject, stagingRoot, transactionId, workflowId));
            }

            string targetRoot = Path.GetFullPath(options.DeploymentRoot!);
            Directory.CreateDirectory(targetRoot);
            string targetAssembly = SafeCombine(targetRoot, descriptor.DeploymentTarget);
            string assemblyRelative = NormalizeRelative(Path.GetRelativePath(targetRoot, targetAssembly));
            List<PackageEntry> package = BuildPackage(sourceRoot, stagingRoot, expectedArtifact, descriptor.RuntimePackage, assemblyRelative);
            string packageHash = ComputePackageHash(package);
            string manifestPath = DeploymentManifestPath(options.RootPath, project, targetRoot);

            using Mutex deploymentLock = new(false, "Global\\RimLiaison-DevBridge-Deployment-" + HashText(targetRoot)[..24]);
            if (!deploymentLock.WaitOne(options.Timeout))
            {
                return Failure(project, workflowId, transactionId, "DEVBRIDGE_DEPLOYMENT_LOCK_TIMEOUT", "The production deployment boundary was busy.");
            }

            string? leaseId = null;
            int? generationBefore = GetInt(statusRoot, "generation");
            bool registered = false;
            bool changed = false;
            try
            {
                JsonElement? previous = ReadJson(manifestPath);
                EnsureOwnership(targetRoot, package, previous);
                changed = NeedsDeployment(targetRoot, package, previous, packageHash);

                DevBridgeLeaseAdapter leaseAdapter = new(transport, ToBridgeOptions());
                DevBridgeLeaseResult lease = string.IsNullOrWhiteSpace(executionContext?.LeaseId)
                    ? await leaseAdapter.BeginLeaseAsync(workflowId, cancellationToken).ConfigureAwait(false)
                    : new DevBridgeLeaseResult(new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success), executionContext.LeaseId, generationBefore);
                if (!lease.IsUsable && string.IsNullOrWhiteSpace(lease.LeaseId))
                {
                    return Failure(project, workflowId, transactionId,
                        lease.Status.ErrorCode ?? "DEVBRIDGE_LEASE_ACQUIRE_FAILED",
                        lease.Status.Error ?? "DevBridge did not grant a transaction lease.", lease.Status.Outcome);
                }
                leaseId = lease.LeaseId;
                string activeLeaseId = lease.LeaseId!;

                DevBridgeProcessResult register = await InvokeAsync(["project", "register", project, "--id", "rimliaison-" + transactionId, "--json"], options.Timeout, workflowId, cancellationToken).ConfigureAwait(false);
                if (!TrySuccess(register, out _, out DevBridgeAdapterStatus? registerFailure))
                {
                    return Failure(project, workflowId, transactionId, registerFailure!.ErrorCode ?? "DEVBRIDGE_PROJECT_REGISTER_FAILED", registerFailure.Error ?? "DevBridge project registration failed.", registerFailure.Outcome, leaseId: leaseId, generation: generationBefore);
                }
                registered = true;

                int generationAfter = generationBefore ?? 0;
                if (changed)
                {
                    await RequireCoordinatorSuccess(["test", "renew", activeLeaseId, "--json"], "DEVBRIDGE_LEASE_RENEW_FAILED", workflowId, cancellationToken).ConfigureAwait(false);
                    DevBridgeProcessResult stop = await InvokeAsync(["stop", activeLeaseId, "--json"], options.Timeout, workflowId, cancellationToken).ConfigureAwait(false);
                    if (!TrySuccess(stop, out JsonElement stopRoot, out DevBridgeAdapterStatus? stopFailure) ||
                        !string.Equals(GetString(stopRoot, "gameState"), "STOPPED", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure(project, workflowId, transactionId, stopFailure?.ErrorCode ?? "DEVELOPMENT_MAINTENANCE_NOT_CONFIRMED", stopFailure?.Error ?? "DevBridge did not prove stopped maintenance state.", DevBridgeOutcomeKind.InfrastructureFailure, leaseId: leaseId, generation: generationBefore);
                    }

                    foreach (PackageEntry entry in package)
                    {
                        string target = SafeCombine(targetRoot, entry.TargetPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        CopyAtomic(entry.SourcePath, target);
                        if (!string.Equals(HashFile(target), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            return Failure(project, workflowId, transactionId, "DEVELOPMENT_DEPLOYMENT_HASH_MISMATCH", "The deployed package did not match staged content.", leaseId: leaseId, generation: generationBefore);
                        }
                    }

                    await RequireCoordinatorSuccess(["test", "renew", activeLeaseId, "--json"], "DEVBRIDGE_LEASE_RENEW_FAILED", workflowId, cancellationToken).ConfigureAwait(false);
                    await RequireCoordinatorSuccess(["ensure-ready", activeLeaseId, "--json"], "DEVELOPMENT_GENERATION_NOT_READY", workflowId, cancellationToken).ConfigureAwait(false);
                    DevBridgeProcessResult ready = await InvokeAsync(["wait-ready", "--json"], options.Timeout, workflowId, cancellationToken).ConfigureAwait(false);
                    if (!TrySuccess(ready, out JsonElement readyRoot, out DevBridgeAdapterStatus? readyFailure) ||
                        !string.Equals(GetString(readyRoot, "state"), "READY", StringComparison.OrdinalIgnoreCase))
                    {
                        return Failure(project, workflowId, transactionId, readyFailure?.ErrorCode ?? "DEVELOPMENT_GENERATION_NOT_READY", readyFailure?.Error ?? "DevBridge did not prove a READY generation.", DevBridgeOutcomeKind.InfrastructureFailure, leaseId: leaseId, generation: generationBefore);
                    }
                    generationAfter = GetInt(readyRoot, "generation") ?? 0;
                    if (generationAfter <= (generationBefore ?? 0))
                    {
                        return Failure(project, workflowId, transactionId, "DEVELOPMENT_GENERATION_MISMATCH", "Deployment did not establish a newer accepted generation.", leaseId: leaseId, generation: generationAfter);
                    }
                }

                WriteDeploymentManifest(manifestPath, targetRoot, package, packageHash, generationAfter);
                WriteArtifactState(options.RootPath, targetRoot, project, package, packageHash, generationAfter, manifestPath);
                DevBridgeLeaseResult released = await leaseAdapter.EndLeaseAsync(leaseId!, workflowId, CancellationToken.None).ConfigureAwait(false);
                if (!released.Status.IsSuccess)
                {
                    leaseId = null;
                    return Failure(project, workflowId, transactionId, "DEVBRIDGE_LEASE_RELEASE_FAILED", released.Status.Error ?? "DevBridge did not prove lease release.", DevBridgeOutcomeKind.InfrastructureFailure, generation: generationAfter);
                }
                leaseId = null;

                string deployedAssemblyHash = HashFile(targetAssembly)!;
                return new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success, RecoveryState: PrerequisiteRecoveryState.Ready),
                    true,
                    transactionId,
                    workflowId,
                    generationAfter,
                    null,
                    new DevBridgeArtifactFreshness(
                        sourceFingerprint,
                        HashFile(expectedArtifact),
                        deployedAssemblyHash,
                        changed ? "deployed" : "unchanged",
                        generationBefore,
                        generationAfter,
                        generationAfter,
                        true,
                        changed ? "package-manifest-plus-new-owned-generation" : "package-manifest-plus-owned-generation-state",
                        transactionId,
                        workflowId,
                        activeLeaseId,
                        null,
                        BuiltPackageSha256: packageHash,
                        DeployedPackageSha256: packageHash,
                        DeploymentManifestPath: manifestPath,
                        RecipeId: recipeResolution.Recipe.Id,
                        RecipeOwner: recipeResolution.Recipe.OwnerProject,
                        RecipeSource: recipeResolution.Recipe.Source,
                        RecipeSha256: recipeResolution.Recipe.Sha256,
                        RecipeSchemaVersion: recipeResolution.Recipe.SchemaVersion),
                    BuildDiagnostics(build, buildProject, stagingRoot, transactionId, workflowId),
                    []);
            }
            finally
            {
                if (registered)
                {
                    try { await InvokeAsync(["project", "release", "rimliaison-" + transactionId, "--json"], options.Timeout, workflowId, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                if (leaseId is not null)
                {
                    try { await new DevBridgeLeaseAdapter(transport, ToBridgeOptions()).EndLeaseAsync(leaseId, workflowId, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                deploymentLock.ReleaseMutex();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(project, workflowId, transactionId, "RIMTEST_CANCELLED", "The internal development transaction was cancelled.", DevBridgeOutcomeKind.Cancelled);
        }
        catch (Exception exception)
        {
            return Failure(project, workflowId, transactionId, "DEVBRIDGE_INTERNAL_TRANSACTION_FAILED", Bound(exception.Message));
        }
        finally
        {
            if (materialization is not null)
            {
                ProjectOwnedDescriptorMaterializer.Delete(materialization);
            }
        }
    }

    private DevBridgeAdapterOptions ToBridgeOptions(string? recipeFilePath = null) => new()
    {
        CommandPath = options.RootPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? options.RootPath
            : Path.Combine(options.RootPath, "DevBridge.cmd"),
        RootPath = options.RootPath,
        RecipeFilePath = recipeFilePath,
        ShowPlanTimeout = options.Timeout,
        RunTimeout = options.Timeout,
        MaxStdoutBytes = options.MaxStdoutBytes,
        MaxStderrBytes = options.MaxStderrBytes
    };

    private async Task<DevBridgeProcessResult> InvokeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workflowId,
        CancellationToken cancellationToken) =>
        await transport.ExecuteAsync(
            new DevBridgeProcessRequest(
                ToBridgeOptions().CommandPath,
                options.RootPath,
                arguments,
                timeout,
                options.MaxStdoutBytes,
                options.MaxStderrBytes,
                DevBridgeProcessEnvironment.ForWorkflow(workflowId),
                "internal-development-transaction"), cancellationToken).ConfigureAwait(false);

    private async Task RequireCoordinatorSuccess(
        IReadOnlyList<string> arguments,
        string errorCode,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process = await InvokeAsync(arguments, options.Timeout, workflowId, cancellationToken).ConfigureAwait(false);
        if (!TrySuccess(process, out _, out DevBridgeAdapterStatus? failure))
        {
            throw new InvalidOperationException(failure?.Error ?? errorCode);
        }
    }

    private static DevBridgeDevelopmentDescriptor ReadDescriptor(string path, string project)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxDescriptorBytes)
        {
            throw new InvalidOperationException("The development descriptor is missing or exceeds its bound.");
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        string value(string name) => root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;
        string descriptorProject = value("project");
        if (!string.Equals(descriptorProject, project, StringComparison.Ordinal)) throw new InvalidOperationException("The development descriptor project does not match the request.");
        return new DevBridgeDevelopmentDescriptor(
            value("schemaVersion"), descriptorProject, value("sourceProject"), value("configuration"),
            value("expectedAssembly"), value("deploymentTarget"), value("testRecipe"),
            root.TryGetProperty("runtimePackage", out JsonElement package) ? package.Clone() : null,
            root.TryGetProperty("deploymentRole", out JsonElement role) ? role.GetString() : null,
            value("canonicalProjectId"), value("metadataOwner"), value("metadataSource"),
            value("contractProducer"), value("materializedContractPath"),
            value("testRecipePath"), value("resolvedRecipePath"), value("recipeSource"),
            value("recipeSha256"), value("recipeSchemaVersion"));
    }

    private static bool RecipeContainsProject(JsonElement definition, string project) =>
        definition.TryGetProperty("projects", out JsonElement projects) && projects.ValueKind == JsonValueKind.Array &&
        projects.EnumerateArray().Any(value => string.Equals(value.GetString(), project, StringComparison.Ordinal));

    private static List<PackageEntry> BuildPackage(
        string sourceRoot,
        string stagingRoot,
        string expectedArtifact,
        JsonElement? runtimePackage,
        string assemblyRelative)
    {
        List<PackageEntry> entries = [];
        if (runtimePackage is JsonElement package && package.ValueKind == JsonValueKind.Object)
        {
            string packageSource = GetString(package, "sourceRoot") ?? ".";
            string packageRoot = Path.GetFullPath(Path.Combine(sourceRoot, packageSource == "." ? string.Empty : SafeRelative(packageSource)));
            string[] includes = GetStringArray(package, "include");
            string[] excludes = GetStringArray(package, "exclude");
            if (includes.Length == 0) throw new InvalidOperationException("runtimePackage.include must contain at least one pattern.");
            string[] defaultExcluded = [".git/**", "Source/**", "bin/**", "obj/**", "tests/**", "Tests/**", "TestResults/**", ".rimdev/**", ".rimctx/**", ".rimerror/**"];
            HashSet<string> explicitRoots = includes.Select(pattern => pattern.Replace('\\', '/').Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            excludes = defaultExcluded.Where(value => !explicitRoots.Contains(value[..^3])).Concat(excludes).ToArray();
            foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizeRelative(Path.GetRelativePath(packageRoot, file));
                if (!includes.Any(pattern => Wildcard(pattern).IsMatch(relative)) || excludes.Any(pattern => Wildcard(pattern).IsMatch(relative))) continue;
                string staged = SafeCombine(stagingRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                CopyAtomic(file, staged);
                entries.Add(new PackageEntry(file, relative, HashFile(staged)!, new FileInfo(staged).Length));
            }
        }
        entries.RemoveAll(entry =>
            string.Equals(entry.TargetPath, assemblyRelative, StringComparison.OrdinalIgnoreCase));
        entries.Add(new PackageEntry(expectedArtifact, assemblyRelative, HashFile(expectedArtifact)!, new FileInfo(expectedArtifact).Length));
        return entries.OrderBy(entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Regex Wildcard(string pattern) => new(
        "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ComputePackageHash(IEnumerable<PackageEntry> entries) =>
        HashText(string.Join('\n', entries.OrderBy(entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase).Select(entry => NormalizeRelative(entry.TargetPath) + '\0' + entry.Sha256)));

    private static string DeploymentManifestPath(string coordinatorRoot, string project, string targetRoot) =>
        Path.Combine(coordinatorRoot, "Runtime", "deployment-manifests", project + "-" + HashText(targetRoot.ToLowerInvariant())[..24] + ".json");

    private static bool NeedsDeployment(string targetRoot, IReadOnlyList<PackageEntry> package, JsonElement? previous, string packageHash)
    {
        if (previous is not JsonElement prior || GetString(prior, "packageSha256") != packageHash) return true;
        foreach (JsonElement entry in prior.GetProperty("files").EnumerateArray())
        {
            string path = GetString(entry, "path") ?? string.Empty;
            string sha = GetString(entry, "sha256") ?? string.Empty;
            if (!string.Equals(HashFile(SafeCombine(targetRoot, path)), sha, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return package.Any(entry => !string.Equals(HashFile(SafeCombine(targetRoot, entry.TargetPath)), entry.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureOwnership(string targetRoot, IReadOnlyList<PackageEntry> package, JsonElement? previous)
    {
        if (previous is not JsonElement prior || !prior.TryGetProperty("files", out JsonElement files)) return;
        HashSet<string> expected = package.Select(entry => NormalizeRelative(entry.TargetPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in files.EnumerateArray())
        {
            string path = GetString(entry, "path") ?? string.Empty;
            string? actual = HashFile(SafeCombine(targetRoot, path));
            if (actual is not null &&
                !string.Equals(actual, GetString(entry, "sha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("DEVBRIDGE_DEPLOYMENT_OWNERSHIP_AMBIGUOUS: managed deployment content changed outside the transaction.");
            }
        }
    }

    private static void WriteDeploymentManifest(string path, string targetRoot, IReadOnlyList<PackageEntry> package, string packageHash, int generation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonObject value = new()
        {
            ["schemaVersion"] = "devbridge-deployment-manifest/v1",
            ["deploymentRoot"] = targetRoot,
            ["packageSha256"] = packageHash,
            ["generation"] = generation,
            ["files"] = new JsonArray(package.Select(entry => (JsonNode)new JsonObject { ["path"] = NormalizeRelative(entry.TargetPath), ["sha256"] = entry.Sha256, ["size"] = entry.Size }).ToArray())
        };
        WriteAtomic(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteArtifactState(string coordinatorRoot, string targetRoot, string project, IReadOnlyList<PackageEntry> package, string packageHash, int generation, string manifestPath)
    {
        string runtime = Path.Combine(coordinatorRoot, "Runtime");
        Directory.CreateDirectory(runtime);
        PackageEntry assembly = package.First(entry => entry.TargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        JsonObject value = new()
        {
            ["schemaVersion"] = "devbridge-mod-development-artifact/v1",
            ["project"] = project,
            ["deploymentRoot"] = targetRoot,
            ["deployedArtifactSha256"] = assembly.Sha256,
            ["deployedPackageSha256"] = packageHash,
            ["generation"] = generation,
            ["deploymentManifestPath"] = manifestPath
        };
        WriteAtomic(Path.Combine(runtime, "mod-development-artifact.json"), value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyAtomic(string source, string target)
    {
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, temporary, true);
        File.Move(temporary, target, true);
    }

    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string SafeCombine(string root, string relative) =>
        IsSafeRelative(relative) ? Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))) : throw new InvalidOperationException("An unsafe relative deployment path was supplied.");

    private static string SafeRelative(string value) => IsSafeRelative(value) ? value : throw new InvalidOperationException("An unsafe package path was supplied.");

    private static bool IsSafeRelative(string value) => !Path.IsPathRooted(value) && !value.Contains(':') && !value.Replace('\\', '/').Split('/').Contains("..");

    private static string NormalizeRelative(string value) => value.Replace('\\', '/').TrimStart('/');

    private static string? HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static bool TrySuccess(DevBridgeProcessResult process, out JsonElement root, out DevBridgeAdapterStatus? failure)
    {
        root = default;
        failure = null;
        if (process.Cancelled)
        {
            failure = new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Cancelled, "RIMTEST_CANCELLED");
            return false;
        }
        if (process.TimedOut)
        {
            failure = new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Timeout, "DEVBRIDGE_COMMAND_TIMEOUT");
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout ?? string.Empty);
            root = document.RootElement.Clone();
            bool success = !root.TryGetProperty("success", out JsonElement property) || property.ValueKind != JsonValueKind.False;
            if (process.ExitCode is not 0 || !success)
            {
                failure = new DevBridgeAdapterStatus(
                    process.ExitCode is > 0 ? DevBridgeOutcomeKind.DevBridgeRefusal : DevBridgeOutcomeKind.InfrastructureFailure,
                    GetString(root, "errorCode") ?? "DEVBRIDGE_COMMAND_FAILED",
                    GetString(root, "error") ?? process.Stderr);
                return false;
            }
            return true;
        }
        catch (JsonException exception)
        {
            failure = new DevBridgeAdapterStatus(DevBridgeOutcomeKind.MalformedResponse, "DEVBRIDGE_RESPONSE_INVALID", exception.Message);
            return false;
        }
    }

    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? GetInt(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;
    private static string[] GetStringArray(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value) ? "The internal development transaction failed." : value.Length <= 4096 ? value : value[..4096];

    private static DevBridgeBuildDiagnostics BuildDiagnostics(DevBridgeProcessResult process, string sourceProject, string staging, string transactionId, string? workflowId) =>
        new("dotnet build", process.ExitCode, Bound(process.Stdout), sourceProject, staging, process.TimedOut, null, Bound(process.Stderr), Bound(process.Stderr), null, process.Cancelled, null, workflowId, null, null, null, null, null, null, null, null, null, "RimLiaison", "project-build", null, null, null);

    private static DevBridgeModDevelopmentResult Failure(string project, string? workflowId, string transactionId, string code, string? error, DevBridgeOutcomeKind outcome = DevBridgeOutcomeKind.InfrastructureFailure, DevBridgeBuildDiagnostics? build = null, string? leaseId = null, int? generation = null) =>
        new(project, new DevBridgeAdapterStatus(outcome, code, Bound(error)), false, transactionId, workflowId, generation, leaseId, null, build, []);

    private sealed record PackageEntry(string SourcePath, string TargetPath, string Sha256, long Size);
}
