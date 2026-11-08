using Insolvia.Core.Jobs;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists pipeline job records (Python's <c>JobStore</c>; ADR 0018). A job is
/// reached only through its case — the route resolves the case through the
/// case store first; <c>caseId</c> is half the key, so a job id from another
/// case does not resolve. Written from BOTH sides of the pipeline: the API
/// creates and reads; the worker advances, through the conditional update.
/// </summary>
public interface IJobStore
{
    /// <summary>Store a new record; MUST throw rather than replace an existing (case, id).</summary>
    Task CreateAsync(Job job, CancellationToken cancellationToken = default);

    /// <summary>One job, or null. Strongly consistent: the worker's read races the accept's write by design.</summary>
    Task<Job?> GetAsync(string caseId, string jobId, CancellationToken cancellationToken = default);

    /// <summary>Every job of one case, in <see cref="JobRules.ListOrder"/> — ALL of them (the idempotency check reads this).</summary>
    Task<IReadOnlyList<Job>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="job"/> back only if the stored status is still
    /// <paramref name="expectedStatus"/> — the compare-and-swap every transition
    /// rides on. Null means the race was lost (or the row is gone).
    /// </summary>
    Task<Job?> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands an accepted job to the pipeline (Python's <c>JobQueue</c>; ADR 0018).
/// One method on purpose: the body is not a parameter — every implementation
/// sends <see cref="JobRules.MessageBody"/>, the one owner of the wire shape.
/// </summary>
public interface IJobSender
{
    /// <summary>Send the job's identifiers-only message.</summary>
    Task EnqueueAsync(Job job, CancellationToken cancellationToken = default);
}
