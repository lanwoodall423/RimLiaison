namespace RimTest;

internal static class Program
{
    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return CliApplication.RunAsync(
                    args,
                    Console.Out,
                    Console.Error,
                    cancellationToken: cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
