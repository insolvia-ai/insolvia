using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime.Credentials;
using Insolvia.Aws.Signing;
using Insolvia.Core.Ports;

namespace Insolvia.Aws.Filing;

/// <summary>
/// The environment kill switch as an SSM parameter (Python's
/// <c>SsmKillSwitch</c>; ADR 0024: "one flag the maintainer can flip —
/// refuses every submission in that environment without a deploy"):
/// <c>/insolvia/&lt;env&gt;/filing/submissions-enabled</c>, <c>"true"</c> or
/// anything else.
/// </summary>
/// <remarks>
/// Read FRESH on every call — at the start of a run and again immediately
/// before the final submit — never cached. FAILS CLOSED: a missing
/// parameter, any value but <c>true</c>, or an error reading it all mean
/// OFF. One SigV4-signed <c>AmazonSSM.GetParameter</c> call (the JSON 1.1
/// protocol) with the signer the mailer client already uses, so the worker's
/// image carries no SSM SDK for one read.
/// </remarks>
public sealed class SsmKillSwitch : IKillSwitch
{
    private readonly HttpClient _http;
    private readonly string _parameter;
    private readonly Uri _endpoint;
    private readonly string _region;
    private readonly Func<CancellationToken, Task<AwsCredentials>> _credentials;
    private readonly TimeProvider _clock;

    /// <summary>Creates the switch.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="parameterName">The parameter's name.</param>
    /// <param name="region">The region.</param>
    /// <param name="credentials">Resolves the signing credentials.</param>
    /// <param name="endpoint">The SSM endpoint; the region's public one when null.</param>
    /// <param name="clock">The signing clock.</param>
    public SsmKillSwitch(HttpClient http, string parameterName, string region, Func<CancellationToken, Task<AwsCredentials>> credentials, Uri? endpoint = null, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _parameter = string.IsNullOrEmpty(parameterName) ? throw new ArgumentException("a parameter name is required", nameof(parameterName)) : parameterName;
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _endpoint = endpoint ?? new Uri($"https://ssm.{region}.amazonaws.com/");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The parameter read.</summary>
    public string ParameterName => _parameter;

    /// <summary>The switch on the default credential chain and <c>AWS_REGION</c>.</summary>
    public static SsmKillSwitch FromEnvironment(HttpClient http, string parameterName, Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        var region = (readVariable("AWS_REGION") is { Length: > 0 } r ? r : null)
            ?? (readVariable("AWS_DEFAULT_REGION") is { Length: > 0 } d ? d : null)
            ?? "us-east-1";
        return new SsmKillSwitch(http, parameterName, region, DefaultChainAsync);
    }

    /// <inheritdoc />
    public async Task<bool> SubmissionsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(new JsonObject { ["Name"] = _parameter }.ToJsonString());
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.1");
            request.Headers.TryAddWithoutValidation("X-Amz-Target", "AmazonSSM.GetParameter");
            SigV4.Sign(request, body, await _credentials(cancellationToken).ConfigureAwait(false), _region, "ssm", _clock.GetUtcNow());
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var value = answer?["Parameter"]?["Value"] is JsonValue text && text.TryGetValue<string>(out var read) ? read : string.Empty;
            return string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when ((error is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            && !cancellationToken.IsCancellationRequested)
        {
            // Unreadable (a timeout included): OFF. Never a cached "on". The
            // caller's own cancellation is not a reading, so it propagates.
            return false;
        }
    }

    private static async Task<AwsCredentials> DefaultChainAsync(CancellationToken cancellationToken)
    {
        var resolved = await DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentialsAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("no AWS credentials available to read the kill switch");
        return new AwsCredentials(resolved.AccessKey, resolved.SecretKey, resolved.UseToken ? resolved.Token : null);
    }
}
