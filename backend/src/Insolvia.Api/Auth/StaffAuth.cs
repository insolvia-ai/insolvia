using Insolvia.Api.Configuration;
using Insolvia.Api.Logging;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Auth;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Auth;

/// <summary>
/// Staff authentication and authorization as endpoint filters (services/api
/// api/auth.py). OPT-IN, never middleware: a route is public unless it says
/// otherwise, so <c>/health</c>, the waitlist and unsubscribe never run a
/// token check — and an <c>Authorization</c> header on them is ignored.
/// </summary>
/// <remarks>
/// <para><b>Three states, not two.</b> A route is public, authenticated, or
/// authenticated-and-permitted:</para>
/// <code>
/// app.MapPost("/v1/cases", Create)
///    .RequireAuth()                                         // 401 — who are you
///    .Requires(Feature.Cases, PermissionLevel.AddEdit);     // 403 — what may you do
/// </code>
/// <para><b>Order is the decorators' order.</b> Endpoint filters run in the
/// order they are added, the first outermost — exactly as Flask's
/// <c>@require_auth</c> above <c>@requires</c>. So <c>RequireAuth</c> must
/// come first: <c>Requires</c> placed before it (or without it) reads a
/// principal nobody has set, which is a programming error and a 500 — as
/// <c>current_principal()</c> raised in Python — never a request that proceeds.
/// Filters attach only to delegate handlers (<c>MapGet(pattern, (…) =&gt; …)</c>);
/// a raw <c>RequestDelegate</c> endpoint ignores them, so feature routes use delegates.</para>
/// <para><b>Accessor resolution is a separate step</b>
/// (<see cref="CurrentAccessorAsync"/>): <c>/v1/me</c> must keep answering for
/// someone signed in and not yet in a firm, so it calls
/// <see cref="ResolveAccessorAsync"/> and reports the absence instead.</para>
/// <para><b>Fail closed.</b> Every authentication failure — no header,
/// malformed, bad signature, expired, wrong issuer, wrong client, wrong
/// <c>token_use</c>, unknown <c>kid</c>, and no auth configuration at all — is
/// the same 401 body. A rejection logs its <see cref="AuthFailureReason"/>
/// category and nothing else: never the token, a claim, or the subject.</para>
/// </remarks>
public static class StaffAuth
{
    private static readonly object PrincipalKey = new();
    private static readonly object AccessorKey = new();

