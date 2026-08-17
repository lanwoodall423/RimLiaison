using RimTest.DevBridge;

namespace RimTest.RimContext;

public sealed class SystemRimContextProcessTransport : IRimContextProcessTransport
{
    private readonly SystemDevBridgeProcessTransport transport = new();

    public async Task<RimContextProcessResult> ExecuteAsync(
        RimContextProcessRequest request,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult result = await transport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    request.FileName,
                    request.WorkingDirectory,
                    request.Arguments,
                    request.Timeout,
                    request.MaxStdoutBytes,
                    request.MaxStderrBytes),
                cancellationToken)
            .ConfigureAwait(false);
        return new RimContextProcessResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.TimedOut,
            result.Cancelled,
            result.StdoutTruncated,
            result.StderrTruncated,
            result.StartError);
    }
}
