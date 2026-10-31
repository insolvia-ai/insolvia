using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Configuration;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Api.Mail;
using Insolvia.Api.Portal;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Clients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// Inviting a debtor to the client portal — the FIRM side (ADR 0023;
/// routes/portal_invitations.py). Staff routes gated on <c>client_portal</c>;
/// the case is resolved under the caller's accessor FIRST, so a case the
/// caller may not see is a 404 here exactly as everywhere else.
/// </summary>
/// <remarks>
/// <para><b>Two messages, one purpose each.</b> The invitation creates the
/// pool user (<see cref="IUserDirectory.CreateUserAsync"/>, Cognito's
/// <c>AdminCreateUser</c>), whose own mail carries the temporary password;
/// then the mailer carries the CONTEXT, with no secret
/// (<see cref="MailTemplates.ClientInvitation"/>).</para>
/// <para><b>An address the directory already knows</b> is resolved through
/// OUR prior binding by email in this firm — a refiled case re-binds the same
/// person. One with no binding of ours is a 409, never a guess.</para>
/// <para><b>Order.</b> Role conflicts are planned BEFORE the account is
/// created, so the common refusal costs no account. What remains is the race
/// the store's transaction refuses, which leaves a pool user with no binding —
/// a state every portal route already answers 403 for.</para>
/// <para>Per CASE for now: #501 (portal access keyed to the
/// client) re-keys this; see that issue for the new shape.</para>
/// </remarks>
public sealed class PortalInvitationRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 4 * 1024;
    private const string Logger = "insolvia_api.api.routes.portal_invitations";
    private const string ClientNotFound = "client not found on this case";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/portal/clients", ListAsync).RequireAuth().Requires(Feature.ClientPortal, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/portal/invitation", InviteAsync).RequireAuth().Requires(Feature.ClientPortal, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/portal/clients/{subject}/resend", ResendAsync).RequireAuth().Requires(Feature.ClientPortal, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/portal/clients/{subject}", RevokeAsync).RequireAuth().Requires(Feature.ClientPortal, PermissionLevel.AddEdit);
    }

    // Everyone ever invited to this case's portal, revoked included — oldest first.
    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await VisibleCaseAsync(context, accessor, caseId, AccessAction.CaseRead, cancellationToken);
        await RecordAsync(context, accessor, caseId, AccessAction.CaseRead, null, cancellationToken);
        var clients = await context.Port<IClientBindingStore>().ListForCaseAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["clients"] = new JsonArray([.. clients.Select(binding => (JsonNode)PortalViews.Binding(binding))]) });
    }

    // {email, displayName, roles?} — roles defaults to [debtor_1]; both is the
    // firm's explicit choice of one login for both spouses, and the access-log
    // row records it.
    private static async Task<IResult> InviteAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await VisibleCaseAsync(context, accessor, caseId, AccessAction.ClientInvite, cancellationToken);
        var draft = ClientBindings.ParseInvitation(await context.BodyAsync(MaxRequestBytes));
        var bindings = context.Port<IClientBindingStore>();
        var clock = context.Clock();

        // PRE-FLIGHT, before an account exists, planned against a subject-less
        // draft that no existing binding can equal.
        var onCase = await bindings.ListForCaseAsync(caseId, cancellationToken);
        ClientBindings.PlanBinding(ClientBindings.CreateBinding(draft, accessor.FirmId, caseId, string.Empty, accessor.Subject, clock), previous: null, onCase);

        string subject;
        try
        {
            subject = await context.Port<IUserDirectory>().CreateUserAsync(draft.Email, cancellationToken);
        }
        catch (ConflictException)
        {
            var prior = await bindings.FindByEmailAsync(accessor.FirmId, draft.Email, cancellationToken)
                ?? throw new ConflictException("that email address already has an Insolvia account that is not one of your firm's clients");
            subject = prior.Subject;
        }

        var binding = ClientBindings.CreateBinding(draft, accessor.FirmId, caseId, subject, accessor.Subject, clock);
        var narrowed = ClientBindings.PlanBinding(binding, await bindings.GetAsync(accessor.FirmId, subject, cancellationToken), onCase);
        await bindings.BindAsync(binding, narrowed, cancellationToken);

        await RecordAsync(context, accessor, caseId, AccessAction.ClientInvite, binding.Roles, cancellationToken);
        await SendContextMailAsync(context, binding, accessor, cancellationToken);

        // No address, no subject, no name: that SOME client was invited is all a request log needs.
        context.Log(Logger, "portal client invited", ("narrowed", narrowed.Count));
        return FlaskJson.Result(PortalViews.Binding(binding), StatusCodes.Status201Created);
    }

    // Re-send an invitation not used yet. The directory re-sends its password
    // mail unless the account already chose a password (the refiled-case
    // shape — a ConflictException, swallowed): then only the context mail goes.
    private static async Task<IResult> ResendAsync(HttpContext context, string caseId, string subject, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await VisibleCaseAsync(context, accessor, caseId, AccessAction.ClientInvite, cancellationToken);
        var binding = await BindingOnCaseAsync(context, accessor, caseId, subject, cancellationToken);
        if (binding.Status != BindingStatus.Invited)
        {
            throw new ConflictException("only an invitation that has not been used yet can be re-sent");
        }

        try
        {
            await context.Port<IUserDirectory>().ResendInviteAsync(binding.Email, cancellationToken);
        }
        catch (ConflictException)
        {
            // A CONFIRMED account — a password of its own already.
        }

        await RecordAsync(context, accessor, caseId, AccessAction.ClientInvite, binding.Roles, cancellationToken);
        await SendContextMailAsync(context, binding, accessor, cancellationToken);
        context.Log(Logger, "portal invitation re-sent");
        return FlaskJson.NoContent();
    }

    // Withdraw access: the binding flips to revoked and its roles are
    // released; the client's next request answers 403 with a denied row. The
    // pool account survives — this service holds no AdminDeleteUser.
    private static async Task<IResult> RevokeAsync(HttpContext context, string caseId, string subject, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await VisibleCaseAsync(context, accessor, caseId, AccessAction.ClientRevoke, cancellationToken);
        var binding = await BindingOnCaseAsync(context, accessor, caseId, subject, cancellationToken);
        var written = await context.Port<IClientBindingStore>().UpdateAsync(ClientBindings.Revoke(binding, context.Clock()), cancellationToken)
            ?? throw new NotFoundException(ClientNotFound);

        await RecordAsync(context, accessor, caseId, AccessAction.ClientRevoke, written.Roles, cancellationToken);
        context.Log(Logger, "portal client revoked");
        return FlaskJson.Result(PortalViews.Binding(written));
    }

    // 404 unless the caller may see the case — the refusal recorded, because
    // walking case ids is what the log is for. An allowed read is not
    // recorded here: each route records its own verb.
    private static async Task VisibleCaseAsync(HttpContext context, Accessor accessor, string caseId, AccessAction action, CancellationToken cancellationToken)
    {
        if (await context.Cases().GetAsync(caseId, accessor, cancellationToken) is not null)
        {
            return;
        }

        await context.AccessLog().RecordAsync(
            AccessEvents.Record(accessor.Subject, action, caseId: caseId, outcome: AccessOutcome.Denied, clock: context.Clock()),
            cancellationToken);
        throw new NotFoundException(CaseRouteSupport.CaseNotFound);
    }

    // The binding for subject, if it is on THIS case of the caller's firm.
    // Any other answer is the same 404.
    private static async Task<ClientBinding> BindingOnCaseAsync(HttpContext context, Accessor accessor, string caseId, string subject, CancellationToken cancellationToken)
    {
        var binding = await context.Port<IClientBindingStore>().GetAsync(accessor.FirmId, subject, cancellationToken);
        return binding is null || binding.CaseId != caseId ? throw new NotFoundException(ClientNotFound) : binding;
    }

    private static Task RecordAsync(HttpContext context, Accessor accessor, string caseId, AccessAction action, IEnumerable<ClientRole>? roles, CancellationToken cancellationToken) =>
        context.AccessLog().RecordAsync(
            AccessEvents.Record(accessor.Subject, action, caseId: caseId, roles: roles?.Select(role => role.ToWire()), clock: context.Clock()),
            cancellationToken);

    // The secret-free half of the invitation. A failure here is logged, not
    // raised: the binding is written and the directory's own mail has gone,
    // so the client can already sign in — and the resend route sends this again.
    private static async Task SendContextMailAsync(HttpContext context, ClientBinding binding, Accessor accessor, CancellationToken cancellationToken)
    {
        var config = context.RequestServices.GetRequiredService<ApiConfig>();
        var email = MailTemplates.ClientInvitation(
            binding.Email,
            MailTemplates.LinksFor(config.MarketingOrigin),
            accessor.Firm.Name,
            config.AppOrigin.TrimEnd('/') + "/portal",
            binding.DisplayName);
        try
        {
            // Stable per binding and per write, so a retry of this one request
            // dedupes at the mailer. (A re-send does not rewrite the binding, so
            // it carries the invitation's key.)
            await context.Port<IMailer>().SendAsync(
                email,
                $"client-invitation:{binding.CaseId}:{binding.Subject}:{FlaskJson.Micros(binding.UpdatedAt)}",
                cancellationToken);
        }
#pragma warning disable CA1031 // The mail is best effort; the invitation already stands.
        catch (Exception)
#pragma warning restore CA1031
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Logger).Write(LogLevel.Warning, "client invitation mail not sent", null);
        }
    }
}
