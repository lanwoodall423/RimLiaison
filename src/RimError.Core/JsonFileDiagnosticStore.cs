using System.Text.Json;

namespace RimError.Core;

public sealed class JsonFileDiagnosticStore : IDiagnosticStore
{
    public JsonFileDiagnosticStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A store file path is required.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public async ValueTask<DiagnosticStoreSnapshot?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        await using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var snapshot = await JsonSerializer.DeserializeAsync<DiagnosticStoreSnapshot>(
            stream,
            DiagnosticJson.Options,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new InvalidDataException($"Diagnostic store is empty: {FilePath}");
        }

        ValidateSchema(snapshot);
        return snapshot with { Items = snapshot.Items ?? [] };
    }

    public async ValueTask WriteAsync(
        DiagnosticStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSchema(snapshot);

        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The diagnostic store path has no directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + ".tmp";
        var orderedSnapshot = snapshot with
        {
            Items = snapshot.Items
                .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .ToArray(),
            Integration = snapshot.Integration is null
                ? null
                : DiagnosticIntegrationAdapter.Combine(snapshot.Integration)
        };

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    orderedSnapshot,
                    DiagnosticJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static void ValidateSchema(DiagnosticStoreSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != DiagnosticJson.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported diagnostic store schema version: {snapshot.SchemaVersion}");
        }

        if (snapshot.CausalAnalysis is not null &&
            snapshot.CausalAnalysis.SchemaVersion !=
            DiagnosticCausalAnalysis.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported causal analysis schema version: " +
                snapshot.CausalAnalysis.SchemaVersion);
        }

        if (snapshot.Integration is not null &&
            snapshot.Integration.SchemaVersion !=
            DiagnosticIntegrationContract.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported integration schema version: " +
                snapshot.Integration.SchemaVersion);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original persistence failure.
        }
    }
}