    /// <summary>
    /// 401 unless the request carries a valid STAFF access token — one minted
    /// for <c>AUTH_CLIENT_ID</c>. A portal token names the portal client and
    /// fails here with <c>invalid_client</c>, which is the whole of why a
    /// debtor's token reaches no staff route (ADR 0023).
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder.</typeparam>
    /// <param name="builder">The route (or group).</param>
    /// <returns>The same builder.</returns>
    public static TBuilder RequireAuth<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(PrincipalClass.Staff);
        return builder.AddEndpointFilter(async (invocation, next) =>
            await AuthenticateHereAsync(invocation.HttpContext).ConfigureAwait(false)
                ? await next(invocation).ConfigureAwait(false)
                : JsonBodyResult.Unauthorized);
    }

    /// <summary>
    /// <see cref="RequireAuth{TBuilder}"/>'s check run INSIDE a handler — for
    /// the one route that authenticates one of two ways (the calendar feed:
    /// its own token, or a staff session when it has none). False, the
    /// rejection logged by category, is the caller's 401.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether the request carries a valid staff token.</returns>
    internal static async Task<bool> AuthenticateHereAsync(HttpContext context)
    {
        try
        {
            context.Items[PrincipalKey] = await AuthenticateAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AuthenticationException error)
        {
            Logger(context, StructuredLog.Categories.Auth).Info("authentication rejected", ("reason", error.Reason.ToWire()));
            return false;
        }
    }

    /// <summary>
    /// 403 unless the caller's firm grants them <paramref name="level"/> on
    /// <paramref name="feature"/>. Resolves the accessor as a side effect, so
    /// an unprovisioned caller is refused here rather than reaching a handler
    /// with no firm to scope a store by. Add it AFTER <see cref="RequireAuth{TBuilder}"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder.</typeparam>
    /// <param name="builder">The route (or group).</param>
    /// <param name="feature">The feature.</param>
    /// <param name="level">The level required.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder Requires<TBuilder>(this TBuilder builder, Feature feature, PermissionLevel level)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            await RequirePermissionAsync(invocation.HttpContext, feature, level).ConfigureAwait(false);
            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary><see cref="Requires{TBuilder}"/>'s check run inside a handler, after <see cref="AuthenticateHereAsync"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="feature">The feature.</param>
    /// <param name="level">The level required.</param>
    /// <returns>The caller's accessor.</returns>
    internal static async Task<Accessor> RequirePermissionAsync(HttpContext context, Feature feature, PermissionLevel level)
    {
        var accessor = await context.CurrentAccessorAsync().ConfigureAwait(false);
        if (!accessor.May(feature, level))
        {
            // The FEATURE is logged, never the subject.
            Logger(context, StructuredLog.Categories.Auth).Info("permission refused", ("feature", feature.ToWire()));
            throw new ForbiddenException("your firm has not granted you access to this");
        }

        return accessor;
    }

    /// <summary>
    /// The principal <see cref="RequireAuth{TBuilder}"/> verified. Throws
    /// (a 500) on an endpoint without it — a programming error, not a request
    /// the caller can provoke.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The staff principal.</returns>
    public static Principal CurrentPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(PrincipalKey, out var principal) && principal is Principal found
            ? found
            : throw new InvalidOperationException("CurrentPrincipal() requires RequireAuth() on the endpoint, before any filter that reads it");
    }

    /// <summary>
    /// The caller's accessor, or null when they are not active in an active
    /// firm — the non-throwing half, for the one route (<c>/v1/me</c>) that
    /// REPORTS a missing firm. TWO uncached reads (the user by subject, then
    /// the firm, so a suspended firm is suspended), once per request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The accessor, or null.</returns>
    public static async Task<Accessor?> ResolveAccessorAsync(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(AccessorKey, out var cached) && cached is Accessor accessor)
        {
            return accessor;
        }

        // A composition bug, not a caller error: a deployment that cannot
        // resolve anyone must not look like every user being unprovisioned.
        var firms = context.RequestServices.GetService<IFirmStore>()
            ?? throw new InvalidOperationException("firm store is not composed");

        var subject = context.CurrentPrincipal().Subject;
        var user = await firms.FindUserAsync(subject, context.RequestAborted).ConfigureAwait(false);
        if (user is null || user.Status != FirmUserStatus.Active)
        {
            Logger(context, StructuredLog.Categories.Auth).Info("accessor unresolved", ("reason", "no_active_firm_user"));
            return null;
        }

        var firm = await firms.GetFirmAsync(user.FirmId, context.RequestAborted).ConfigureAwait(false);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            Logger(context, StructuredLog.Categories.Auth).Info("accessor unresolved", ("reason", "firm_not_active"));
            return null;
        }

        accessor = new Accessor(firm, user);
        context.Items[AccessorKey] = accessor;
        return accessor;
    }

    /// <summary>
    /// The caller's firm and firm user, or a 403 — ONE message for every
    /// reason (disabled, never added, firm suspended): each is the same
    /// instruction to talk to the firm's admin.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The accessor.</returns>
    public static async Task<Accessor> CurrentAccessorAsync(this HttpContext context) =>
        await context.ResolveAccessorAsync().ConfigureAwait(false)
            ?? throw new ForbiddenException("your account is not active in a firm");

    /// <summary>
    /// The caller's <c>auth_time</c> if they signed in within
    /// <paramref name="maxAgeSeconds"/>; otherwise a 403
    /// <c>ReauthenticationRequired</c> (ADR 0024: signing the filing
    /// authorization, approving a filing).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="maxAgeSeconds">How recent the sign-in must be.</param>
    /// <returns>The <c>auth_time</c>.</returns>
    public static long RequireFreshSignIn(this HttpContext context, int maxAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(context);
        var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        return TokenVerification.RequireRecentAuthentication(context.CurrentPrincipal().AuthenticatedAt, clock.GetUtcNow(), maxAgeSeconds);
    }

    /// <summary>
    /// The request's bearer token verified against <paramref name="settings"/>
    /// — the one implementation both principal classes share, so the
    /// cryptographic half cannot drift between them. Throws
    /// <see cref="AuthenticationException"/> for every refusal.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="settings">The issuer and the one client id to accept.</param>
    /// <returns>The verified principal.</returns>
    internal static async Task<Principal> VerifyRequestTokenAsync(HttpContext context, AuthSettings settings)
    {
        // Settings present but no provider composed: a broken deployment,
        // still a 401 — never "allow".
        var provider = context.RequestServices.GetService<IJwksProvider>()
            ?? throw new AuthenticationException(AuthFailureReason.NotConfigured);

        // Flask's headers.get: the first value when the header repeats.
        var header = context.Request.Headers.Authorization;
        var token = TokenVerification.BearerToken(header.Count > 0 ? header[0] : null);
        var key = await provider.GetSigningKeyAsync(TokenVerification.KeyId(token), context.RequestAborted).ConfigureAwait(false);
        return await TokenVerification
            .VerifyAccessTokenAsync(token, key, settings, context.RequestServices.GetService<TimeProvider>())
            .ConfigureAwait(false);
    }

    internal static ILogger Logger(HttpContext context, string category) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(category);

    private static Task<Principal> AuthenticateAsync(HttpContext context)
    {
        var config = context.RequestServices.GetRequiredService<ApiConfig>();
        return VerifyRequestTokenAsync(context, TokenVerification.SettingsOrThrow(config.AuthIssuerUrl, config.AuthClientId));
    }
}
