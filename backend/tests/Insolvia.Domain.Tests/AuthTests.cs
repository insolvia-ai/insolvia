using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core.Auth;
using Insolvia.Core.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Core.Tests;

/// <summary>Real RS256 tokens, signed with a keypair made for the run — the Python suites' `jwt.encode`.</summary>
internal static class Tokens
{
    public const string Kid = "test-kid";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static SecurityKey PublicKey { get; } = new RsaSecurityKey(Rsa.ExportParameters(false)) { KeyId = Kid };

    private static SecurityKey PrivateKey { get; } = new RsaSecurityKey(Rsa) { KeyId = Kid };

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string Sign(JsonObject claims) =>
        Handler.CreateToken(claims.ToJsonString(), new SigningCredentials(PrivateKey, SecurityAlgorithms.RsaSha256));

    /// <summary>A token with an arbitrary header and an empty signature — for the refusals that never reach a key.</summary>
    public static string Unsigned(string headerJson, string payloadJson = "{}") =>
        Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson)) + "." + Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson)) + ".";
}

// Translated from test_cognito_auth.py: the client-allowlist profile (#261) —
// what the allowlist adds and, mostly, what it refuses.
public sealed class CognitoAllowlistAuthTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    private const string ClaudeClient = "examplemcpclaudeclient00";
    private const string InspectorClient = "examplemcpinspector00000";
    private const string AppClient = "exampleappclientid000000";
    private const string Subject = "00000000-0000-4000-8000-000000000001";

    private static readonly MultiClientAuthSettings Settings = TokenVerification.MultiClientSettingsOrThrow(Issuer, [ClaudeClient, InspectorClient]);

    internal static string MakeToken(string issuer = Issuer, string clientId = ClaudeClient, string tokenUse = "access", string? subject = Subject, int expiresIn = 3600)
    {
        var now = Tokens.Now;
        var claims = new JsonObject { ["iss"] = issuer, ["client_id"] = clientId, ["token_use"] = tokenUse, ["iat"] = now, ["exp"] = now + expiresIn };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        return Tokens.Sign(claims);
    }

    private static Task<Principal> Verify(string token) => TokenVerification.VerifyAccessTokenForClientsAsync(token, Tokens.PublicKey, Settings);

    [Theory]
    [InlineData(ClaudeClient)]
    [InlineData(InspectorClient)]
    public async Task Every_allowlisted_client_verifies(string clientId)
    {
        var principal = await Verify(MakeToken(clientId: clientId));
        Assert.Equal(Subject, principal.Subject);
        Assert.Equal(clientId, principal.ClientId);
    }

    [Fact]
    public async Task A_client_off_the_allowlist_is_refused()
    {
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => Verify(MakeToken(clientId: AppClient)));
        Assert.Equal(AuthFailureReason.InvalidClient, error.Reason);
    }

    public static TheoryData<string, AuthFailureReason> SharedChecks() => new()
    {
        { "expired", AuthFailureReason.Expired },
        { "issuer", AuthFailureReason.InvalidIssuer },
        { "id-token", AuthFailureReason.WrongTokenUse },
        { "no-subject", AuthFailureReason.InvalidClaims },
    };

    [Theory]
    [MemberData(nameof(SharedChecks))]
    public async Task The_shared_checks_hold_on_this_path(string variant, AuthFailureReason reason)
    {
        var token = variant switch
        {
            "expired" => MakeToken(expiresIn: -60),
            "issuer" => MakeToken(issuer: Issuer + "x"),
            "id-token" => MakeToken(tokenUse: "id"),
            _ => MakeToken(subject: null),
        };
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => Verify(token));
        Assert.Equal(reason, error.Reason);
    }

    public static TheoryData<string?, string?[]?> MissingConfiguration() => new()
    {
        { null, [ClaudeClient] },
        { Issuer, null },
        { Issuer, [] },
        { Issuer, [string.Empty] },
    };

    [Theory]
    [MemberData(nameof(MissingConfiguration))]
    public void Missing_configuration_fails_closed(string? issuer, string?[]? clientIds)
    {
        var error = Assert.Throws<AuthenticationException>(() => TokenVerification.MultiClientSettingsOrThrow(issuer, clientIds));
        Assert.Equal(AuthFailureReason.NotConfigured, error.Reason);
    }

    [Fact]
    public void The_issuer_is_normalised_like_the_single_client_profile() =>
        Assert.Equal(Issuer, TokenVerification.MultiClientSettingsOrThrow(Issuer + "/", [ClaudeClient]).IssuerUrl);
}

