using System.Reflection;
using Amazon.SQS;
using Amazon.SQS.Model;
using Insolvia.Aws.Jobs;
using Insolvia.Core.Jobs;

namespace Insolvia.Aws.Tests;

/// <summary>
/// The enqueue adapter (Python's SqsJobQueue): it sends exactly
/// <see cref="JobRules.MessageBody"/> to the configured queue — the pinned,
/// identifiers-only, byte-exact body — and nothing else.
/// </summary>
public sealed class SqsJobSenderTests
{
    private const string QueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/insolvia-local-jobs";

    [Fact]
    public async Task The_message_is_the_pinned_body_on_the_configured_queue()
    {
        var sqs = DispatchProxy.Create<IAmazonSQS, RecordingSqs>();
        var job = JobRules.NewJob(JobRules.PacketAssemblyKind, "00000000-0000-4000-8000-00000000ca5e", "subject-1");

        await new SqsJobSender(sqs, QueueUrl).EnqueueAsync(job, TestContext.Current.CancellationToken);

        var sent = Assert.Single(((RecordingSqs)(object)sqs).Sent);
        Assert.Equal(QueueUrl, sent.QueueUrl);
        Assert.Equal($$"""{"version":1,"jobId":"{{job.Id}}","caseId":"00000000-0000-4000-8000-00000000ca5e","kind":"packet_assembly"}""", sent.MessageBody);
        Assert.True(sent.MessageAttributes is null || sent.MessageAttributes.Count == 0);
    }

    [Fact]
    public void A_queue_url_is_required() =>
        Assert.Throws<ArgumentException>(() => new SqsJobSender(DispatchProxy.Create<IAmazonSQS, RecordingSqs>(), ""));

    /// <summary>An <see cref="IAmazonSQS"/> that records SendMessage and refuses everything else.</summary>
    public class RecordingSqs : DispatchProxy
    {
        public List<SendMessageRequest> Sent { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAmazonSQS.SendMessageAsync) && args?[0] is SendMessageRequest request)
            {
                Sent.Add(request);
                return Task.FromResult(new SendMessageResponse { MessageId = "m-1" });
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
