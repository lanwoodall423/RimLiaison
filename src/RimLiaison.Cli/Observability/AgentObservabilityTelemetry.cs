using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace RimLiaison.Observability;

public static class AgentObservabilityTelemetryNames
{
    public const string ActivitySource = "RimLiaison.AgentObservability";
    public const string Meter = "RimLiaison.AgentObservability";
}

public static class AgentObservabilityTags
{
    public const string RunId = "rimliaison.run.id";
    public const string AgentId = "rimliaison.agent.id";
    public const string LogicalAgentId = "rimliaison.logical.agent.id";
    public const string ModId = "rimliaison.mod.id";
    public const string ModName = "rimliaison.mod.name";
    public const string Stage = "rimliaison.stage";
    public const string ToolName = "rimliaison.tool.name";
    public const string OperationType = "rimliaison.operation.type";
    public const string Outcome = "rimliaison.outcome";
    public const string IssueCategory = "rimliaison.issue.category";
}

public sealed class AgentObservabilityTelemetryOptions
{
    public bool Enabled { get; init; } = true;
    public string? ExportEndpoint { get; init; }
    public int ExportTimeoutMilliseconds { get; init; } = 5_000;

    public static AgentObservabilityTelemetryOptions FromEnvironment()
    {
        bool disabled = IsTrue(Environment.GetEnvironmentVariable(
            "RIMLIAISON_OTEL_DISABLED"));
        string? endpoint = Environment.GetEnvironmentVariable("RIMLIAISON_OTEL_ENDPOINT") ??
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return new AgentObservabilityTelemetryOptions
        {
            Enabled = !disabled,
            ExportEndpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim()
        };
    }

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

public interface IAgentObservabilityTelemetry : IDisposable
{
    bool Enabled { get; }

    Activity? StartActivity(
        string name,
        ActivityContext? parentContext = null,
        IReadOnlyDictionary<string, object?>? tags = null);

    void RecordEvent(string type);

    void RecordOperation(
        string operationType,
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds);

    void RecordAgentDuration(
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds);

    void RecordIssue(AgentIssueCategory category, bool recovered);
}

/// <summary>
/// OpenTelemetry instrumentation for product-level agent activity. It always
/// keeps the local structured store independent from tracing and treats all
/// exporter/provider failures as optional evidence failures.
/// </summary>
public sealed class OpenTelemetryAgentTelemetry : IAgentObservabilityTelemetry
{
    private readonly ActivitySource source;
    private readonly Meter meter;
    private readonly TracerProvider? tracerProvider;
    private readonly MeterProvider? meterProvider;
    private readonly Histogram<double>? agentDuration;
    private readonly Histogram<double>? toolDuration;
    private readonly Counter<long>? toolFailures;
    private readonly Counter<long>? buildFailures;
    private readonly Counter<long>? testFailures;
    private readonly Counter<long>? retries;
    private readonly Counter<long>? issueCount;
    private readonly Counter<long>? recoveryCount;
    private int disposed;

    public OpenTelemetryAgentTelemetry(
        AgentObservabilityTelemetryOptions? options = null)
    {
        options ??= AgentObservabilityTelemetryOptions.FromEnvironment();
        Enabled = options.Enabled;
        source = new ActivitySource(AgentObservabilityTelemetryNames.ActivitySource);
        meter = new Meter(AgentObservabilityTelemetryNames.Meter);
        if (!Enabled)
        {
            return;
        }

        agentDuration = meter.CreateHistogram<double>(
            "rimliaison.agent.duration",
            "ms",
            "Duration of a mod agent run.");
        toolDuration = meter.CreateHistogram<double>(
            "rimliaison.tool.execution.duration",
            "ms",
            "Duration of a meaningful tool operation.");
        toolFailures = meter.CreateCounter<long>(
            "rimliaison.tool.failure.count",
            description: "Failed tool operations.");
        buildFailures = meter.CreateCounter<long>(
            "rimliaison.build.failure.count",
            description: "Build failures.");
        testFailures = meter.CreateCounter<long>(
            "rimliaison.test.failure.count",
            description: "Test failures.");
        retries = meter.CreateCounter<long>(
            "rimliaison.retry.count",
            description: "Retry operations.");
        issueCount = meter.CreateCounter<long>(
            "rimliaison.issue.count",
            description: "Detected agent issues.");
        recoveryCount = meter.CreateCounter<long>(
            "rimliaison.recovery.count",
            description: "Recovered agent issues.");

        try
        {
            TracerProviderBuilder tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(AgentObservabilityTelemetryNames.ActivitySource)
                .SetSampler(new AlwaysOnSampler());
            MeterProviderBuilder meterBuilder = Sdk.CreateMeterProviderBuilder()
                .AddMeter(AgentObservabilityTelemetryNames.Meter);
            if (TryGetEndpoint(options.ExportEndpoint, out Uri? endpoint))
            {
                tracerBuilder.AddOtlpExporter(exporter => ConfigureExporter(
                    exporter,
                    endpoint!,
                    options.ExportTimeoutMilliseconds));
                meterBuilder.AddOtlpExporter(exporter => ConfigureExporter(
                    exporter,
                    endpoint!,
                    options.ExportTimeoutMilliseconds));
            }

            tracerProvider = tracerBuilder.Build();
            meterProvider = meterBuilder.Build();
        }
        catch
        {
            // The provider is an observability enhancement. A missing package,
            // invalid endpoint, or constrained host cannot stop the agent.
            tracerProvider = null;
            meterProvider = null;
            ExportSetupFailed = true;
        }
    }

