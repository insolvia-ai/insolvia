namespace Insolvia.Core.Ports;

/// <summary>
/// One transaction around writes a route makes through SEVERAL stores — what
/// a single store's own transaction cannot cover (Copy case: the case, then
/// its records and sealed tax ids, which a foreign key ties to the case row).
/// A store's own transactional method called inside joins this one.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> and commits it, or rolls it back when it
    /// throws (the exception propagates). <paramref name="work"/> must be safe
    /// to run again from the start: do its reads inside it, not before.
    /// </summary>
    /// <remarks>
    /// A store's own <c>landed</c> check does not run inside this unit — a
    /// joined call is not retried on its own — so a unit whose replay would
    /// meet its own rows (a lost commit acknowledgement) passes
    /// <paramref name="landed"/>: whether THIS call's write is there.
    /// </remarks>
    /// <param name="work">The writes; may run more than once.</param>
    /// <param name="landed">After a transient failure, whether the failed attempt had in fact committed.</param>
    /// <param name="cancellationToken">Cancels the unit.</param>
    /// <returns>A task that completes when the unit has committed.</returns>
    Task RunAsync(Func<CancellationToken, Task> work, Func<CancellationToken, Task<bool>>? landed = null, CancellationToken cancellationToken = default);
}
