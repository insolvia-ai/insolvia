using System.Text.Json.Nodes;
using Insolvia.Core.Auth;
using Insolvia.Core.Ports;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Aws.Jwks;

/// <summary>
/// The pool's JWKS, fetched over HTTPS and cached (issue #79; Python's
/// <c>CognitoJwksProvider</c> in insolvia_core adapters/aws/jwks_provider.py).
/// </summary>
/// <remarks>
/// <para><b>The caching rules, and the reason for each.</b></para>
/// <list type="bullet">
/// <item>The whole document is cached by <c>kid</c> for the TTL: without it every
/// authenticated request is an outbound HTTPS call and a hard dependency on
/// Cognito's availability.</item>
/// <item>An unknown <c>kid</c> triggers at most one refetch, and no more often
/// than the minimum refresh interval: rotation is picked up without a
/// redeploy, but "unknown kid" is exactly what a forged token carries, and a
/// refetch per bogus token would let an anonymous caller make this service
/// hammer Cognito.</item>
/// <item>A still-unknown <c>kid</c> throws <see cref="AuthFailureReason.UnknownKey"/> —
/// never "verify without a key".</item>
/// <item>A failed fetch never clears a good cache (a Cognito blip must not sign
/// everyone out), and it still advances the refresh clock, which is what
/// rate-limits the retry. An empty cache that cannot be filled fails closed.</item>
/// </list>
/// <para>Unlike the Python adapter, this one is shared by concurrent requests,
/// so a refresh is serialised: callers that queued behind one re-check the
/// cache instead of fetching again.</para>
/// </remarks>
public sealed class HttpJwksProvider : IJwksProvider, IDisposable
{
    /// <summary>How long the document is trusted before the happy path refetches it.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    /// <summary>The floor between refetches an unknown <c>kid</c> may cause.</summary>
    public static readonly TimeSpan DefaultMinRefresh = TimeSpan.FromSeconds(60);

    /// <summary>How long one fetch may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A JWKS is a few hundred bytes; anything near this is not one, and reading it unbounded is a memory DoS.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    private const long NeverFetched = long.MinValue;

    private readonly HttpClient _http;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _minRefresh;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private volatile Dictionary<string, SecurityKey> _keys = new(StringComparer.Ordinal);

    // The clock's timestamp of the last fetch ATTEMPT, or NeverFetched.
    private long _fetchedAt = NeverFetched;

    /// <summary>Creates the provider. Nothing is fetched until the first token arrives.</summary>
    /// <param name="http">The client the GET goes through (its handler is the test seam).</param>
    /// <param name="jwksUrl">The document's URL — see <see cref="ForIssuer"/>.</param>
    /// <param name="ttl">Defaults to <see cref="DefaultTtl"/>.</param>
    /// <param name="minRefresh">Defaults to <see cref="DefaultMinRefresh"/>.</param>
    /// <param name="timeout">Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="clock">The monotonic clock; defaults to the system's.</param>
    public HttpJwksProvider(HttpClient http, Uri jwksUrl, TimeSpan? ttl = null, TimeSpan? minRefresh = null, TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(jwksUrl);
        _http = http;
        JwksUrl = jwksUrl;
        _ttl = ttl ?? DefaultTtl;
        _minRefresh = minRefresh ?? DefaultMinRefresh;
        _timeout = timeout ?? DefaultTimeout;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Where the keys are fetched from. Built from configuration, never from request input.</summary>
    public Uri JwksUrl { get; }

    /// <summary>
    /// Cognito publishes at <c>&lt;issuer&gt;/.well-known/jwks.json</c>.
    /// Google does not (<see cref="TokenVerification.GoogleJwksUrl"/>), which is
    /// why the constructor takes the URL itself.
    /// </summary>
    /// <param name="issuerUrl">The pool's issuer.</param>
    /// <returns>The JWKS URL.</returns>
    public static Uri ForIssuer(string issuerUrl)
    {
        ArgumentNullException.ThrowIfNull(issuerUrl);
        return new Uri(issuerUrl.TrimEnd('/') + "/.well-known/jwks.json");
    }

    /// <summary>
    /// Turns a JWKS document into <c>{kid: key}</c>, skipping anything
    /// unusable — an entry with no <c>kid</c>, a key type other than RSA, a
    /// modulus or exponent that does not decode: one bad entry must not take
    /// down verification for the good ones. Throws <see cref="FormatException"/>
    /// when nothing usable remains.
    /// </summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>The keys by <c>kid</c>.</returns>
    public static Dictionary<string, SecurityKey> ParseJwks(JsonNode? document)
    {
        if (document is not JsonObject root)
        {
            throw new FormatException("JWKS document is not a JSON object");
        }

        if (root["keys"] is not JsonArray entries)
        {
            throw new FormatException("JWKS document has no `keys` array");
        }

        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject jwk
                || Text(jwk["kid"]) is not { Length: > 0 } kid
                || Text(jwk["kty"]) != "RSA"
                || Decode(Text(jwk["n"])) is not { } modulus
                || Decode(Text(jwk["e"])) is not { } exponent)
            {
                continue;
            }

            keys[kid] = new RsaSecurityKey(new System.Security.Cryptography.RSAParameters { Modulus = modulus, Exponent = exponent }) { KeyId = kid };
        }

