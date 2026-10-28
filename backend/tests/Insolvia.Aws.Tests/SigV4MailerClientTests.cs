using System.Net;
using System.Text.Json.Nodes;
using Insolvia.Aws.Mailer;
using Insolvia.Aws.Signing;

namespace Insolvia.Aws.Tests;

/// <summary>
/// test_mailer_client.py's SigV4 half: the URL, the signature, the contract
/// body, the non-2xx failure and the suppression call — with no network and
/// no AWS configuration (the credentials and the clock are arguments). The
/// signatures are botocore's own, computed by <c>SigV4Auth.add_auth</c> for
/// the same request, so this pins parity with the Python client, not merely
/// self-consistency.
/// </summary>
public sealed class SigV4MailerClientTests
{
    private const string BaseUrl = "https://mailer-staging.insolvia.ai";

    private static readonly AwsCredentials Temporary = new("AKIAFAKEEXAMPLE", "secretsecretsecret", "faketoken");
    private static readonly DateTimeOffset SignedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly MailerMessage Welcome = new(
        "welcome",
        "transactional",
        "ada@example.test",
        "Welcome to Insolvia",
        "<p>Hello</p>",
        "Hello",
        "https://www.insolvia.ai/unsubscribe?token=v1.abc.def");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_client_signs_and_posts_to_the_messages_url()
    {
        var (client, captured) = Make();

        await client.SendAsync(Welcome, "msg-1", Ct);

        var request = Assert.Single(captured);
        Assert.Equal("https://mailer-staging.insolvia.ai/v1/services/insolvia_api/messages", request.Url);
        Assert.Equal("POST", request.Method);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Contains("Credential=AKIAFAKEEXAMPLE", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal("faketoken", request.Headers["X-Amz-Security-Token"]);
    }

    [Fact]
    public async Task The_send_body_is_the_mailer_contract()
    {
        var (client, captured) = Make();

        await client.SendAsync(Welcome, "msg-42", Ct);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""
                {
                  "schema_version": 1,
                  "application_message_id": "msg-42",
                  "category": "welcome",
                  "message_class": "transactional",
                  "to_address": "ada@example.test",
                  "subject": "Welcome to Insolvia",
                  "html_body": "<p>Hello</p>",
                  "text_body": "Hello",
                  "attachments": [],
                  "list_unsubscribe_url": "https://www.insolvia.ai/unsubscribe?token=v1.abc.def"
                }
                """),
            JsonNode.Parse(Assert.Single(captured).Body)));
    }

    [Fact]
    public async Task A_send_without_an_unsubscribe_link_omits_the_key()
    {
        var (client, captured) = Make();

        await client.SendAsync(Welcome with { ListUnsubscribeUrl = null }, "msg-43", Ct);

        Assert.False(JsonNode.Parse(Assert.Single(captured).Body)!.AsObject().ContainsKey("list_unsubscribe_url"));
    }

    [Fact]
    public async Task A_non_2xx_answer_raises_with_its_status()
    {
        var (client, _) = Make(HttpStatusCode.Forbidden, """{"error": "Forbidden"}""");

        var error = await Assert.ThrowsAsync<MailerRequestException>(() => client.SendAsync(Welcome, "msg-1", Ct));

        Assert.Equal(403, error.Status);
        Assert.Contains("Forbidden", error.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suppression_posts_the_contract_body_to_the_suppressions_url()
    {
        var (client, captured) = Make();

        await client.SuppressAsync("ada@example.test", "unsubscribe", Ct);

        var request = Assert.Single(captured);
        Assert.Equal("https://mailer-staging.insolvia.ai/v1/services/insolvia_api/suppressions", request.Url);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal("""{"schema_version":1,"email_address":"ada@example.test","reason":"unsubscribe"}""", request.Body);
    }

    [Theory]
    [InlineData(
        true,
        "AWS4-HMAC-SHA256 Credential=AKIAFAKEEXAMPLE/20261027/us-east-1/execute-api/aws4_request, SignedHeaders=content-type;host;x-amz-date;x-amz-security-token, Signature=72de3f98d04ccd9dde42db1d9b7d5d72808e0b795a2ce6f8de4b400ae634941e")]
    [InlineData(
        false,
        "AWS4-HMAC-SHA256 Credential=AKIAFAKEEXAMPLE/20261027/us-east-1/execute-api/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=624b6236697137a53c292b609fdbcac2d750268c299bfda2acb34aaf4ae21238")]
    public async Task The_signature_is_the_one_botocore_computes_for_the_same_request(bool withToken, string expected)
    {
        var credentials = withToken ? Temporary : Temporary with { SessionToken = null };
        var (client, captured) = Make(credentials: credentials);

        await client.SuppressAsync("ada@example.test", "unsubscribe", Ct);

        var request = Assert.Single(captured);
        Assert.Equal(expected, request.Headers["Authorization"]);
        Assert.Equal("20261027T120000Z", request.Headers["X-Amz-Date"]);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_is_ignored_and_the_region_falls_back_in_python_order()
    {
        using var http = new HttpClient();
        var variables = new Dictionary<string, string?> { ["AWS_DEFAULT_REGION"] = "eu-west-1" };

        var client = SigV4MailerClient.FromEnvironment(http, BaseUrl + "/", name => variables.GetValueOrDefault(name));

        Assert.Equal(BaseUrl, client.BaseUrl);
        Assert.Equal("eu-west-1", client.Region);
        Assert.Equal("us-east-1", SigV4MailerClient.FromEnvironment(http, BaseUrl, _ => null).Region);
        Assert.Equal("ap-south-1", SigV4MailerClient.FromEnvironment(http, BaseUrl, name => name == "AWS_REGION" ? "ap-south-1" : "eu-west-1").Region);
    }

    [Fact]
    public void Credentials_never_print_their_secret()
    {
        Assert.DoesNotContain("secretsecretsecret", Temporary.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("faketoken", Temporary.ToString(), StringComparison.Ordinal);
    }

    private static (SigV4MailerClient Client, List<Captured> Captured) Make(
        HttpStatusCode status = HttpStatusCode.OK,
        string answer = "{}",
        AwsCredentials? credentials = null)
    {
        var captured = new List<Captured>();
        var http = new HttpClient(new CapturingHandler(captured, status, answer));
        var clock = new FixedClock(SignedAt);
        return (new SigV4MailerClient(http, BaseUrl, _ => Task.FromResult(credentials ?? Temporary), "us-east-1", clock), captured);
    }

    internal sealed record Captured(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingHandler(List<Captured> captured, HttpStatusCode status, string answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            captured.Add(new Captured(request.RequestUri!.ToString(), request.Method.Method, headers, body));
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }
}
