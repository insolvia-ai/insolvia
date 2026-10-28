using Insolvia.Admin.Configuration;
using Insolvia.Admin.Logging;
using Insolvia.Core;
using Insolvia.Core.Auth;
using Insolvia.Core.Ports;

namespace Insolvia.Admin.Auth;

/// <summary>
/// Staff authentication as an endpoint filter (services/admin api/auth.py's
/// <c>@require_staff</c>). Opt-in, never middleware: <c>/health</c> is public
/// and stays so.
/// </summary>
/// <remarks>
/// <para><b>The trust model in one rule.</b> The verifier here speaks the
/// GOOGLE profile (<see cref="TokenVerification.VerifyGoogleIdTokenAsync"/>)
/// and only that profile. A firm user's Cognito access token — valid,
/// unexpired, signed by AWS — dies in verification here, because it carries
/// the wrong issuer, no <c>aud</c> and no <c>hd</c>. There is no role check to
/// forget; the signature-and-claims check is the boundary (ADR 0011).</para>
/// <para><b>No third state.</b> A staff caller has no "authenticated but not
/// permitted" intermediate: the Workspace domain check inside verification is
/// the whole authorization. The day that stops being true, the decision point
/// is <see cref="TokenVerification.StaffPrincipalFromClaims"/>, not a
/// permissions table bolted on here.</para>
/// <para><b>Fail closed.</b> Every failure — no header, bad signature, wrong
/// issuer, no <c>GOOGLE_CLIENT_ID</c>, no key provider — is the same 401 body,
/// and the log line carries the <see cref="AuthFailureReason"/> category only.</para>
/// </remarks>
public static class StaffAuth
{
    private static readonly object StaffKey = new();

    /// <summary>401 unless the request carries a valid Google Workspace ID token.</summary>
    /// <typeparam name="TBuilder">The endpoint builder.</typeparam>
    /// <param name="builder">The route (or group).</param>
    /// <returns>The same builder.</returns>
    public static TBuilder RequireStaff<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            StaffPrincipal staff;
            try
            {
                staff = await AuthenticateAsync(context).ConfigureAwait(false);
            }
            catch (AuthenticationException error)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(StructuredLog.Categories.Auth)
                    .Info("staff authentication rejected", ("reason", error.Reason.ToWire()));
                return JsonBodyResult.Unauthorized;
            }

            context.Items[StaffKey] = staff;
            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The verified staff caller. Throws (a 500) on an endpoint without
    /// <see cref="RequireStaff{TBuilder}"/> — a programming error, never data.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The staff principal.</returns>
    public static StaffPrincipal CurrentStaff(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(StaffKey, out var found) && found is StaffPrincipal staff
            ? staff
            : throw new InvalidOperationException("CurrentStaff() requires RequireStaff() on the endpoint");
    }

    private static async Task<StaffPrincipal> AuthenticateAsync(HttpContext context)
    {
        var config = context.RequestServices.GetRequiredService<AdminConfig>();
        var settings = TokenVerification.GoogleSettingsOrThrow(config.GoogleClientId, config.WorkspaceDomain);
        var provider = context.RequestServices.GetService<IJwksProvider>()
            ?? throw new AuthenticationException(AuthFailureReason.NotConfigured);

        // Flask's headers.get: the first value when the header repeats.
        var header = context.Request.Headers.Authorization;
        var token = TokenVerification.BearerToken(header.Count > 0 ? header[0] : null);
        var key = await provider.GetSigningKeyAsync(TokenVerification.KeyId(token), context.RequestAborted).ConfigureAwait(false);
        return await TokenVerification
            .VerifyGoogleIdTokenAsync(token, key, settings, context.RequestServices.GetService<TimeProvider>())
            .ConfigureAwait(false);
    }
}

/// <summary>An <see cref="IResult"/> that writes a fixed JSON body with Flask's bare content type.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The serialized body.</param>
internal sealed record JsonBodyResult(int Status, string Body) : IResult
{
    /// <summary>The 401 every authentication failure answers.</summary>
    public static readonly JsonBodyResult Unauthorized = new(StatusCodes.Status401Unauthorized, Http.ErrorResponses.UnauthorizedJson);

    public Task ExecuteAsync(HttpContext httpContext) => Http.ErrorResponses.WriteJsonAsync(httpContext, Status, Body);
}
