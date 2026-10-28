using Insolvia.Core.Filings;

namespace Insolvia.Core.Ports;

/// <summary>
/// Hands an approved filing to the filing worker (ADR 0024: its own queue).
/// One method, and its argument is the APPROVAL, not a message: the body is
/// <see cref="FilingJobs.Message"/>'s, so no caller can put anything else on
/// the queue. The approve rule is its one caller.
/// </summary>
public interface IFilingJobSender
{
    /// <summary>Send the approval's one job.</summary>
    Task EnqueueAsync(FilingApproval approval, CancellationToken cancellationToken = default);
}
