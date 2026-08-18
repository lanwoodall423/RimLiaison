using System.Text.Json;

namespace RimError.Core;

public sealed class JsonFileBaselineStore
{
    public JsonFileBaselineStore(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException(
                "A baseline directory path is required.",
                nameof(directoryPath));
        }

        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public async ValueTask<DiagnosticBaseline?> ReadAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        DiagnosticBaselineNames.Validate(name);
        var path = GetPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var baseline = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!baseline.Name.Equals(name, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Baseline name does not match its file: {name}");
        }

        return baseline;
    }

    public async ValueTask<DiagnosticBaseline[]> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var files = Directory
            .EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var baselines = new List<DiagnosticBaseline>(files.Length);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            baselines.Add(await ReadFileAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return baselines
            .OrderBy(baseline => baseline.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask WriteAsync(
        DiagnosticBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Validate(baseline);
        Directory.CreateDirectory(DirectoryPath);

        var path = GetPath(baseline.Name);
        var temporaryPath = path + ".tmp";
        var orderedBaseline = baseline with
        {
            Items = baseline.Items
                .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .ToArray()
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
                    orderedBaseline,
                    DiagnosticJson.Options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private string GetPath(string name) =>
        Path.Combine(DirectoryPath, name + ".json");

    private static async ValueTask<DiagnosticBaseline> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var baseline = await JsonSerializer.DeserializeAsync<DiagnosticBaseline>(
            stream,
            DiagnosticJson.Options,
            cancellationToken).ConfigureAwait(false);
        if (baseline is null)
        {
            throw new InvalidDataException($"Baseline is empty: {path}");
        }

        Validate(baseline);
        return baseline with { Items = baseline.Items ?? [] };
    }

    private static void Validate(DiagnosticBaseline baseline)
    {
        DiagnosticBaselineNames.Validate(baseline.Name);
        if (baseline.SchemaVersion != DiagnosticBaseline.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported baseline schema version: {baseline.SchemaVersion}");
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
