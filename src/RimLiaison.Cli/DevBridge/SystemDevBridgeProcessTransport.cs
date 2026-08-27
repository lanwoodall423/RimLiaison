using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RimLiaison.Observability;
using RimLiaison.Profiling;

namespace RimLiaison.DevBridge;

public sealed class SystemDevBridgeProcessTransport : IDevBridgeProcessTransport
{
    public async Task<DevBridgeProcessResult> ExecuteAsync(
        DevBridgeProcessRequest request,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        string operation = ProfilerActivity.DevBridgeOperation(request.Arguments);
        string operationKey = request.OperationKey ?? "devbridge:" + operation;
        AgentOperationScope? observation = AgentObservabilityRuntime.BeginOperation(
            "tool",
            operation,
            DevelopmentStage.Testing,
            operationKey,
            new
            {
                toolName = "DevBridge",
                operationType = "command",
                command = AgentObservabilityData.SanitizeCommand(
                    request.FileName + " " + string.Join(' ', request.Arguments))
            });
        try
        {
            DevBridgeProcessResult result = await ProfilerActivity.ObserveAsync(
                    operation,
                    "devbridge",
                    () => ExecuteCoreAsync(request, cancellationToken),
                    (activity, value) =>
                    {
                        bool structuredFailure =
                            DevBridgeProcessResponseParser.TryParse(
                                value.Stdout,
                                out DevBridgeProcessResponse? parsed) &&
                            parsed?.RepresentsFailure(value.ExitCode) == true;
                        ProfilerActivity.SetOutcome(
                            activity,
                            value.Cancelled
                                ? "cancelled"
                                : value.TimedOut
                                    ? "timeout"
                                    : !structuredFailure &&
                                        value.ExitCode is 0 &&
                                        value.StartError is null
                                        ? "success"
                                        : "failure",
                            parsed?.ErrorCode ??
                                (value.StartError is null
                                    ? null
                                    : "DEVBRIDGE_PROCESS_START_FAILED"));
                        ProfilerActivity.SetCounts(
                            activity,
                            outputChars: (value.Stdout?.Length ?? 0) +
                                (value.Stderr?.Length ?? 0));
                    },
                    phase: "child-process",
                    scope: operation)
                .ConfigureAwait(false);

            AgentDiagnosticEvidenceReference? stdoutEvidence =
                AgentObservabilityRuntime.PersistEvidence(
                    "devbridge.process.stdout",
                    result.Stdout,
                    result.StdoutTruncated);
            AgentDiagnosticEvidenceReference? stderrEvidence =
                AgentObservabilityRuntime.PersistEvidence(
                    "devbridge.process.stderr",
                    result.Stderr,
                    result.StderrTruncated);
            DevBridgeProcessResponse? response =
                DevBridgeProcessResponseParser.TryParse(result.Stdout, out DevBridgeProcessResponse? parsed)
                    ? parsed
                    : null;
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = AgentObservabilityData.SanitizeCommand(
                    request.FileName + " " + string.Join(' ', request.Arguments),
                    4_096),
                ["workingDirectory"] = request.WorkingDirectory,
                ["resolvedExecutablePath"] = ResolveExecutablePath(request.FileName),
                ["resolvedToolRoot"] = ResolveToolRoot(request.WorkingDirectory),
                ["exitCode"] = result.ExitCode,
                ["stdoutExcerpt"] = AgentObservabilityData.BoundText(result.Stdout, 2048),
                ["stderrExcerpt"] = AgentObservabilityData.BoundText(result.Stderr, 2048),
                ["stdoutTruncated"] = result.StdoutTruncated,
                ["stderrTruncated"] = result.StderrTruncated,
                ["timedOut"] = result.TimedOut,
                ["cancelled"] = result.Cancelled,
                ["durationMs"] = Math.Max(
                    0,
                    (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds),
                ["operationKey"] = operationKey,
                ["toolName"] = "DevBridge"
            };
            if (!string.IsNullOrWhiteSpace(result.StartError))
            {
                details["startError"] = AgentObservabilityData.BoundText(
                    result.StartError,
                    2_048);
            }
            if (response is not null)
            {
                details["errorCode"] = response.ErrorCode;
                details["error"] = response.Error;
                details["nextAction"] = response.NextAction;
                details["state"] = response.State;
                details["responseSchema"] = response.SchemaVersion;
                details["protocolVersion"] = response.ProtocolVersion;
                details["buildIdentity"] = response.BuildIdentity;
                details["runtimeIdentity"] = response.RuntimeIdentity;
                details["structuredResponse"] = new
                {
                    success = response.Success,
                    healthy = response.Healthy,
                    exitCode = response.ExitCode,
                    errorCode = response.ErrorCode,
                    error = response.Error,
                    nextAction = response.NextAction,
                    state = response.State,
                    schemaVersion = response.SchemaVersion,
                    protocolVersion = response.ProtocolVersion,
                    buildIdentity = response.BuildIdentity,
                    findings = response.Findings,
                    runtimeIdentity = response.RuntimeIdentity
                };
            }
            if (stdoutEvidence is not null)
            {
                details["stdoutEvidenceId"] = stdoutEvidence.Id;
            }
            if (stderrEvidence is not null)
            {
                details["stderrEvidenceId"] = stderrEvidence.Id;
            }

            AgentEvent? lifecycleEvent;
            if (result.Cancelled)
            {
                lifecycleEvent = observation?.Fail(
                    "DevBridge command was cancelled.",
                    "RIMTEST_CANCELLED",
                    details);
            }
            else if (result.TimedOut)
            {
                lifecycleEvent = observation?.Fail(
                    "DevBridge command timed out.",
                    "DEVBRIDGE_COMMAND_TIMEOUT",
                    details,
                    timeout: true);
            }
            else if (response?.RepresentsFailure(result.ExitCode) == true ||
                result.ExitCode is > 0 ||
                result.StartError is not null)
            {
                lifecycleEvent = observation?.Fail(
                    "DevBridge command failed.",
                    response?.ErrorCode ??
                        (result.StartError is null
                            ? "DEVBRIDGE_COMMAND_FAILED"
                            : "DEVBRIDGE_PROCESS_START_FAILED"),
                    details);
            }
            else
            {
                lifecycleEvent = observation?.Complete("DevBridge command completed.", details);
            }

            return result with
            {
                Response = response,
                Evidence = new DevBridgeProcessEvidence(
                    ResolveExecutablePath(request.FileName),
                    ResolveToolRoot(request.WorkingDirectory),
                    request.WorkingDirectory,
                    operationKey,
                    stdoutEvidence?.Id,
                    stderrEvidence?.Id,
                    lifecycleEvent?.Id,
                    Math.Max(
                        0,
                        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds),
                    result.StdoutTruncated,
                    result.StderrTruncated)
            };
        }
        catch (OperationCanceledException)
        {
            observation?.Fail(
                "DevBridge command was cancelled.",
                "RIMTEST_CANCELLED");
            throw;
        }
        catch (Exception exception)
        {
            observation?.Fail(
                "DevBridge command raised an exception.",
                "DEVBRIDGE_COMMAND_EXCEPTION",
                new
                {
                    error = AgentObservabilityData.BoundText(exception.Message, 1024),
                    operationKey
                });
            throw;
        }
        finally
        {
            observation?.Dispose();
        }
    }