// Translated from test_google_auth.py: the staff profile (ADR 0011).
public sealed class GoogleAuthTests
{
    private const string ClientId = "000000000000-fake.apps.googleusercontent.com";
    private const string OtherClientId = "111111111111-other.apps.googleusercontent.com";
    private const string Domain = "example-workspace.test";
    private const string Subject = "100000000000000000001";

    private static readonly GoogleAuthSettings Settings = new(ClientId, Domain);

    private static JsonObject Claims()
    {
        var now = Tokens.Now;
        return new JsonObject
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = ClientId,
            ["sub"] = Subject,
            ["hd"] = Domain,
            ["email"] = "staff@example-workspace.test",
            ["email_verified"] = true,
            ["iat"] = now,
            ["exp"] = now + 3600,
        };
    }

    // An override of null REMOVES the claim — absent and null are different wire shapes.
    private static string Token(Action<JsonObject>? change = null)
    {
        var claims = Claims();
        change?.Invoke(claims);
        return Tokens.Sign(claims);
    }

    private static Task<StaffPrincipal> Verify(string token) => TokenVerification.VerifyGoogleIdTokenAsync(token, Tokens.PublicKey, Settings);

    [Fact]
    public async Task A_workspace_token_yields_the_staff_principal()
    {
        var principal = await Verify(Token());
        Assert.Equal(Subject, principal.Subject);
        Assert.Equal("staff@example-workspace.test", principal.Email);
    }

    [Fact]
    public async Task The_legacy_issuer_form_is_accepted() =>
        Assert.Equal(Subject, (await Verify(Token(c => c["iss"] = "accounts.google.com"))).Subject);

    public static TheoryData<string, AuthFailureReason> BrokenClaims() => new()
    {
        { "issuer", AuthFailureReason.InvalidIssuer },
        { "audience", AuthFailureReason.InvalidClient },
        { "wrong-hd", AuthFailureReason.InvalidClaims },
        { "no-hd", AuthFailureReason.InvalidClaims },
        { "unverified", AuthFailureReason.InvalidClaims },
        { "no-email", AuthFailureReason.InvalidClaims },
        { "expired", AuthFailureReason.Expired },
    };

    [Theory]
    [MemberData(nameof(BrokenClaims))]
    public async Task Each_broken_claim_is_refused(string variant, AuthFailureReason reason)
    {
        Action<JsonObject> change = variant switch
        {
            "issuer" => c => c["iss"] = "https://accounts.example.test",
            "audience" => c => c["aud"] = OtherClientId,
            "wrong-hd" => c => c["hd"] = "personal-gmail.test",
            "no-hd" => c => c.Remove("hd"),
            "unverified" => c => c["email_verified"] = false,
            "no-email" => c => c.Remove("email"),
            _ => c => c["exp"] = Tokens.Now - 60,
        };
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => Verify(Token(change)));
        Assert.Equal(reason, error.Reason);
    }

    [Fact]
    public async Task A_cognito_shaped_token_is_refused()
    {
        var now = Tokens.Now;
        var cognitoShaped = Tokens.Sign(new JsonObject
        {
            ["iss"] = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_fake",
            ["sub"] = "00000000-0000-4000-8000-000000000001",
            ["token_use"] = "access",
            ["client_id"] = "fakepoolclientid",
            ["iat"] = now,
            ["exp"] = now + 3600,
        });
        await Assert.ThrowsAsync<AuthenticationException>(() => Verify(cognitoShaped));
    }

    [Theory]
    [InlineData(null, Domain)]
    [InlineData(ClientId, null)]
    [InlineData(null, null)]
    public void Missing_settings_fail_closed(string? clientId, string? domain) =>
        Assert.Equal(
            AuthFailureReason.NotConfigured,
            Assert.Throws<AuthenticationException>(() => TokenVerification.GoogleSettingsOrThrow(clientId, domain)).Reason);
}

