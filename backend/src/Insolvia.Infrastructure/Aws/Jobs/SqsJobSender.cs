using Amazon.SQS;
using Amazon.SQS.Model;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;

namespace Insolvia.Aws.Jobs;

/// <summary>
/// The enqueue side of the pipeline (Python's <c>SqsJobQueue</c>; ADR 0018).
/// Deliberately thin: the body is <see cref="JobRules.MessageBody"/> — the
/// one owner of the wire shape, identifiers only, byte-for-byte the shape
/// the queue already carries — so this adapter cannot invent a contract of its own. The worker's
/// Lambda handler and local poller parse it with
/// <see cref="JobRules.ParseMessageBody"/>.
/// </summary>
/// <param name="sqs">The client (one per process).</param>
/// <param name="queueUrl"><c>JOB_QUEUE_URL</c>.</param>
public sealed class SqsJobSender(IAmazonSQS sqs, string queueUrl) : IJobSender
{
    private readonly IAmazonSQS _sqs = sqs ?? throw new ArgumentNullException(nameof(sqs));

    /// <summary>The queue every message goes to.</summary>
    public string QueueUrl { get; } = string.IsNullOrEmpty(queueUrl) ? throw new ArgumentException("a queue url is required", nameof(queueUrl)) : queueUrl;

    /// <summary>The request this sender sends for <paramref name="job"/> — the seam the contract test reads.</summary>
    public SendMessageRequest RequestFor(Job job) => new() { QueueUrl = QueueUrl, MessageBody = JobRules.MessageBody(job) };

    /// <inheritdoc />
    public async Task EnqueueAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _sqs.SendMessageAsync(RequestFor(job), cancellationToken).ConfigureAwait(false);
    }
}