    private static string? ResolveExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(fileName) ||
                fileName.Contains(Path.DirectorySeparatorChar) ||
                fileName.Contains(Path.AltDirectorySeparatorChar))
            {
                return Path.GetFullPath(fileName);
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (string directory in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    foreach (string extension in new[] { "", ".exe", ".cmd", ".bat", ".com" })
                    {
                        string candidate = Path.Combine(directory, fileName + extension);
                        if (File.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
        }

        return fileName;
    }

    private static string? ResolveToolRoot(string workingDirectory)
    {
        try
        {
            return Path.GetFullPath(workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return workingDirectory;
        }
    }

    private async Task<DevBridgeProcessResult> ExecuteCoreAsync(
        DevBridgeProcessRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                Cancelled: true);
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = BuildStartInfo(request);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: exception.Message);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new DevBridgeProcessResult(
                    null,
                    string.Empty,
                    string.Empty,
                    StartError: "DevBridge process did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: exception.Message);
        }

        Task<BoundedReadResult> stdoutTask =
            ReadBoundedAsync(process.StandardOutput.BaseStream, request.MaxStdoutBytes);
        Task<BoundedReadResult> stderrTask =
            ReadBoundedAsync(process.StandardError.BaseStream, request.MaxStderrBytes);

        using CancellationTokenSource timeoutCancellation = new();
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
        timeoutCancellation.CancelAfter(request.Timeout);

        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            TryTerminateClientProcess(process);
        }

        if (!process.HasExited)
        {
            TryTerminateClientProcess(process);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (process.HasExited)
        {
        }

        BoundedReadResult stdout = await stdoutTask.ConfigureAwait(false);
        BoundedReadResult stderr = await stderrTask.ConfigureAwait(false);
        return new DevBridgeProcessResult(
            process.HasExited ? process.ExitCode : null,
            stdout.Text,
            stderr.Text,
            timedOut,
            cancelled,
            stdout.Truncated,
            stderr.Truncated);
    }

    private static ProcessStartInfo BuildStartInfo(DevBridgeProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("DevBridge command path is required.");
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.FileName = request.FileName;
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (KeyValuePair<string, string> variable in request.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(variable.Key))
                {
                    continue;
                }

                startInfo.Environment[variable.Key] = variable.Value ?? string.Empty;
            }
        }

        return startInfo;
    }

    private static async Task<BoundedReadResult> ReadBoundedAsync(
        Stream stream,
        int maximumBytes)
    {
        byte[] buffer = new byte[8192];
        using var output = new MemoryStream(Math.Min(maximumBytes, 8192));
        bool truncated = false;

        int count;
        while ((count = await stream.ReadAsync(buffer, CancellationToken.None)
                   .ConfigureAwait(false)) > 0)
        {
            int remaining = maximumBytes - checked((int)output.Length);
            if (remaining > 0)
            {
                int toWrite = Math.Min(remaining, count);
                output.Write(buffer, 0, toWrite);
                if (toWrite < count)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        return new BoundedReadResult(
            Encoding.UTF8.GetString(output.ToArray()),
            truncated);
    }

    private static void TryTerminateClientProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // This is only the adapter's external client process. Do not
                // terminate its child coordinator or anything the coordinator
                // owns; any accepted external operation remains owned by its
                // coordinator.
                process.Kill();
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed record BoundedReadResult(string Text, bool Truncated);
}