// Translated from test_recent_authentication.py: `auth_time` within the
// window, to the second, and nothing that merely LOOKS fresh.
public sealed class RecentAuthenticationTests
{
    private const long Now = 4_102_444_800; // 2100-01-01T00:00:00Z
    private const int Window = 300;
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    private const string Client = "exampleappclientid000000";

    private static readonly DateTimeOffset NowInstant = DateTimeOffset.FromUnixTimeSeconds(Now);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Window - 1)]
    [InlineData(Window)]
    [InlineData(-TokenVerification.ClockSkewSeconds)]
    public void A_sign_in_inside_the_window_is_accepted(int age)
    {
        var signedIn = Now - age;
        Assert.Equal(signedIn, TokenVerification.RequireRecentAuthentication(signedIn, NowInstant, Window));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Now - Window - 1)]
    [InlineData(Now - 86_400)]
    [InlineData(Now + TokenVerification.ClockSkewSeconds + 1)]
    public void Anything_else_requires_signing_in_again(long? authenticatedAt) =>
        Assert.Throws<ReauthenticationRequiredException>(() => TokenVerification.RequireRecentAuthentication(authenticatedAt, NowInstant, Window));

    [Fact]
    public void It_is_a_403_kind_of_refusal_not_a_401() =>
        Assert.True(typeof(ForbiddenException).IsAssignableFrom(typeof(ReauthenticationRequiredException)));

    public static TheoryData<string, long?> AuthTimes() => new()
    {
        { (Now - 120).ToString(System.Globalization.CultureInfo.InvariantCulture), Now - 120 },
        { "absent", null },
        { "true", null },
        { "\"1700000000\"", null },
        { "0", null },
    };

    [Theory]
    [MemberData(nameof(AuthTimes))]
    public async Task The_principal_carries_auth_time_only_when_it_is_a_time(string claim, long? expected)
    {
        var issued = Tokens.Now;
        var claims = new JsonObject
        {
            ["iss"] = Issuer,
            ["client_id"] = Client,
            ["token_use"] = "access",
            ["sub"] = "00000000-0000-4000-8000-000000000001",
            ["iat"] = issued,
            ["exp"] = issued + 3600,
        };
        if (claim != "absent")
        {
            claims["auth_time"] = JsonNode.Parse(claim);
        }

        var token = Tokens.Sign(claims);
        var single = await TokenVerification.VerifyAccessTokenAsync(token, Tokens.PublicKey, TokenVerification.SettingsOrThrow(Issuer, Client));
        var multi = await TokenVerification.VerifyAccessTokenForClientsAsync(token, Tokens.PublicKey, TokenVerification.MultiClientSettingsOrThrow(Issuer, [Client]));
        Assert.Equal(expected, single.AuthenticatedAt);
        Assert.Equal(expected, multi.AuthenticatedAt);
    }

    [Fact]
    public void A_window_of_zero_is_a_programming_error() =>
        Assert.Contains("must be positive", Assert.Throws<ArgumentOutOfRangeException>(() => TokenVerification.RequireRecentAuthentication(Now, NowInstant, 0)).Message, StringComparison.Ordinal);
}

