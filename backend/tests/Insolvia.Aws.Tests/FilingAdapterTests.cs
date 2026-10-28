using System.Net;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Insolvia.Aws.Filing;
using Insolvia.Aws.Signing;
using Insolvia.Core.Filings;

namespace Insolvia.Aws.Tests;

// test_aws_adapters.py (services/filing) and filing_queue.py's contract: the
// kill switch is on for "true" alone, fails closed, and is read every time;
// the queue send carries exactly the job message.
public sealed class FilingAdapterTests
{
    private static readonly AwsCredentials Keys = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");

    public static TheoryData<HttpStatusCode, string, bool> Answers => new()
    {
        { HttpStatusCode.OK, """{"Parameter":{"Name":"p","Value":"true"}}""", true },
        { HttpStatusCode.OK, """{"Parameter":{"Name":"p","Value":" TRUE "}}""", true },
        { HttpStatusCode.OK, """{"Parameter":{"Name":"p","Value":"false"}}""", false },
        { HttpStatusCode.OK, """{"Parameter":{"Name":"p","Value":"yes"}}""", false },
        { HttpStatusCode.OK, """{}""", false },
        { HttpStatusCode.OK, "not json", false },
        { HttpStatusCode.BadRequest, """{"__type":"ParameterNotFound"}""", false },
        { HttpStatusCode.InternalServerError, "", false },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task The_kill_switch_is_on_only_for_true_and_fails_closed(HttpStatusCode status, string answer, bool enabled)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent(answer) });
        var killSwitch = new SsmKillSwitch(new HttpClient(handler), "/insolvia/staging/filing/submissions-enabled", "us-east-1", _ => Task.FromResult(Keys));

        Assert.Equal(enabled, await killSwitch.SubmissionsEnabledAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unreachable_parameter_store_is_off()
    {
        var handler = new Handler(_ => throw new HttpRequestException("refused"));
        var killSwitch = new SsmKillSwitch(new HttpClient(handler), "p", "us-east-1", _ => Task.FromResult(Keys));

        Assert.False(await killSwitch.SubmissionsEnabledAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_kill_switch_is_read_fresh_every_time_and_signed_as_ssm_get_parameter()
    {
        var value = "true";
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Parameter\":{\"Value\":\"" + value + "\"}}") });
        var killSwitch = new SsmKillSwitch(new HttpClient(handler), "/insolvia/prod/filing/submissions-enabled", "us-east-1", _ => Task.FromResult(Keys));

        Assert.True(await killSwitch.SubmissionsEnabledAsync(TestContext.Current.CancellationToken));
        value = "false";
        Assert.False(await killSwitch.SubmissionsEnabledAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Seen.Count);
        var (request, body) = handler.Seen[0];
        Assert.Equal("https://ssm.us-east-1.amazonaws.com/", request.RequestUri!.ToString());
        Assert.Equal("AmazonSSM.GetParameter", request.Headers.GetValues("X-Amz-Target").Single());
        Assert.Contains("/ssm/aws4_request", string.Join(" ", request.Headers.GetValues("Authorization")), StringComparison.Ordinal);
        Assert.Equal("""{"Name":"/insolvia/prod/filing/submissions-enabled"}""", body);
    }

    [Fact]
    public async Task The_queue_send_carries_the_job_message_and_nothing_else()
    {
        var sqs = new RecordingSqs();
        var sender = new SqsFilingJobSender(sqs.Client, "https://sqs.us-east-1.amazonaws.com/000000000000/insolvia-staging-filing");
        var approval = new FilingApproval(
            "00000000-0000-4000-8000-0000000000a1", "00000000-0000-4000-8000-0000000000f1", "00000000-0000-4000-8000-0000000000c1",
            "firm", "attorney", "credential", "authorization", new string('a', 64), "packet", "flmb", "8", "r", DateTimeOffset.UnixEpoch, 0, 1);

        await sender.EnqueueAsync(approval, TestContext.Current.CancellationToken);

        var sent = Assert.Single(sqs.Sent);
        Assert.Equal(sender.QueueUrl, sent.QueueUrl);
        Assert.Equal(
            """{"approval_id":"00000000-0000-4000-8000-0000000000a1","case_id":"00000000-0000-4000-8000-0000000000c1","filing_id":"00000000-0000-4000-8000-0000000000f1","kind":"filing.submit","version":1}""",
            sent.MessageBody);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken))));
            return respond(request);
        }
    }

    /// <summary>An SQS client whose transport records the request instead of sending it.</summary>
    private sealed class RecordingSqs
    {
        public RecordingSqs()
        {
            Client = new SqsStub(this);
        }

        public List<SendMessageRequest> Sent { get; } = [];

        public IAmazonSQS Client { get; }

        private sealed class SqsStub(RecordingSqs owner) : AmazonSQSClient(new Amazon.Runtime.BasicAWSCredentials("AKIDEXAMPLE", "secret"), Amazon.RegionEndpoint.USEast1)
        {
            public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
            {
                owner.Sent.Add(request);
                return Task.FromResult(new SendMessageResponse { MessageId = "m-1" });
            }
        }
    }
}
