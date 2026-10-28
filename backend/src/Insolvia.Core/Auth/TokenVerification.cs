using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Core.Auth;

/// <summary>
/// Cognito access-token and Google ID-token verification (ported from
/// <c>auth.py</c>, issues #79, #209, #261). Pure — no framework, no AWS, no
/// network: the signing key arrives as an argument, and finding it is the
/// <see cref="Ports.IJwksProvider"/> adapter's job.
/// </summary>
/// <remarks>
/// <para>Cognito ACCESS tokens carry <c>client_id</c> and no <c>aud</c>, so
/// the app-client check is against <c>client_id</c>. Every check is
/// load-bearing: RS256 only (refusing <c>none</c> and HS256-with-the-public-key
/// before any key lookup), <c>iss</c>, <c>token_use == "access"</c> (an ID
/// token from the same pool is signed by the same key), <c>client_id</c>,
/// <c>exp</c>/<c>nbf</c>/<c>iat</c> with <see cref="ClockSkewSeconds"/> of
/// leeway, and a non-empty <c>sub</c>.</para>
/// <para>The Google profile (ADR 0011) verifies the ID token instead —
/// Google's access tokens are opaque — with <c>aud</c> checked, either of
/// Google's two issuer spellings, the Workspace <c>hd</c>, and a verified email.</para>
/// </remarks>
public static class TokenVerification
{
    /// <summary>
    /// Clock skew tolerated between an issuer's clock and this verifier's, in
    /// seconds — on <c>iat</c>/<c>nbf</c> (ahead) and <c>exp</c> (behind),
    /// and on <c>auth_time</c> ahead. One constant so the checks cannot drift.
    /// </summary>
    public const int ClockSkewSeconds = 60;

    /// <summary>The only <c>token_use</c> the API accepts.</summary>
    public const string TokenUseAccess = "access";

    /// <summary>Google's two documented issuer spellings.</summary>
    public static readonly IReadOnlyList<string> GoogleIssuers = ["https://accounts.google.com", "accounts.google.com"];

    // Cognito signs with RS256 and only RS256; Google likewise. Listing it is
    // what stops a downgrade to `none` or to HS256 keyed by the public key.
    internal static readonly string[] Algorithms = ["RS256"];

    private static readonly string[] CognitoRequired = ["exp", "iss", "sub", "token_use", "client_id"];
    private static readonly string[] GoogleRequired = ["exp", "iss", "sub", "aud"];

    /// <summary>
    /// Where Google publishes its keys — NOT derivable as
    /// <c>&lt;issuer&gt;/.well-known/jwks.json</c>, which is why the JWKS
    /// adapter takes an explicit URL for this profile.
    /// </summary>
    public static Uri GoogleJwksUrl { get; } = new("https://www.googleapis.com/oauth2/v3/certs");

    /// <summary>Require both settings, or refuse to verify anything — missing configuration never means "allow".</summary>
    public static AuthSettings SettingsOrThrow(string? issuerUrl, string? clientId)
    {
        if (string.IsNullOrEmpty(issuerUrl) || string.IsNullOrEmpty(clientId))
        {
            throw new AuthenticationException(AuthFailureReason.NotConfigured);
        }

        return new AuthSettings(issuerUrl.TrimEnd('/'), clientId);
    }

    /// <summary>
    /// The client-portal profile (ADR 0023): the pool's issuer, the portal's
    /// OWN app client — and a refusal when that client is not disjoint from
    /// the staff one, since without <c>aud</c> the client id IS the audience.
    /// </summary>
    public static AuthSettings PortalSettingsOrThrow(string? issuerUrl, string? portalClientId, string? staffClientId)
    {
        if (string.IsNullOrEmpty(portalClientId) || string.IsNullOrEmpty(staffClientId))
        {
            throw new AuthenticationException(AuthFailureReason.NotConfigured);
        }

        if (portalClientId == staffClientId)
        {
            throw new AuthenticationException(AuthFailureReason.NotConfigured);
        }

        return SettingsOrThrow(issuerUrl, portalClientId);
    }

    /// <summary>Require the issuer and a non-empty allowlist of non-empty ids, or refuse.</summary>
    public static MultiClientAuthSettings MultiClientSettingsOrThrow(string? issuerUrl, IReadOnlyList<string?>? clientIds)
    {
        if (string.IsNullOrEmpty(issuerUrl) || clientIds is null || clientIds.Count == 0 || clientIds.Any(string.IsNullOrEmpty))
        {
            throw new AuthenticationException(AuthFailureReason.NotConfigured);
        }

        return new MultiClientAuthSettings(issuerUrl.TrimEnd('/'), clientIds.Select(id => id!).ToValueCollection());
    }

