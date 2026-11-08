using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Core.Auth;

/// <summary>What PyJWT's <c>decode</c> raised, as the categories the profiles map.</summary>
internal enum JwtFailure
{
    Expired,
    InvalidIssuer,
    InvalidAudience,
    MissingRequiredClaim,
    InvalidSignature,
    Other,
}

/// <summary>
/// The PyJWT half of verification, reproduced: the SIGNATURE is checked by
/// Microsoft.IdentityModel (RS256 only, against the one key the JWKS port
/// produced), and the claim checks run here, in PyJWT 2.x's order and with its
/// comparisons — required claims, <c>iat</c>, <c>nbf</c>, <c>exp</c> (each
/// with the leeway), <c>iss</c>, <c>aud</c>, then <c>sub</c>/<c>jti</c> types
/// — so each failure lands in the <see cref="AuthFailureReason"/> the contract
/// names for it. The library's own lifetime/issuer/audience validation is off on
/// purpose: its ordering and boundary rules differ, and the reasons would drift.
/// </summary>
internal static class JwtClaimsDecoder
{
    private static readonly JsonWebTokenHandler Handler = new();

    public static async Task<(JsonObject? Claims, JwtFailure? Failure)> DecodeAsync(
        string token,
        SecurityKey signingKey,
        IReadOnlyList<string> required,
        string? issuer,
        string? audience,
        TimeProvider? clock)
    {
        var parameters = new TokenValidationParameters
        {
            IssuerSigningKey = signingKey,
            ValidAlgorithms = TokenVerification.Algorithms,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = false,
            TryAllIssuerSigningKeys = true,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            RequireAudience = false,
        };

        TokenValidationResult result;
        try
        {
            result = await Handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return (null, JwtFailure.InvalidSignature);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken verified)
        {
            return (null, JwtFailure.InvalidSignature);
        }

        JsonObject? claims;
        try
        {
            claims = JsonNode.Parse(Base64UrlEncoder.Decode(verified.EncodedPayload)) as JsonObject;
        }
        catch (JsonException)
        {
            claims = null;
        }

        if (claims is null)
        {
            return (null, JwtFailure.Other);
        }

        var failure = Validate(claims, required, issuer, audience, clock);
        return failure is null ? (claims, null) : (null, failure);
    }

    private static JwtFailure? Validate(
        JsonObject claims,
        IReadOnlyList<string> required,
        string? issuer,
        string? audience,
        TimeProvider? clock)
    {
        foreach (var claim in required)
        {
            if (claims[claim] is null)
            {
                return JwtFailure.MissingRequiredClaim;
            }
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        double leeway = TokenVerification.ClockSkewSeconds;

        if (claims.ContainsKey("iat"))
        {
            if (!TryCoerceInt(claims["iat"], out var iat))
            {
                return JwtFailure.Other;
            }

            if (iat > now + leeway)
            {
                return JwtFailure.Other;
            }
        }

        if (claims.ContainsKey("nbf"))
        {
            if (!TryCoerceInt(claims["nbf"], out var nbf) || nbf > now + leeway)
            {
                return JwtFailure.Other;
            }
        }

        if (claims.ContainsKey("exp"))
        {
            if (!TryCoerceInt(claims["exp"], out var exp))
            {
                return JwtFailure.Other;
            }

            if (exp <= now - leeway)
            {
                return JwtFailure.Expired;
            }
        }

        if (issuer is not null)
        {
            if (!claims.ContainsKey("iss"))
            {
                return JwtFailure.MissingRequiredClaim;
            }

            if (Json.AsString(claims["iss"]) != issuer)
            {
                return JwtFailure.InvalidIssuer;
            }
        }

        if (audience is not null)
        {
            var aud = claims["aud"];
            if (aud is null || IsFalsy(aud))
            {
                return JwtFailure.MissingRequiredClaim;
            }

            // A string is a one-element list; anything but a list of strings is refused.
            List<string?> asStrings = aud switch
            {
                JsonArray list => [.. list.Select(Json.AsString)],
                _ => [Json.AsString(aud)],
            };
            if (asStrings.Any(value => value is null) || !asStrings.Contains(audience, StringComparer.Ordinal))
            {
                return JwtFailure.InvalidAudience;
            }
        }

        if (claims.ContainsKey("sub") && !Json.IsString(claims["sub"]))
        {
            return JwtFailure.Other;
        }

        if (claims.ContainsKey("jti") && !Json.IsString(claims["jti"]))
        {
            return JwtFailure.Other;
        }

        return null;
    }

    // Python's `int(x)` over a JSON value: a number truncates toward zero, a
    // string must spell an integer, a bool is 0 or 1. Anything else (a TypeError
    // under `int(x)`, one PyJWT does not catch) is a refusal here.
    private static bool TryCoerceInt(JsonNode? node, out double value)
    {
        value = 0;
        if (Json.AsBool(node) is { } flag)
        {
            value = flag ? 1 : 0;
            return true;
        }

        if (Json.TryGetNumber(node, out var number))
        {
            if (!double.IsFinite(number))
            {
                return false;
            }

            value = Math.Truncate(number);
            return true;
        }

        var text = Json.AsString(node);
        if (text is not null && long.TryParse(text.Trim().Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool IsFalsy(JsonNode node) => node switch
    {
        JsonArray list => list.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ when Json.AsString(node) is { } text => text.Length == 0,
        _ when Json.AsBool(node) is { } flag => !flag,
        _ when Json.TryGetNumber(node, out var number) => number == 0,
        _ => false,
    };
}
