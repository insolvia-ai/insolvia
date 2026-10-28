using Insolvia.Api.Configuration;
using Insolvia.Api.Logging;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Auth;
using Insolvia.Core.Clients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Auth;

/// <summary>
/// The client-portal principal (ADR 0023; services/api api/client_auth.py):
/// the only filter a <c>/v1/portal/</c> route may carry. It does in ONE step
/// what staff routes split across <see cref="StaffAuth.RequireAuth{TBuilder}"/>
/// and <see cref="StaffAuth.CurrentAccessorAsync"/>, because the portal has no
/// route that answers for a caller with nothing to resolve.
/// </summary>
/// <remarks>
/// <para>The token's <c>client_id</c> must be <c>AUTH_PORTAL_CLIENT_ID</c> and
/// nothing else; the two ids must differ or nothing verifies at all —
/// disjointness IS the audience check, since Cognito access tokens carry no
/// <c>aud</c>.</para>
/// <para>The binding is found by subject, then RE-READ BY KEY (strongly
/// consistent), so a revocation takes effect on the very next request; then
/// the firm, so a suspended firm's clients are suspended with it. A binding
/// that exists and no longer admits its holder answers 403 AND writes a
/// <c>denied</c> access-log row against its case — what someone whose access
/// was withdrawn trying anyway looks like. A subject with no binding has no
/// case to record against: the same 403, and only the metadata log line.</para>
/// </remarks>
public static class ClientAuth
{
    // One message for every reason — the instruction is the same: talk to your law firm.
    private const string Refused = "you do not have access to a case through the client portal";

    private static readonly object ClientKey = new();
    private static readonly object ClientPrincipalKey = new();

    /// <summary>
    /// 401 unless the request carries a valid CLIENT-PORTAL token; 403 unless
    /// that client holds a live binding in an active firm.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint builder.</typeparam>
    /// <param name="builder">The route (or group).</param>
    /// <returns>The same builder.</returns>
    public static TBuilder RequireClient<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(PrincipalClass.Client);
        return builder.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            Principal principal;
            try
            {
                principal = await AuthenticateClientAsync(context).ConfigureAwait(false);
            }
            catch (AuthenticationException error)
            {
                StaffAuth.Logger(context, StructuredLog.Categories.ClientAuth).Info("portal authentication rejected", ("reason", error.Reason.ToWire()));
                return JsonBodyResult.Unauthorized;
            }

            context.Items[ClientPrincipalKey] = principal;
            context.Items[ClientKey] = await ResolveClientAsync(context, principal).ConfigureAwait(false);
            return await next(invocation).ConfigureAwait(false);
        });
    }

    /// <summary>The client <see cref="RequireClient{TBuilder}"/> resolved; a 500 on an endpoint without it.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The client accessor.</returns>
    public static ClientAccessor CurrentClient(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ClientKey, out var client) && client is ClientAccessor found
            ? found
            : throw new InvalidOperationException("CurrentClient() requires RequireClient() on the endpoint");
    }

    /// <summary>
    /// The verified portal token's principal — where an <c>origin</c> takes
    /// <c>client_id</c> and <c>subject</c> from, never a body.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The portal principal.</returns>
    public static Principal CurrentClientPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ClientPrincipalKey, out var principal) && principal is Principal found
            ? found
            : throw new InvalidOperationException("CurrentClientPrincipal() requires RequireClient() on the endpoint");
    }

    private static Task<Principal> AuthenticateClientAsync(HttpContext context)
    {
        var config = context.RequestServices.GetRequiredService<ApiConfig>();
        return StaffAuth.VerifyRequestTokenAsync(
            context,
            TokenVerification.PortalSettingsOrThrow(config.AuthIssuerUrl, config.AuthPortalClientId, config.AuthClientId));
    }

    private static async Task<ClientAccessor> ResolveClientAsync(HttpContext context, Principal principal)
    {
        var services = context.RequestServices;
        var bindings = services.GetService<IClientBindingStore>()
            ?? throw new InvalidOperationException("client binding store is not composed");
        var logger = StaffAuth.Logger(context, StructuredLog.Categories.ClientAuth);
        var cancellation = context.RequestAborted;

        var found = await bindings.FindAsync(principal.Subject, cancellation).ConfigureAwait(false);
        if (found is null)
        {
            logger.Info("client unresolved", ("reason", "no_binding"));
            throw new ForbiddenException(Refused);
        }

        // Re-read by key: the by-subject lookup may lag, the row does not.
        var binding = await bindings.GetAsync(found.FirmId, principal.Subject, cancellation).ConfigureAwait(false);
        if (binding is null)
        {
            logger.Info("client unresolved", ("reason", "no_binding"));
            throw new ForbiddenException(Refused);
        }

        if (!binding.Live)
        {
            await RefuseAsync(context, binding, "binding_revoked").ConfigureAwait(false);
        }

        var firms = services.GetService<IFirmStore>()
            ?? throw new InvalidOperationException("firm store is not composed");
        var firm = await firms.GetFirmAsync(binding.FirmId, cancellation).ConfigureAwait(false);
        if (firm is null || firm.Status != FirmStatus.Active)
        {
            await RefuseAsync(context, binding, "firm_not_active").ConfigureAwait(false);
        }

        if (binding.Status == BindingStatus.Invited)
        {
            // The first request after the first sign-in — written once, here.
            var activated = await bindings
                .UpdateAsync(ClientBindings.Activate(binding, services.GetService<TimeProvider>()), cancellation)
                .ConfigureAwait(false);

            // Revoked-and-gone between the read and the write.
            binding = activated ?? throw new ForbiddenException(Refused);
        }

        return new ClientAccessor(firm!, binding);
    }

    // A binding that no longer admits its holder: the denial is recorded
    // against its case, then 403. The verb is the one the request would have
    // been — a read for a safe method, an answer otherwise.
    private static async Task RefuseAsync(HttpContext context, ClientBinding binding, string reason)
    {
        var accessLog = context.RequestServices.GetService<IAccessLog>()
            ?? throw new InvalidOperationException("access log is not composed");
        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        await accessLog.RecordAsync(
            AccessEvents.Record(
                principal: binding.Subject,
                action: safe ? AccessAction.PortalRead : AccessAction.PortalAnswer,
                caseId: binding.CaseId,
                outcome: AccessOutcome.Denied,
                clock: context.RequestServices.GetService<TimeProvider>()),
            context.RequestAborted).ConfigureAwait(false);

        // A category, never the subject or the case.
        StaffAuth.Logger(context, StructuredLog.Categories.ClientAuth).Info("client unresolved", ("reason", reason));
        throw new ForbiddenException(Refused);
    }
}
