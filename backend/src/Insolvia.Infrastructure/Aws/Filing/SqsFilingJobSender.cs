using Amazon.SQS;
using Amazon.SQS.Model;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;

namespace Insolvia.Aws.Filing;

/// <summary>
/// The <see cref="IFilingJobSender"/> on the filing worker's own SQS queue
/// (Python's <c>SqsFilingQueue</c>; infra/modules/filing_queue). Thin on
/// purpose: the body is <see cref="FilingJobs.Message"/>'s — byte-equal to
/// Python's sorted, compact <c>json.dumps</c> — so this adapter cannot invent
/// a contract. THE ONE send to that queue; the API role's grant on it is
/// SendMessage alone.
/// </summary>
public sealed class SqsFilingJobSender(IAmazonSQS sqs, string queueUrl) : IFilingJobSender
{
    private readonly IAmazonSQS _sqs = sqs ?? throw new ArgumentNullException(nameof(sqs));

    /// <summary>The queue.</summary>
    public string QueueUrl { get; } = string.IsNullOrEmpty(queueUrl) ? throw new ArgumentException("a queue url is required", nameof(queueUrl)) : queueUrl;

    /// <inheritdoc />
    public Task EnqueueAsync(FilingApproval approval, CancellationToken cancellationToken = default) =>
        _sqs.SendMessageAsync(new SendMessageRequest(QueueUrl, FilingJobs.Message(approval)), cancellationToken);
}
