using System.Net.Http.Headers;
using System.Text.Json;
using Amazon.Runtime.Credentials;
using Insolvia.Aws.Signing;

namespace Insolvia.Aws.Mailer;

/// <summary>
/// One transactional message, as the mailer's contract names its fields
/// (Python's <c>OutboundEmail</c>, which the API's port carries; this is the
/// adapter's own copy so Insolvia.Aws needs nothing from a host).
/// </summary>
/// <param name="Category">One of the mailer's allow-listed categories for <c>insolvia_api</c>.</param>
/// <param name="MessageClass">Always <c>transactional</c> today.</param>
/// <param name="ToAddress">The recipient.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="HtmlBody">The HTML part.</param>
/// <param name="TextBody">The plain-text part.</param>
/// <param name="ListUnsubscribeUrl">The RFC 8058 one-click link, or null for none (the key is then omitted).</param>
public sealed record MailerMessage(
    string Category,
    string MessageClass,
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody,
    string? ListUnsubscribeUrl = null);

/// <summary>The mailer rejected or failed a request (a non-2xx answer).</summary>
public sealed class MailerRequestException : Exception
{
    /// <summary>Creates the exception for a non-2xx answer.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The answer's body.</param>
    public MailerRequestException(int status, string body)
        : base($"mailer request failed: HTTP {status}: {body}")
    {
        Status = status;
        Body = body;
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public MailerRequestException(string message)
        : base(message)
    {
        Body = "";
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public MailerRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
        Body = "";
    }

    /// <summary>Creates the exception with no message.</summary>
    public MailerRequestException()
    {
        Body = "";
    }

    /// <summary>The HTTP status the mailer answered.</summary>
    public int Status { get; }

    /// <summary>The body the mailer answered.</summary>
    public string Body { get; }
}

/// <summary>
/// The SigV4-signed HTTP client for the mailer service (Python's
/// <c>SigV4MailerClient</c>, adapters/aws/mailer_client.py). The mailer's API
/// Gateway is IAM-authenticated (service <c>execute-api</c>): only the caller
/// role it allow-lists — this API's execution role — may invoke it. POSTs to
/// <c>{base}/v1/services/insolvia_api/messages</c> to send and
/// <c>…/suppressions</c> to stop sending.
/// </summary>
public sealed class SigV4MailerClient
{
    /// <summary>The service id the mailer registers this API under.</summary>
    public const string ServiceId = "insolvia_api";

    private const string SigningService = "execute-api";
    private const int SchemaVersion = 1;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<AwsCredentials>> _credentials;
    private readonly TimeProvider _clock;

    /// <summary>A client posting under <paramref name="baseUrl"/>.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseUrl">The mailer's base URL (<c>MAILER_API_URL</c>); a trailing slash is ignored.</param>
    /// <param name="credentials">Resolves the credentials to sign with, per request (they rotate).</param>
    /// <param name="region">The signing region.</param>
    /// <param name="clock">The signing clock; the system clock when null.</param>
    public SigV4MailerClient(HttpClient http, string baseUrl, Func<CancellationToken, Task<AwsCredentials>> credentials, string region, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        BaseUrl = baseUrl.TrimEnd('/');
        Region = region ?? throw new ArgumentNullException(nameof(region));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The base URL, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>The signing region.</summary>
    public string Region { get; }

    /// <summary>
    /// The deployed shape: the region from <c>AWS_REGION</c>, then
    /// <c>AWS_DEFAULT_REGION</c>, then <c>us-east-1</c> (the Python client's
    /// order); credentials from the SDK's default chain — the Lambda role's
    /// temporary credentials in AWS, the developer's profile locally.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseUrl">The mailer's base URL.</param>
    /// <param name="readVariable">Reads an environment variable; null when unset.</param>
    /// <returns>The client.</returns>
    public static SigV4MailerClient FromEnvironment(HttpClient http, string baseUrl, Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        var region = NonEmpty(readVariable("AWS_REGION")) ?? NonEmpty(readVariable("AWS_DEFAULT_REGION")) ?? "us-east-1";
        return new SigV4MailerClient(http, baseUrl, DefaultChainAsync, region);
    }

    /// <summary>Send <paramref name="message"/>; <paramref name="idempotencyKey"/> becomes <c>application_message_id</c>.</summary>
    /// <param name="message">The message.</param>
    /// <param name="idempotencyKey">A key stable across retries of one logical send.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the mailer accepted the send.</returns>
    public Task SendAsync(MailerMessage message, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PostAsync("messages", writer =>
        {
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("application_message_id", idempotencyKey);
            writer.WriteString("category", message.Category);
            writer.WriteString("message_class", message.MessageClass);
            writer.WriteString("to_address", message.ToAddress);
            writer.WriteString("subject", message.Subject);
            writer.WriteString("html_body", message.HtmlBody);
            writer.WriteString("text_body", message.TextBody);
            writer.WriteStartArray("attachments");
            writer.WriteEndArray();

            // Omitted rather than null: the mailer's model rejects unknown
            // keys and accepts a missing optional one.
            if (!string.IsNullOrEmpty(message.ListUnsubscribeUrl))
            {
                writer.WriteString("list_unsubscribe_url", message.ListUnsubscribeUrl);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Add <paramref name="address"/> to the mailer's suppression store
    /// (issue #80). Idempotent on the mailer's side. The ownership proof that
    /// justifies the call is the caller's to have checked.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="reason">Why, e.g. <c>unsubscribe</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the mailer accepted the suppression.</returns>
    public Task SuppressAsync(string address, string reason, CancellationToken cancellationToken = default) =>
        PostAsync("suppressions", writer =>
        {
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("email_address", address);
            writer.WriteString("reason", reason);
        }, cancellationToken);

    private static async Task<AwsCredentials> DefaultChainAsync(CancellationToken cancellationToken)
    {
        var resolved = await DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentialsAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("no AWS credentials available to sign the mailer request");
        return new AwsCredentials(resolved.AccessKey, resolved.SecretKey, resolved.UseToken ? resolved.Token : null);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task PostAsync(string resource, Action<Utf8JsonWriter> writeBody, CancellationToken cancellationToken)
    {
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeBody(writer);
                writer.WriteEndObject();
            }

            body = buffer.ToArray();
        }

        var credentials = await _credentials(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{BaseUrl}/v1/services/{ServiceId}/{resource}"));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        SigV4.Sign(request, body, credentials, Region, SigningService, _clock.GetUtcNow());

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new MailerRequestException((int)response.StatusCode, answer);
        }
    }
}
