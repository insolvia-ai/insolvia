using System.Security.Claims;
using System.Text.Encodings.Web;
using Insolvia.Core;
using Insolvia.Core.Auth;
using Insolvia.Core.Ports;
using Insolvia.Mcp.Configuration;
using Insolvia.Mcp.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Insolvia.Mcp.Auth;

/// <summary>
/// The MCP surface's token verification (services/mcp <c>api/auth.py</c>'s
/// <c>CognitoTokenVerifier</c>) as an ASP.NET authentication scheme: the
/// RS256 / issuer / token_use / client_id checks of
/// <see cref="TokenVerification.VerifyAccessTokenForClientsAsync"/>, unchanged.
/// </summary>
/// <remarks>
/// <para>FAIL CLOSED: no header, a malformed token, a bad signature, expired,
/// the wrong issuer, client or token_use, an unknown kid, and no auth
/// configuration on this deployment at all are every one a failed
/// authentication, which the challenge answers with the same 401. A rejection
/// logs the coarse <see cref="AuthFailureReason"/> and nothing else (GLBA).</para>
/// <para>THE CLIENT-ID ALLOWLIST IS THE AUDIENCE CHECK. Cognito access tokens
/// carry <c>client_id</c>, never an RFC 8707 <c>aud</c>; this service verifies
/// exactly its own pre-registered clients, disjoint from the app's, so an app
/// token presented here fails closed (ADR 0016). The gap against the spec's
/// aud-validation MUST is real and stated in mcp-surface.md § Identity.</para>
/// <para>The challenge is written here rather than by the SDK's
/// <c>McpAuthenticationHandler</c> because the Python SDK's is the contract:
/// <c>error="invalid_token"</c>, its description, a JSON body, and a
/// <c>resource_metadata</c> built from the CONFIGURED resource URL — never
/// from the request's Host, which behind API Gateway is not ours to trust.</para>
/// </remarks>
public sealed class BearerAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    McpConfig config,
    IServiceProvider services)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "InsolviaBearer";

    /// <summary>The verified subject's claim type.</summary>
    public const string SubjectClaim = "sub";

    /// <summary>The verified app client's claim type.</summary>
    public const string ClientIdClaim = "client_id";

    /// <summary>One verified scope (Cognito's space-delimited <c>scope</c>, split).</summary>
    public const string ScopeClaim = "scope";

    private readonly ILogger _log = loggerFactory.CreateLogger(StructuredLog.Categories.Auth);

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The Python SDK's bearer backend never calls the verifier without a
        // bearer credential, so no header is no log line — only a token that
        // was presented and refused is.
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var token = TokenVerification.BearerToken(header);
            var settings = TokenVerification.MultiClientSettingsOrThrow(config.AuthIssuerUrl, [.. config.AuthClientIds]);

            // Configured issuer and clients but no provider composed: a
            // broken deployment, not a caller error. Still a rejection.
            var provider = services.GetService<IJwksProvider>()
                ?? throw new AuthenticationException(AuthFailureReason.NotConfigured);
            var key = await provider.GetSigningKeyAsync(TokenVerification.KeyId(token), Context.RequestAborted).ConfigureAwait(false);
            var principal = await TokenVerification.VerifyAccessTokenForClientsAsync(token, key, settings).ConfigureAwait(false);

            var claims = new List<Claim>
            {
                new(SubjectClaim, principal.Subject),
                new(ClientIdClaim, principal.ClientId),
            };
            claims.AddRange(principal.Scopes.Select(scope => new Claim(ScopeClaim, scope)));
            var identity = new ClaimsIdentity(claims, SchemeName, SubjectClaim, null);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (AuthenticationException error)
        {
            // Category only. No token, no claims, no subject.
            _log.Info("authentication rejected", ("reason", error.Reason.ToWire()));
            return AuthenticateResult.Fail("rejected");
        }
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate =
            $"Bearer error=\"invalid_token\", error_description=\"Authentication required\", resource_metadata=\"{config.ResourceMetadataUrl}\"";
        Response.ContentType = "application/json";
        await Response.WriteAsync("{\"error\":\"invalid_token\",\"error_description\":\"Authentication required\"}", Context.RequestAborted).ConfigureAwait(false);
    }
}
