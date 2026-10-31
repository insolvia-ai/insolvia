using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The memory twin of <see cref="IUnitOfWork"/>: runs the work, with no
/// rollback — the memory stores have no foreign keys to order writes around,
/// and a test that needs all-or-none runs on Postgres.
/// </summary>
public sealed class MemoryUnitOfWork : IUnitOfWork
{
    /// <inheritdoc />
    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(cancellationToken);
    }
}
