using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// Real RS256 tokens shaped like Cognito access tokens, signed with a keypair
/// made for the run — test_auth.py's <c>make_token</c>. Nothing is mocked: the
/// API verifies them through Core's verifier and the static JWKS provider.
/// Every id is obviously fake; this repo is public.
/// </summary>
internal static class Tokens
{
    public const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00";
    public const string OtherIssuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_OTHER000";
    public const string StaffClientId = "exampleappclientid000000";
    public const string PortalClientId = "exampleportalclientid000";
    public const string OtherClientId = "someotherappclientid0000";
    public const string Kid = "test-key-1";
    public const string UnknownKid = "test-key-absent";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RSA Imposter = RSA.Create(2048);
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>The public half, as the JWKS provider serves it.</summary>
    public static SecurityKey PublicKey { get; } = new RsaSecurityKey(Rsa.ExportParameters(false)) { KeyId = Kid };

    public static string Make(
        string subject = Ids.Alice,
        string clientId = StaffClientId,
        string issuer = Issuer,
        string tokenUse = "access",
        int expiresIn = 3600,
        string kid = Kid,
        bool imposter = false,
        bool withSubject = true,
        JsonObject? extra = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = issuer,
            ["client_id"] = clientId,
            ["token_use"] = tokenUse,
            ["iat"] = now,
            ["auth_time"] = now,
            ["exp"] = now + expiresIn,
            ["jti"] = "00000000-0000-4000-8000-0000000000ff",
            ["username"] = subject,
            ["scope"] = "aws.cognito.signin.user.admin",
        };
        if (withSubject)
        {
            claims["sub"] = subject;
        }

        foreach (var (name, value) in extra ?? [])
        {
            claims[name] = value?.DeepClone();
        }

        var key = new RsaSecurityKey(imposter ? Imposter : Rsa) { KeyId = kid };
        return Handler.CreateToken(claims.ToJsonString(), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    }

    public static string Staff(string subject) => Make(subject, StaffClientId);

    public static string Portal(string subject) => Make(subject, PortalClientId);

    /// <summary>A token with an arbitrary header and an empty signature (alg none and friends).</summary>
    public static string Unsigned(string headerJson, string payloadJson) =>
        Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson)) + "." + Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson)) + ".";
}

/// <summary>The synthetic people and firms of the suites (test_portal_routes.py's cast).</summary>
internal static class Ids
{
    public const string FirmA = "00000000-0000-4000-8000-00000000f18a";
    public const string FirmB = "00000000-0000-4000-8000-00000000f18b";
    public const string Alice = "00000000-0000-4000-8000-00000000a11c";
    public const string Bob = "00000000-0000-4000-8000-00000000b0b0";
    public const string Dana = "00000000-0000-4000-8000-00000000da4a";
    public const string Erin = "00000000-0000-4000-8000-00000000e214";
    public const string Sam = "00000000-0000-4000-8000-000000005a11";
    public const string Pat = "00000000-0000-4000-8000-00000000ba70";
    public const string Quinn = "00000000-0000-4000-8000-00000000c0b1";
    public const string Ray = "00000000-0000-4000-8000-00000000ab1e";
    public const string Stranger = "00000000-0000-4000-8000-00000000dead";
    public const string Case = "00000000-0000-4000-8000-0000000ca5e1";
    public const string OtherCase = "00000000-0000-4000-8000-0000000ca5e2";
    public const string FirmBCase = "00000000-0000-4000-8000-0000000ca5e3";
}
