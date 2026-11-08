using Insolvia.Core.Filings;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// An <see cref="IFilingJobSender"/> that keeps each body it was handed — the
/// exact <see cref="FilingJobs.Message"/> an SQS send would carry — for a
/// test or a local run to deliver to the worker. <see cref="Fail"/> makes the
/// next send throw, as an unreachable queue would.
/// </summary>
public sealed class MemoryFilingJobSender : IFilingJobSender
{
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    /// <summary>Whether the next send throws.</summary>
    public bool Fail { get; set; }

    /// <summary>Every body sent, oldest first.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <inheritdoc />
    public Task EnqueueAsync(FilingApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (Fail)
        {
            throw new InvalidOperationException("the filing queue is unavailable");
        }

        lock (_gate)
        {
            _messages.Add(FilingJobs.Message(approval));
        }

        return Task.CompletedTask;
    }
}
