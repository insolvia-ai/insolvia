using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Aws.Jwks;
using Insolvia.Core.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Aws.Tests;

// Translated from packages/insolvia_core tests/unit/test_jwks_provider.py.
// Where the Python monkeypatched `_fetch`, these put a fake handler under
// the HttpClient — so the real GET, size cap and parse run too — and drive a
// fake monotonic clock instead of sleeping. What is under test is the caching
// and fail-closed policy around the fetch.
public sealed class HttpJwksProviderTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";

    private static readonly RSA KeyOne = RSA.Create(2048);
    private static readonly RSA KeyTwo = RSA.Create(2048);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Jwk(string kid, RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        return new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = kid,
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private static string Document(params JsonObject[] keys) => new JsonObject { ["keys"] = new JsonArray([.. keys]) }.ToJsonString();

    private static (HttpJwksProvider Provider, FakeHandler Handler, FakeClock Clock) Fake(
        IReadOnlyList<object> responses, TimeSpan? ttl = null, TimeSpan? minRefresh = null)
    {
        var handler = new FakeHandler(responses);
        var clock = new FakeClock();
        var provider = new HttpJwksProvider(new HttpClient(handler), HttpJwksProvider.ForIssuer(Issuer), ttl, minRefresh, clock: clock);
        return (provider, handler, clock);
    }

    private static byte[] Modulus(SecurityKey key) => ((RsaSecurityKey)key).Parameters.Modulus!;

    [Fact]
    public void Builds_the_well_known_jwks_url() =>
        Assert.Equal(new Uri(Issuer + "/.well-known/jwks.json"), HttpJwksProvider.ForIssuer(Issuer + "/"));

    [Fact]
    public async Task Fetches_once_and_serves_from_cache()
    {
        var (provider, handler, _) = Fake([Document(Jwk("kid-1", KeyOne))]);

        var first = await provider.GetSigningKeyAsync("kid-1", Ct);
        var second = await provider.GetSigningKeyAsync("kid-1", Ct);

        Assert.Equal(KeyOne.ExportParameters(false).Modulus, Modulus(first));
        Assert.Same(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpJwksProvider.ForIssuer(Issuer), handler.LastUri);
    }

    [Fact]
    public async Task Refetches_once_for_an_unknown_kid_then_serves_the_rotated_key()
    {
        var (provider, handler, _) = Fake([Document(Jwk("kid-1", KeyOne)), Document(Jwk("kid-1", KeyOne), Jwk("kid-2", KeyTwo))], minRefresh: TimeSpan.Zero);

        await provider.GetSigningKeyAsync("kid-1", Ct);
        var rotated = await provider.GetSigningKeyAsync("kid-2", Ct);

        Assert.Equal(KeyTwo.ExportParameters(false).Modulus, Modulus(rotated));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task An_unknown_kid_throws_rather_than_returning_nothing()
    {
        var (provider, _, _) = Fake([Document(Jwk("kid-1", KeyOne))], minRefresh: TimeSpan.Zero);
        await provider.GetSigningKeyAsync("kid-1", Ct);

        var error = await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("kid-absent", Ct).AsTask());

        Assert.Equal(AuthFailureReason.UnknownKey, error.Reason);
    }

    [Fact]
    public async Task Unknown_kids_do_not_refetch_faster_than_the_floor()
    {
        // A forged token carries an unknown kid; without this floor an
        // anonymous caller could make the service hammer Cognito.
        var (provider, handler, clock) = Fake([Document(Jwk("kid-1", KeyOne))], minRefresh: TimeSpan.FromHours(1));
        await provider.GetSigningKeyAsync("kid-1", Ct);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("forged-kid", Ct).AsTask());
        }

        Assert.Equal(1, handler.Calls);

        // Past the floor, one more fetch — and only one.
        clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("forged-kid", Ct).AsTask());
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("forged-kid", Ct).AsTask());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_default_floor_is_sixty_seconds()
    {
        var (provider, handler, clock) = Fake([Document(Jwk("kid-1", KeyOne))]);
        await provider.GetSigningKeyAsync("kid-1", Ct);

        clock.Advance(TimeSpan.FromSeconds(59));
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("forged-kid", Ct).AsTask());
        Assert.Equal(1, handler.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("forged-kid", Ct).AsTask());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_serving_the_cached_keys()
    {
        var (provider, _, _) = Fake([Document(Jwk("kid-1", KeyOne)), new HttpRequestException("cognito is having a day")], minRefresh: TimeSpan.Zero);
        await provider.GetSigningKeyAsync("kid-1", Ct);

        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("kid-2", Ct).AsTask());

        // The outage must not have signed everyone out.
        Assert.Equal(KeyOne.ExportParameters(false).Modulus, Modulus(await provider.GetSigningKeyAsync("kid-1", Ct)));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("status")]
    [InlineData("not-json")]
    [InlineData("no-keys")]
    [InlineData("too-large")]
    public async Task A_cold_cache_that_cannot_be_filled_fails_closed(string failure)
    {
        object response = failure switch
        {
            "network" => new HttpRequestException("no network"),
            "status" => HttpStatusCode.ServiceUnavailable,
            "not-json" => "<html>",
            "no-keys" => """{"keys":[]}""",
            _ => """{"keys":[],"pad":" """ + new string('x', HttpJwksProvider.MaxDocumentBytes) + "\"}",
        };
        var (provider, _, _) = Fake([response]);

        var error = await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("kid-1", Ct).AsTask());

        Assert.Equal(AuthFailureReason.UnknownKey, error.Reason);
    }

    [Fact]
    public async Task A_failed_fetch_still_advances_the_refresh_clock()
    {
        // The timestamp moves on failure too: an outage degrades to "keep the
        // keys we have", not "hammer Cognito on every request".
        var (provider, handler, clock) = Fake([Document(Jwk("kid-1", KeyOne)), new HttpRequestException("down")], ttl: TimeSpan.FromHours(1), minRefresh: TimeSpan.FromSeconds(60));
        await provider.GetSigningKeyAsync("kid-1", Ct);
        clock.Advance(TimeSpan.FromSeconds(60));

        // The refetch an unknown kid earns fails...
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("kid-2", Ct).AsTask());
        Assert.Equal(2, handler.Calls);

        // ...and the failure restarted the floor: no retry inside it.
        await Assert.ThrowsAsync<AuthenticationException>(() => provider.GetSigningKeyAsync("kid-2", Ct).AsTask());
        Assert.Equal(2, handler.Calls);
        Assert.NotNull(await provider.GetSigningKeyAsync("kid-1", Ct));
    }

    [Fact]
    public async Task A_stale_cache_is_refreshed_on_the_happy_path()
    {
        var (provider, handler, _) = Fake([Document(Jwk("kid-1", KeyOne)), Document(Jwk("kid-1", KeyTwo))], ttl: TimeSpan.Zero);

        Assert.Equal(KeyOne.ExportParameters(false).Modulus, Modulus(await provider.GetSigningKeyAsync("kid-1", Ct)));
        Assert.Equal(KeyTwo.ExportParameters(false).Modulus, Modulus(await provider.GetSigningKeyAsync("kid-1", Ct)));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Concurrent_cold_requests_share_one_fetch()
    {
        var (provider, handler, _) = Fake([Document(Jwk("kid-1", KeyOne))]);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => provider.GetSigningKeyAsync("kid-1", Ct).AsTask()));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_fetched_key_verifies_a_token_it_signed()
    {
        var (provider, _, _) = Fake([Document(Jwk("kid-1", KeyOne))]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JsonObject { ["iss"] = Issuer, ["client_id"] = "exampleappclientid000000", ["token_use"] = "access", ["sub"] = "00000000-0000-4000-8000-000000000001", ["iat"] = now, ["exp"] = now + 60 };
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }
            .CreateToken(claims.ToJsonString(), new SigningCredentials(new RsaSecurityKey(KeyOne) { KeyId = "kid-1" }, SecurityAlgorithms.RsaSha256));

        var key = await provider.GetSigningKeyAsync(TokenVerification.KeyId(token), Ct);
        var principal = await TokenVerification.VerifyAccessTokenAsync(token, key, TokenVerification.SettingsOrThrow(Issuer, "exampleappclientid000000"));

        Assert.Equal("00000000-0000-4000-8000-000000000001", principal.Subject);
    }

    // ── parsing a real-shaped JWKS document ──────────────────────────

    [Fact]
    public void Parse_jwks_keys_by_kid() =>
        Assert.Equal(["test-kid"], HttpJwksProvider.ParseJwks(JsonNode.Parse(Document(Jwk("test-kid", KeyOne)))).Keys);

    [Fact]
    public void Parse_jwks_skips_unusable_entries_but_keeps_the_good_ones()
    {
        var document = new JsonObject
        {
            ["keys"] = new JsonArray(
                "not-an-object",
                new JsonObject { ["kty"] = "RSA", ["n"] = "abc", ["e"] = "AQAB" }, // no kid
                new JsonObject { ["kty"] = "NONSENSE", ["kid"] = "bad-kid" },
                new JsonObject { ["kty"] = "RSA", ["kid"] = "no-modulus", ["e"] = "AQAB" },
                Jwk("test-kid", KeyOne)),
        };

        Assert.Equal(["test-kid"], HttpJwksProvider.ParseJwks(document).Keys);
    }

    [Theory]
    [InlineData("\"not-an-object\"")]
    [InlineData("""{"no_keys":[]}""")]
    [InlineData("""{"keys":"nope"}""")]
    [InlineData("""{"keys":[]}""")]
    public void Parse_jwks_rejects_a_document_with_no_usable_keys(string document)
    {
        var error = Assert.Throws<FormatException>(() => HttpJwksProvider.ParseJwks(JsonNode.Parse(document)));

        Assert.Contains("JWKS document", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers each GET with the next scripted response (the last one repeats), counting calls.</summary>
    private sealed class FakeHandler(IReadOnlyList<object> responses) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            LastUri = request.RequestUri;
            var call = Interlocked.Increment(ref _calls);

            // Let concurrent callers pile up behind the first fetch.
            await Task.Yield();
            return responses[Math.Min(call - 1, responses.Count - 1)] switch
            {
                Exception error => throw error,
                HttpStatusCode status => new HttpResponseMessage(status),
                string body => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
                var other => throw new InvalidOperationException("unscripted response " + other),
            };
        }
    }

    /// <summary>A monotonic clock that moves only when told to.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