    public bool Enabled { get; }

    public bool ExportSetupFailed { get; private set; }

    public Activity? StartActivity(
        string name,
        ActivityContext? parentContext = null,
        IReadOnlyDictionary<string, object?>? tags = null)
    {
        if (!Enabled || Volatile.Read(ref disposed) != 0)
        {
            return null;
        }

        try
        {
            Activity? activity = parentContext.HasValue
                ? source.StartActivity(
                    name,
                    ActivityKind.Internal,
                    parentContext.Value)
                : source.StartActivity(name, ActivityKind.Internal);
            if (activity is null)
            {
                return null;
            }

            if (tags is not null)
            {
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag.Key))
                    {
                        activity.SetTag(tag.Key, tag.Value);
                    }
                }
            }
            return activity;
        }
        catch
        {
            return null;
        }
    }

    public void RecordEvent(string type)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        // Event counters intentionally use no ids, paths, commands, or mod
        // identifiers. The detailed product record remains in the local store.
    }

    public void RecordOperation(
        string operationType,
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            KeyValuePair<string, object?>[] tags =
            [
                new(AgentObservabilityTags.OperationType, operationType),
                new(AgentObservabilityTags.Stage, stage.ToString().ToLowerInvariant()),
                new(AgentObservabilityTags.Outcome, outcome)
            ];
            toolDuration?.Record(Math.Max(0, durationMilliseconds), tags);
            if (outcome is "failure" or "timeout")
            {
                toolFailures?.Add(1, tags);
            }
            if (operationType.Equals("build", StringComparison.OrdinalIgnoreCase) &&
                outcome is "failure" or "timeout")
            {
                buildFailures?.Add(1, tags);
            }
            if (operationType.Equals("test", StringComparison.OrdinalIgnoreCase) &&
                outcome is "failure" or "timeout")
            {
                testFailures?.Add(1, tags);
            }
            if (operationType.Equals("retry", StringComparison.OrdinalIgnoreCase))
            {
                retries?.Add(1, tags);
            }
        }
        catch
        {
        }
    }

    public void RecordIssue(AgentIssueCategory category, bool recovered)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var tags = new KeyValuePair<string, object?>[]
            {
                new(AgentObservabilityTags.IssueCategory, category.ToString().ToLowerInvariant())
            };
            issueCount?.Add(1, tags);
            if (recovered)
            {
                recoveryCount?.Add(1, tags);
            }
        }
        catch
        {
        }
    }

    public void RecordAgentDuration(
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            agentDuration?.Record(
                Math.Max(0, durationMilliseconds),
                new KeyValuePair<string, object?>[]
                {
                    new(AgentObservabilityTags.Stage, stage.ToString().ToLowerInvariant()),
                    new(AgentObservabilityTags.Outcome, outcome)
                });
        }
        catch
        {
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
            tracerProvider?.Dispose();
        }
        catch
        {
        }
        try
        {
            meterProvider?.Dispose();
        }
        catch
        {
        }
        source.Dispose();
        meter.Dispose();
    }

    private static bool TryGetEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        Uri endpoint,
        int timeoutMilliseconds)
    {
        exporter.Endpoint = endpoint;
        exporter.TimeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 250, 60_000);
    }
}

public sealed class NoopAgentObservabilityTelemetry : IAgentObservabilityTelemetry
{
    public bool Enabled => false;

    public Activity? StartActivity(
        string name,
        ActivityContext? parentContext = null,
        IReadOnlyDictionary<string, object?>? tags = null) => null;

    public void RecordEvent(string type)
    {
    }

    public void RecordOperation(
        string operationType,
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds)
    {
    }

    public void RecordIssue(AgentIssueCategory category, bool recovered)
    {
    }

    public void RecordAgentDuration(
        DevelopmentStage stage,
        string outcome,
        double durationMilliseconds)
    {
    }

    public void Dispose()
    {
    }
}