// Translated from test_token_clock_skew.py: a token minted a moment ahead of
// this clock is accepted, one minted well ahead is not, and expiry is
// extended by the leeway and not a second more — on all three profiles.
public sealed class TokenClockSkewTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    private const string Client = "exampleappclientid000000";
    private const string Subject = "00000000-0000-4000-8000-000000000001";
    private const string GoogleClient = "000000000000-fake.apps.googleusercontent.com";
    private const string GoogleDomain = "example-workspace.test";
    private const string GoogleSubject = "100000000000000000001";

    public static TheoryData<string> Verifiers() => ["single-client", "allowlist"];

    private static async Task<string> VerifyAsync(string verifier, string token) => verifier == "single-client"
        ? (await TokenVerification.VerifyAccessTokenAsync(token, Tokens.PublicKey, TokenVerification.SettingsOrThrow(Issuer, Client))).Subject
        : (await TokenVerification.VerifyAccessTokenForClientsAsync(token, Tokens.PublicKey, TokenVerification.MultiClientSettingsOrThrow(Issuer, [Client]))).Subject;

    private static string MakeToken(int issuedIn = 0, int expiresIn = 3600, int? notBeforeIn = null)
    {
        var now = Tokens.Now;
        var claims = new JsonObject { ["iss"] = Issuer, ["client_id"] = Client, ["token_use"] = "access", ["sub"] = Subject, ["iat"] = now + issuedIn, ["exp"] = now + expiresIn };
        if (notBeforeIn is { } nbf)
        {
            claims["nbf"] = now + nbf;
        }

        return Tokens.Sign(claims);
    }

    private static string MakeGoogleToken(int issuedIn = 0, int expiresIn = 3600, int? notBeforeIn = null)
    {
        var now = Tokens.Now;
        var claims = new JsonObject
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = GoogleClient,
            ["sub"] = GoogleSubject,
            ["hd"] = GoogleDomain,
            ["email"] = "staff@example-workspace.test",
            ["email_verified"] = true,
            ["iat"] = now + issuedIn,
            ["exp"] = now + expiresIn,
        };
        if (notBeforeIn is { } nbf)
        {
            claims["nbf"] = now + nbf;
        }

        return Tokens.Sign(claims);
    }

    private static Task<StaffPrincipal> Google(string token) =>
        TokenVerification.VerifyGoogleIdTokenAsync(token, Tokens.PublicKey, new GoogleAuthSettings(GoogleClient, GoogleDomain));

    [Fact]
    public void The_leeway_is_a_minute() => Assert.Equal(60, TokenVerification.ClockSkewSeconds);

    [Theory]
    [InlineData("single-client", false)]
    [InlineData("single-client", true)]
    [InlineData("allowlist", false)]
    [InlineData("allowlist", true)]
    public async Task A_token_minted_slightly_ahead_of_this_clock_is_accepted(string verifier, bool withNotBefore) =>
        Assert.Equal(Subject, await VerifyAsync(verifier, MakeToken(issuedIn: 30, notBeforeIn: withNotBefore ? 30 : null)));

    [Theory]
    [InlineData("single-client", "iat")]
    [InlineData("single-client", "nbf")]
    [InlineData("allowlist", "iat")]
    [InlineData("allowlist", "nbf")]
    public async Task A_token_from_well_in_the_future_is_refused(string verifier, string claim) =>
        await Assert.ThrowsAsync<AuthenticationException>(() => VerifyAsync(verifier, claim == "iat" ? MakeToken(issuedIn: 300) : MakeToken(notBeforeIn: 300)));

    [Theory]
    [InlineData("single-client", -(TokenVerification.ClockSkewSeconds + 30))]
    [InlineData("single-client", -3600)]
    [InlineData("allowlist", -(TokenVerification.ClockSkewSeconds + 30))]
    [InlineData("allowlist", -3600)]
    public async Task An_expired_token_is_still_refused(string verifier, int expiresIn)
    {
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => VerifyAsync(verifier, MakeToken(issuedIn: -7200, expiresIn: expiresIn)));
        Assert.Equal(AuthFailureReason.Expired, error.Reason);
    }

    [Theory]
    [MemberData(nameof(Verifiers))]
    public async Task Expiry_is_extended_by_the_leeway_and_no_more(string verifier) =>
        Assert.Equal(Subject, await VerifyAsync(verifier, MakeToken(issuedIn: -3600, expiresIn: -(TokenVerification.ClockSkewSeconds - 30))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_google_token_minted_slightly_ahead_of_this_clock_is_accepted(bool withNotBefore) =>
        Assert.Equal(GoogleSubject, (await Google(MakeGoogleToken(issuedIn: 30, notBeforeIn: withNotBefore ? 30 : null))).Subject);

    [Theory]
    [InlineData("iat")]
    [InlineData("nbf")]
    public async Task A_google_token_from_well_in_the_future_is_refused(string claim) =>
        await Assert.ThrowsAsync<AuthenticationException>(() => Google(claim == "iat" ? MakeGoogleToken(issuedIn: 300) : MakeGoogleToken(notBeforeIn: 300)));

    [Theory]
    [InlineData(-(TokenVerification.ClockSkewSeconds + 30))]
    [InlineData(-3600)]
    public async Task An_expired_google_token_is_still_refused(int expiresIn)
    {
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => Google(MakeGoogleToken(issuedIn: -7200, expiresIn: expiresIn)));
        Assert.Equal(AuthFailureReason.Expired, error.Reason);
    }

    [Fact]
    public async Task Google_expiry_is_extended_by_the_leeway_and_no_more() =>
        Assert.Equal(GoogleSubject, (await Google(MakeGoogleToken(issuedIn: -3600, expiresIn: -(TokenVerification.ClockSkewSeconds - 30)))).Subject);
}

