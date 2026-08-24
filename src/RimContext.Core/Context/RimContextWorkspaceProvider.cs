using System.Runtime.InteropServices;
using System.Text.Json;
using RimContext.Core.Configuration;
using RimContext.Core.Model;

namespace RimContext.Core.Context;

/// <summary>
/// Supplies facts owned by the RimContext process itself. It deliberately does
/// not infer Git, DevBridge, RimWorld, or test-run state; those sections remain
/// explicit until their owning provider contributes a snapshot.
/// </summary>
public sealed class RimContextWorkspaceProvider : IRimContextBundleProvider
{
    public string Id => "rimcontext";

    public ValueTask<RimContextProviderSnapshot> CollectAsync(
        RimContextProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceConfiguration configuration = WorkspaceConfiguration.Resolve(
            request.RootPath,
            request.StorePath,
            request.AssemblyRoots);
        DateTimeOffset now = request.NowUtc;
        string[] capabilities =
        [
            "static-index",
            "affected-impact",
            "definitions",
            "references",
            "harmony"
        ];
        var topology = new RimContextTopology
        {
            Components =
            [
                new RimContextComponent
                {
                    Name = "RimContext",
                    Role = "Static source, Defs, Harmony, project, and dependency context.",
                    Repository = "RimContext/RimLiaison",
                    Version = IndexConstants.ToolVersion,
                    LocalPath = AppContext.BaseDirectory,
                    Capabilities = capabilities
                }
            ],
            Dependencies = []
        };
        var environment = new RimContextEnvironmentState
        {
            Os = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Compiler = IndexConstants.SemanticIndexerVersion,
            RimWorldVersion = RimContextBundleStatuses.Unknown,
            Tools =
            [
                new RimContextToolVersion
                {
                    Name = "rimcontext",
                    Version = IndexConstants.ToolVersion
                },
                new RimContextToolVersion
                {
                    Name = ".NET",
                    Version = Environment.Version.ToString()
                }
            ],
            Configuration = new RimContextSetting[]
            {
                new RimContextSetting
                {
                    Name = "assemblyRootCount",
                    Value = request.AssemblyRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                new RimContextSetting
                {
                    Name = "indexStore",
                    Value = configuration.StoreDisplayPath()
                },
                new RimContextSetting
                {
                    Name = "workspaceIdentity",
                    Value = configuration.WorkspaceIdentity
                }
            }
            .OrderBy(static setting => setting.Name, StringComparer.Ordinal)
            .ToArray(),
            SecretsExcluded = true
        };
        var extensions = new List<RimContextExtension>
        {
            Extension(
                Id,
                "index",
                new
                {
                    available = File.Exists(configuration.StorePath),
                    configurationFingerprint = configuration.ConfigurationFingerprint,
                    store = configuration.StoreDisplayPath()
                })
        };
        var snapshot = new RimContextProviderSnapshot(
            Id,
            now,
            Topology: Available(topology, Id, now),
            Environment: Available(environment, Id, now),
            Repository: Unknown<RimContextRepositoryState>(
                "GIT_PROVIDER_NOT_CONFIGURED",
                "Repository facts are owned by Git and were not supplied by this static provider.",
                "git"),
            Deployment: Unknown<RimContextDeploymentState>(
                "DEVBRIDGE_PROVIDER_NOT_CONFIGURED",
                "Build and deployment facts are owned by DevBridge2.",
                "devbridge2"),
            Runtime: Unknown<RimContextRuntimeState>(
                "RUNTIME_PROVIDER_NOT_CONFIGURED",
                "Runtime facts are owned by DevBridge2 and RimBridgeServer.",
                "devbridge2/rimbridgeserver"),
            Testing: Unknown<RimContextTestingState>(
                "TEST_PROVIDER_NOT_CONFIGURED",
                "Test selection and evidence facts are owned by RimTest/RimLiaison.",
                "rimtest/rimliaison"),
            Efficiency: Unknown<RimContextEfficiencyMetrics>(
                "EFFICIENCY_PROVIDER_NOT_CONFIGURED",
                "Execution metrics were not supplied by the orchestration provider.",
                "rimliaison"),
            Extensions: extensions);
        return ValueTask.FromResult(snapshot);
    }

    private static RimContextSection<T> Available<T>(T value, string provider, DateTimeOffset observedAtUtc) => new()
    {
        Status = RimContextBundleStatuses.Available,
        Value = value,
        Provider = provider,
        ObservedAtUtc = observedAtUtc
    };

    private static RimContextSection<T> Unknown<T>(
        string reasonCode,
        string message,
        string provider) => new()
        {
            Status = RimContextBundleStatuses.Unknown,
            Provider = provider,
            ReasonCode = reasonCode,
            Message = message
        };

    private static RimContextExtension Extension(string provider, string key, object value) => new()
    {
        Provider = provider,
        Key = key,
        Value = JsonSerializer.SerializeToElement(value)
    };
}
