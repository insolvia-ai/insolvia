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
    Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}
