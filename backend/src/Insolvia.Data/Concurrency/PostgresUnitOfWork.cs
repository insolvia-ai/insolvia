using Insolvia.Core.Ports;

namespace Insolvia.Data.Concurrency;

/// <summary>
/// <see cref="IUnitOfWork"/> over the request's one context: every store is
/// composed over the same <see cref="InsolviaDbContext"/>, so their writes —
/// and their own <see cref="Transactions.InTransactionAsync(Microsoft.EntityFrameworkCore.DbContext, Func{CancellationToken, Task}, CancellationToken, System.Data.IsolationLevel)"/>
/// calls, which join an open transaction — land in this one.
/// </summary>
public sealed class PostgresUnitOfWork(InsolviaDbContext db) : IUnitOfWork
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        _db.InTransactionAsync(work, cancellationToken);
}
