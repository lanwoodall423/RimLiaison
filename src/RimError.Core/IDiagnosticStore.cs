namespace RimError.Core;

/// <summary>
/// Persistence boundary for the current diagnostic snapshot.
/// Implementations own file, database, or host-specific storage details.
/// </summary>
public interface IDiagnosticStore
{
    ValueTask<DiagnosticStoreSnapshot?> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        DiagnosticStoreSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