    /// <summary>Require both Google settings, or refuse to verify anything.</summary>
    public static GoogleAuthSettings GoogleSettingsOrThrow(string? clientId, string? workspaceDomain)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(workspaceDomain))
        {
            throw new AuthenticationException(AuthFailureReason.NotConfigured);
        }

        return new GoogleAuthSettings(clientId, workspaceDomain);
    }

    /// <summary>
    /// The token out of an <c>Authorization: Bearer &lt;jwt&gt;</c> header.
    /// Absent and malformed are different reasons on purpose — only the log
    /// line tells them apart.
    /// </summary>
    public static string BearerToken(string? headerValue)
    {
        if (headerValue is null || PyText.Strip(headerValue).Length == 0)
        {
            throw new AuthenticationException(AuthFailureReason.MissingCredentials);
        }

        var parts = PyText.SplitWhitespace(headerValue);
        if (parts.Length != 2 || !string.Equals(parts[0], "bearer", StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0)
        {
            throw new AuthenticationException(AuthFailureReason.MalformedHeader);
        }

        return parts[1];
    }

    /// <summary>
    /// The <c>kid</c> from the token's (still unverified) header — trusted only
    /// as "which key to try". An attacker-chosen <c>alg</c> is refused here,
    /// before a key lookup.
    /// </summary>
    public static string KeyId(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var header = UnverifiedHeader(token);
        var kid = Json.AsString(header["kid"]);
        if (string.IsNullOrEmpty(kid))
        {
            throw new AuthenticationException(AuthFailureReason.MalformedToken);
        }

        var alg = Json.AsString(header["alg"]);
        if (alg is null || !Algorithms.Contains(alg, StringComparer.Ordinal))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidSignature);
        }

        return kid;
    }

    /// <summary>Verify a Cognito access token for exactly one app client and return the principal.</summary>
    public static async Task<Principal> VerifyAccessTokenAsync(string token, SecurityKey signingKey, AuthSettings settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var claims = await DecodeCognitoAsync(token, signingKey, settings.IssuerUrl, clock).ConfigureAwait(false);
        return PrincipalFromClaims(claims, settings);
    }

    /// <summary><see cref="VerifyAccessTokenAsync"/> with the client check against an ALLOWLIST (the MCP profile).</summary>
    public static async Task<Principal> VerifyAccessTokenForClientsAsync(string token, SecurityKey signingKey, MultiClientAuthSettings settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var claims = await DecodeCognitoAsync(token, signingKey, settings.IssuerUrl, clock).ConfigureAwait(false);
        return BuildPrincipal(claims, clientId => settings.ClientIds.Contains(clientId, StringComparer.Ordinal));
    }

    /// <summary>
    /// The claim checks the decoder does not do — Cognito access tokens for
    /// THIS app client — then the identity itself.
    /// </summary>
    public static Principal PrincipalFromClaims(JsonObject claims, AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return BuildPrincipal(claims, clientId => clientId == settings.ClientId);
    }

    /// <summary>
    /// <paramref name="authenticatedAt"/> (a principal's <c>auth_time</c>) if
    /// it is within <paramref name="maxAgeSeconds"/> of <paramref name="now"/>;
    /// otherwise <see cref="ReauthenticationRequiredException"/>. A missing time
    /// is stale, never fresh; one more than the skew in the future is refused.
    /// </summary>
    public static long RequireRecentAuthentication(long? authenticatedAt, DateTimeOffset now, int maxAgeSeconds)
    {
        if (maxAgeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgeSeconds), maxAgeSeconds, "max_age_seconds must be positive");
        }

        if (authenticatedAt is null)
        {
            throw new ReauthenticationRequiredException("sign in again to continue");
        }

        var age = (now.ToUnixTimeMilliseconds() / 1000.0) - authenticatedAt.Value;
        if (age > maxAgeSeconds || age < -ClockSkewSeconds)
        {
            throw new ReauthenticationRequiredException("sign in again to continue");
        }

        return authenticatedAt.Value;
    }

    /// <summary>Verify a Google Workspace ID token and return the staff principal.</summary>
    public static async Task<StaffPrincipal> VerifyGoogleIdTokenAsync(string token, SecurityKey signingKey, GoogleAuthSettings settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (claims, failure) = await JwtClaimsDecoder
            .DecodeAsync(token, signingKey, GoogleRequired, issuer: null, audience: settings.ClientId, clock)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            throw new AuthenticationException(failure switch
            {
                JwtFailure.Expired => AuthFailureReason.Expired,
                JwtFailure.InvalidAudience => AuthFailureReason.InvalidClient,
                JwtFailure.MissingRequiredClaim => AuthFailureReason.InvalidClaims,
                _ => AuthFailureReason.InvalidSignature,
            });
        }

        return StaffPrincipalFromClaims(claims!, settings);
    }

    /// <summary>
    /// The Google claim checks the decoder does not do: the issuer (two
    /// spellings), the Workspace domain, a verified email, a subject.
    /// </summary>
    public static StaffPrincipal StaffPrincipalFromClaims(JsonObject claims, GoogleAuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(settings);
        var issuer = Json.AsString(claims["iss"]);
        if (issuer is null || !GoogleIssuers.Contains(issuer, StringComparer.Ordinal))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidIssuer);
        }

        // `hd` is present only on Workspace accounts: a personal Gmail has
        // none and fails the comparison, which is the right reading.
        if (Json.AsString(claims["hd"]) != settings.WorkspaceDomain)
        {
            throw new AuthenticationException(AuthFailureReason.InvalidClaims);
        }

        var email = Json.AsString(claims["email"]);
        if (Json.AsBool(claims["email_verified"]) != true || string.IsNullOrEmpty(email))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidClaims);
        }

        var subject = Json.AsString(claims["sub"]);
        if (string.IsNullOrEmpty(subject))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidClaims);
        }

        return new StaffPrincipal(subject, email, WholeNumberOrNull(claims["exp"]));
    }

    private static async Task<JsonObject> DecodeCognitoAsync(string token, SecurityKey signingKey, string issuer, TimeProvider? clock)
    {
        var (claims, failure) = await JwtClaimsDecoder
            .DecodeAsync(token, signingKey, CognitoRequired, issuer, audience: null, clock)
            .ConfigureAwait(false);
        if (failure is not null)
        {
            throw new AuthenticationException(failure switch
            {
                JwtFailure.Expired => AuthFailureReason.Expired,
                JwtFailure.InvalidIssuer => AuthFailureReason.InvalidIssuer,
                JwtFailure.MissingRequiredClaim => AuthFailureReason.InvalidClaims,
                _ => AuthFailureReason.InvalidSignature,
            });
        }

        return claims!;
    }

    private static Principal BuildPrincipal(JsonObject claims, Func<string, bool> clientAllowed)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (Json.AsString(claims["token_use"]) != TokenUseAccess)
        {
            throw new AuthenticationException(AuthFailureReason.WrongTokenUse);
        }

        var clientId = Json.AsString(claims["client_id"]);
        if (clientId is null || !clientAllowed(clientId))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidClient);
        }

        var subject = Json.AsString(claims["sub"]);
        if (string.IsNullOrEmpty(subject))
        {
            throw new AuthenticationException(AuthFailureReason.InvalidClaims);
        }

        var username = Json.AsString(claims["username"]);
        var scope = Json.AsString(claims["scope"]);
        return new Principal(
            Subject: subject,
            Username: string.IsNullOrEmpty(username) ? null : username,
            ClientId: clientId,
            Scopes: scope is null ? [] : PyText.SplitWhitespace(scope).ToValueCollection(),
            ExpiresAt: WholeNumberOrNull(claims["exp"]),
            AuthenticatedAt: AuthTime(claims["auth_time"]));
    }

    // `auth_time` only when it is a positive whole number: absent, a bool, a
    // string or zero all read as unknown, which the freshness check treats as stale.
    private static long? AuthTime(JsonNode? raw) =>
        Json.TryGetWholeNumber(raw, out var value) && value > 0 ? value : null;

    private static long? WholeNumberOrNull(JsonNode? raw) =>
        Json.TryGetWholeNumber(raw, out var value) ? value : null;

    // PyJWT's get_unverified_header: at least three segments (split at the
    // first and last dot), a base64url header that decodes to a JSON object,
    // and payload/signature segments that decode at all. Everything else is
    // MALFORMED_TOKEN, as every PyJWTError was.
    private static JsonObject UnverifiedHeader(string token)
    {
        var first = token.IndexOf('.', StringComparison.Ordinal);
        var last = token.LastIndexOf('.');
        if (first < 0 || last == first)
        {
            throw new AuthenticationException(AuthFailureReason.MalformedToken);
        }

        try
        {
            var header = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token[..first]))) as JsonObject;
            _ = Base64UrlEncoder.DecodeBytes(token[(first + 1)..last]);
            _ = Base64UrlEncoder.DecodeBytes(token[(last + 1)..]);
            if (header is null)
            {
                throw new AuthenticationException(AuthFailureReason.MalformedToken);
            }

            // PyJWT's header validation: a present `kid` must be a string.
            if (header.ContainsKey("kid") && !Json.IsString(header["kid"]))
            {
                throw new AuthenticationException(AuthFailureReason.MalformedToken);
            }

            return header;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            throw new AuthenticationException(AuthFailureReason.MalformedToken, error);
        }
    }
}