// The single-client profile's token-level refusals. Python pinned these
// end-to-end through services/api's test_auth.py (a route suite the API port
// translates); the verifier's half is restated here, at the function, so the
// domain carries its own guarantees.
public sealed class SingleClientAuthTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    private const string Client = "exampleappclientid000000";

    private static readonly AuthSettings Settings = TokenVerification.SettingsOrThrow(Issuer, Client);

    private static string Valid(Action<JsonObject>? change = null)
    {
        var now = Tokens.Now;
        var claims = new JsonObject
        {
            ["iss"] = Issuer,
            ["client_id"] = Client,
            ["token_use"] = "access",
            ["sub"] = "00000000-0000-4000-8000-000000000001",
            ["username"] = "00000000-0000-4000-8000-0000000000ff",
            ["scope"] = "openid  profile\temail",
            ["iat"] = now,
            ["exp"] = now + 3600,
        };
        change?.Invoke(claims);
        return Tokens.Sign(claims);
    }

    private static Task<Principal> Verify(string token) => TokenVerification.VerifyAccessTokenAsync(token, Tokens.PublicKey, Settings);

    [Fact]
    public async Task Valid_access_token_returns_the_claims_derived_identity()
    {
        var principal = await Verify(Valid());
        Assert.Equal("00000000-0000-4000-8000-000000000001", principal.Subject);
        Assert.Equal("00000000-0000-4000-8000-0000000000ff", principal.Username);
        Assert.Equal(Client, principal.ClientId);
    }

    [Fact]
    public async Task Multiple_scopes_split_on_whitespace() =>
        Assert.Equal(["openid", "profile", "email"], (await Verify(Valid())).Scopes);

    [Fact]
    public async Task A_token_without_a_scope_claim_is_still_valid() =>
        Assert.Empty((await Verify(Valid(c => c.Remove("scope")))).Scopes);

    [Fact]
    public async Task Wrong_client_id_is_rejected() =>
        Assert.Equal(AuthFailureReason.InvalidClient, (await Assert.ThrowsAsync<AuthenticationException>(() => Verify(Valid(c => c["client_id"] = "someone-else")))).Reason);

    [Fact]
    public async Task Tampered_signature_is_rejected()
    {
        var token = Valid();
        var tampered = token[..^4] + (token[^4] == 'A' ? "B" : "A") + token[^3..];
        Assert.Equal(AuthFailureReason.InvalidSignature, (await Assert.ThrowsAsync<AuthenticationException>(() => Verify(tampered))).Reason);
    }

    [Fact]
    public async Task Mutated_payload_is_rejected()
    {
        var parts = Valid().Split('.');
        var forged = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(Base64UrlEncoder.Decode(parts[1]).Replace("00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000002", StringComparison.Ordinal)));
        Assert.Equal(AuthFailureReason.InvalidSignature, (await Assert.ThrowsAsync<AuthenticationException>(() => Verify($"{parts[0]}.{forged}.{parts[2]}"))).Reason);
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var unsigned = Tokens.Unsigned("""{"alg": "none", "kid": "test-kid"}""", Valid().Split('.')[1]);
        Assert.Equal(AuthFailureReason.InvalidSignature, Assert.Throws<AuthenticationException>(() => TokenVerification.KeyId(unsigned)).Reason);
        Assert.Equal(AuthFailureReason.InvalidSignature, (await Assert.ThrowsAsync<AuthenticationException>(() => Verify(unsigned))).Reason);
    }

    [Fact]
    public void The_kid_is_read_from_the_header() => Assert.Equal(Tokens.Kid, TokenVerification.KeyId(Valid()));

    [Fact]
    public void Token_without_a_kid_header_is_rejected() =>
        Assert.Equal(AuthFailureReason.MalformedToken, Assert.Throws<AuthenticationException>(() => TokenVerification.KeyId(Tokens.Unsigned("""{"alg": "RS256"}"""))).Reason);

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("!!!.e30.sig")]
    public void Garbage_token_is_rejected(string garbage) =>
        Assert.Equal(AuthFailureReason.MalformedToken, Assert.Throws<AuthenticationException>(() => TokenVerification.KeyId(garbage)).Reason);

    [Fact]
    public async Task Id_token_is_rejected() =>
        Assert.Equal(AuthFailureReason.WrongTokenUse, (await Assert.ThrowsAsync<AuthenticationException>(() => Verify(Valid(c => c["token_use"] = "id")))).Reason);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_authorization_header_is_rejected(string? header) =>
        Assert.Equal(AuthFailureReason.MissingCredentials, Assert.Throws<AuthenticationException>(() => TokenVerification.BearerToken(header)).Reason);

    [Theory]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer a b")]
    [InlineData("token")]
    public void Malformed_authorization_header_is_rejected(string header) =>
        Assert.Equal(AuthFailureReason.MalformedHeader, Assert.Throws<AuthenticationException>(() => TokenVerification.BearerToken(header)).Reason);

    [Fact]
    public void A_bearer_header_in_any_case_yields_the_token() => Assert.Equal("abc.def.ghi", TokenVerification.BearerToken("bEaReR abc.def.ghi"));

    [Fact]
    public void Missing_auth_config_fails_closed()
    {
        Assert.Equal(AuthFailureReason.NotConfigured, Assert.Throws<AuthenticationException>(() => TokenVerification.SettingsOrThrow(null, Client)).Reason);
        Assert.Equal(AuthFailureReason.NotConfigured, Assert.Throws<AuthenticationException>(() => TokenVerification.SettingsOrThrow(Issuer, null)).Reason);
    }

    [Fact]
    public void Every_failure_reason_keeps_its_python_wire_value() =>
        Assert.Equal(
            ["missing_credentials", "malformed_header", "malformed_token", "unknown_key", "invalid_signature", "expired", "invalid_issuer", "invalid_client", "wrong_token_use", "invalid_claims", "not_configured"],
            Wire.Names<AuthFailureReason>());

    // ── The portal verify profile (from test_clients.py) ───────────

    [Theory]
    [InlineData(null, "staff-client")]
    [InlineData("portal-client", null)]
    [InlineData("same", "same")]
    public void The_portal_profile_refuses_to_exist_unless_disjoint_from_staff(string? portal, string? staff) =>
        Assert.Equal(
            AuthFailureReason.NotConfigured,
            Assert.Throws<AuthenticationException>(() => TokenVerification.PortalSettingsOrThrow("https://issuer.example.test", portal, staff)).Reason);

    [Fact]
    public void The_portal_profile_verifies_the_portal_client() =>
        Assert.Equal(
            new AuthSettings("https://issuer.example.test", "portal-client"),
            TokenVerification.PortalSettingsOrThrow("https://issuer.example.test/", "portal-client", "staff-client"));
}

// Translated from test_jwks_provider.py's memory-adapter half. The HTTP
// adapter's caching tests belong with that adapter (Insolvia.Aws).
public sealed class StaticJwksProviderTests
{
    [Fact]
    public async Task Static_provider_returns_a_known_key()
    {
        var provider = new StaticJwksProvider(new Dictionary<string, SecurityKey> { ["kid-1"] = Tokens.PublicKey });
        Assert.Same(Tokens.PublicKey, await provider.GetSigningKeyAsync("kid-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Static_provider_raises_unknown_key()
    {
        var provider = new StaticJwksProvider(new Dictionary<string, SecurityKey> { ["kid-1"] = Tokens.PublicKey });
        var error = await Assert.ThrowsAsync<AuthenticationException>(async () => await provider.GetSigningKeyAsync("kid-2", TestContext.Current.CancellationToken));
        Assert.Equal(AuthFailureReason.UnknownKey, error.Reason);
    }
}
