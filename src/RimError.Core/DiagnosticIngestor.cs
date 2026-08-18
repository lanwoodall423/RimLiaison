using System.Diagnostics;
using System.Text;

namespace RimError.Core;

public sealed class DiagnosticIngestor
{
    public ValueTask<DiagnosticIngestionResult> IngestAsync(
        TextReader reader,
        string source = "stdin",
        DiagnosticIngestionMetadata? metadata = null,
        DiagnosticIngestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return IngestAsync(
            [new DiagnosticSourceInput
            {
                Source = source,
                Reader = reader,
                Metadata = metadata
            }],
            options,
            cancellationToken);
    }

    public async ValueTask<DiagnosticIngestionResult> IngestAsync(
        IEnumerable<DiagnosticSourceInput> sources,
        DiagnosticIngestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var validatedOptions = options ?? new DiagnosticIngestionOptions();
        validatedOptions.Validate();

        var stopwatch = Stopwatch.StartNew();
        var session = new DiagnosticIngestionSession(validatedOptions);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(source);
            ValidateSource(source.Source);

            await session.ProcessAsync(
                source.Reader,
                source.Source,
                source.Metadata,
                source.InputBytes,
                cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return session.Complete(stopwatch.Elapsed);
    }

    public async ValueTask<DiagnosticIngestionResult> IngestFilesAsync(
        IEnumerable<string> paths,
        DiagnosticIngestionMetadata? metadata = null,
        DiagnosticIngestionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var validatedOptions = options ?? new DiagnosticIngestionOptions();
        validatedOptions.Validate();

        var stopwatch = Stopwatch.StartNew();
        var session = new DiagnosticIngestionSession(validatedOptions);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSource(path);

            var inputBytes = new FileInfo(path).Length;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024);

            await session.ProcessAsync(
                reader,
                path,
                metadata,
                inputBytes,
                cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return session.Complete(stopwatch.Elapsed);
    }

    private static void ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("A diagnostic source label is required.", nameof(source));
        }
    }
}
