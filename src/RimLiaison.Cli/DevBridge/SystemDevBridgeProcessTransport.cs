using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RimLiaison.Profiling;

namespace RimLiaison.DevBridge;

public sealed class SystemDevBridgeProcessTransport : IDevBridgeProcessTransport
{
    public Task<DevBridgeProcessResult> ExecuteAsync(
        DevBridgeProcessRequest request,
        CancellationToken cancellationToken)
    {
        string operation = ProfilerActivity.DevBridgeOperation(request.Arguments);
        return ProfilerActivity.ObserveAsync(
            operation,
            "devbridge",
            () => ExecuteCoreAsync(request, cancellationToken),
            (activity, result) =>
            {
                ProfilerActivity.SetOutcome(
                    activity,
                    result.Cancelled
                        ? "cancelled"
                        : result.TimedOut
                            ? "timeout"
                            : result.ExitCode is 0 && result.StartError is null
                                ? "success"
                                : "failure",
                    result.StartError is null
                        ? null
                        : "DEVBRIDGE_PROCESS_START_FAILED");
                ProfilerActivity.SetCounts(
                    activity,
                    outputChars: (result.Stdout?.Length ?? 0) +
                        (result.Stderr?.Length ?? 0));
            },
            phase: "child-process",
            scope: operation);
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