        if (keys.Count == 0)
        {
            throw new FormatException("JWKS document contained no usable keys");
        }

        return keys;
    }

    /// <inheritdoc />
    public async ValueTask<SecurityKey> GetSigningKeyAsync(string kid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kid);
        if (IsStale())
        {
            await RefreshAsync(_ttl, cancellationToken).ConfigureAwait(false);
        }

        if (_keys.TryGetValue(kid, out var key))
        {
            return key;
        }

        // Unknown kid on a warm cache: a rotation, or a forged token. The
        // rate-limited refetch decides which.
        if (MayRefresh())
        {
            await RefreshAsync(_minRefresh, cancellationToken).ConfigureAwait(false);
            if (_keys.TryGetValue(kid, out key))
            {
                return key;
            }
        }

        throw new AuthenticationException(AuthFailureReason.UnknownKey);
    }

    /// <inheritdoc />
    public void Dispose() => _refreshing.Dispose();

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static byte[]? Decode(string? base64Url)
    {
        if (string.IsNullOrEmpty(base64Url))
        {
            return null;
        }

        try
        {
            var bytes = Base64UrlEncoder.DecodeBytes(base64Url);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private bool IsStale() => Elapsed(Interlocked.Read(ref _fetchedAt)) is not { } elapsed || elapsed >= _ttl;

    private bool MayRefresh() => Elapsed(Interlocked.Read(ref _fetchedAt)) is not { } elapsed || elapsed >= _minRefresh;

    private TimeSpan? Elapsed(long at) => at == NeverFetched ? null : _clock.GetElapsedTime(at);

    /// <summary>
    /// Replace the cache, or leave the previous one exactly as it was. The
    /// timestamp advances even on failure. <paramref name="interval"/> is the
    /// rule that let this caller in; a caller that waited behind another
    /// refresh re-checks it and stands down if that refresh satisfied it.
    /// </summary>
    private async Task RefreshAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Elapsed(Interlocked.Read(ref _fetchedAt)) is { } elapsed && elapsed < interval)
            {
                return;
            }

            Dictionary<string, SecurityKey> document;
            try
            {
                document = await FetchAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // any fetch failure is the same outcome: keep what we have
            catch (Exception)
#pragma warning restore CA1031
            {
                Interlocked.Exchange(ref _fetchedAt, _clock.GetTimestamp());
                if (_keys.Count == 0)
                {
                    // Nothing cached and nothing fetched: every token is unverifiable.
                    throw new AuthenticationException(AuthFailureReason.UnknownKey);
                }

                return;
            }

            _keys = document;
            Interlocked.Exchange(ref _fetchedAt, _clock.GetTimestamp());
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task<Dictionary<string, SecurityKey>> FetchAsync()
    {
        using var timeout = new CancellationTokenSource(_timeout, _clock);
        using var response = await _http
            .GetAsync(JwksUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxDocumentBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length
                && (read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            if (total > MaxDocumentBytes)
            {
                throw new FormatException("JWKS document is implausibly large");
            }

            return ParseJwks(JsonNode.Parse(buffer.AsSpan(0, total)));
        }
    }
}
