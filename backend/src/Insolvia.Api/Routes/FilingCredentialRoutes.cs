using System.Text.RegularExpressions;
using Insolvia.Api.Auth;
using Insolvia.Api.Filing;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.FilingAuthorizations;
using Insolvia.Domain.FilingCredentials;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// The attorney's own CM/ECF credential and written authorization (ADR 0024,
/// guardrails 2 and 3; routes/filing_credentials.py):
/// <c>/v1/me/filing-credentials</c> (status, enrol, revoke) and
/// <c>/v1/me/filing-authorization</c> (read, sign, withdraw).
/// </summary>
/// <remarks>
/// <para><b>Under <c>/v1/me</c>, and that is the authorization.</b> No attorney
/// id in any URL or body: the owner is the token's subject, so another
/// attorney's credential id is simply not found (404).</para>
/// <para><b>Gated by <c>electronic_filing</c></b>, hidden for every role by
/// default — an admin grants it per attorney.</para>
/// <para><b>The API seals and cannot open.</b> The composition holds an
/// <see cref="IFilingCredentialSealer"/> and no opener; nothing returned
/// carries the password, the seed or the envelope
/// (<see cref="FilingCredentialView"/> is the only view of a credential).</para>
/// <para><b>No credential without a signed authorization</b>: enrolment is a
/// 403 until the caller signed the current text, and withdrawing the
/// signature destroys every credential. Signing needs a fresh sign-in;
/// withdrawing does not, and is gated at view-only — it only takes authority away.</para>
/// <para>Every success is <c>Cache-Control: no-store</c>: the enrolment body
/// is the one place a TOTP seed crosses a client.</para>
/// </remarks>
public sealed partial class FilingCredentialRoutes : IEndpointModule
{
    // A login, a password and a seed: the cap refuses anything else first.
    private const int MaxRequestBytes = 4 * 1024;
    private const string Logger = "insolvia_api.api.routes.filing_credentials";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/me/filing-credentials", ListAsync).RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/me/filing-credentials", EnrolAsync).RequestBody<FilingEnrolmentRequest>().RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/me/filing-credentials/{credentialId}", RevokeAsync).RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.AddEdit);
        routes.MapGet("/v1/me/filing-authorization", ReadAuthorizationAsync).RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/me/filing-authorization", SignAsync).RequestBody<AuthorizationSignRequest>().RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/me/filing-authorization", WithdrawAsync).RequireAuth().Requires(Feature.ElectronicFiling, PermissionLevel.ViewOnly);
    }

    /// <summary>Marks the response <c>Cache-Control: no-store</c> and passes <paramref name="result"/> through.</summary>
    internal static T NoStore<T>(HttpContext context, T result)
    {
        context.Response.Headers.CacheControl = "no-store";
        return result;
    }

    /// <summary>List my CM/ECF credentials.</summary>
    /// <remarks>The caller's own credentials, by status only — never a password or an authenticator key.</remarks>
    internal static async Task<FilingCredentialList> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.Port<IFilingCredentialStore>();
        var accessor = await context.CurrentAccessorAsync();
        var found = await FilingCredentialRules.ListAsync(accessor.FirmId, accessor.Subject, store, cancellationToken);
        return NoStore(context, new FilingCredentialList([.. found.Select(FilingCredentialView.Of)]));
    }

    /// <summary>Enrol a CM/ECF credential.</summary>
    /// <remarks>
    /// The password and authenticator key are sealed on arrival and never
    /// returned. A 403 until the caller has signed the current authorization text.
    /// </remarks>
    internal static async Task<Created<FilingCredentialView>> EnrolAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.Port<IFilingCredentialStore>();
        var sealer = context.Port<IFilingCredentialSealer>();
        var accessor = await context.CurrentAccessorAsync();
        var enrolment = FilingCredentialRules.ParseEnrolment(await context.BodyAsync(MaxRequestBytes), context.RequestServices.GetService<ICourtRegistry>());
        var credential = await FilingCredentialRules.EnrolAsync(
            enrolment, accessor.FirmId, accessor.Subject, sealer, store, context.AccessLog(), context.Port<IFilingAuthorizationStore>(), context.Clock(), cancellationToken);

        // Metadata only: that an enrolment happened, never the login or the subject.
        context.Log(Logger, "filing credential enrolled");
        return NoStore(context, TypedResults.Created((string?)null, FilingCredentialView.Of(credential)));
    }

    /// <summary>Revoke a CM/ECF credential.</summary>
    /// <remarks>Destroys one of the caller's credentials. Another attorney's credential is a 404, the same as one that never existed.</remarks>
    internal static async Task<NoContent> RevokeAsync(HttpContext context, string credentialId, CancellationToken cancellationToken)
    {
        var store = context.Port<IFilingCredentialStore>();
        var accessor = await context.CurrentAccessorAsync();
        if (!CredentialId().IsMatch(credentialId)
            || !await FilingCredentialRules.RevokeAsync(credentialId, accessor.FirmId, accessor.Subject, store, context.AccessLog(), context.Clock(), cancellationToken))
        {
            throw new NotFoundException("filing credential not found");
        }

        context.Log(Logger, "filing credential revoked");
        return NoStore(context, TypedResults.NoContent());
    }

    /// <summary>Read my filing authorization.</summary>
    /// <remarks>The caller's signature on record (in force or stale), and the text a new signature would be over.</remarks>
    internal static async Task<FilingAuthorizationView> ReadAuthorizationAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var current = await FilingAuthorizationRules.CurrentAsync(accessor.FirmId, accessor.Subject, context.Port<IFilingAuthorizationStore>(), cancellationToken);
        return NoStore(context, FilingAuthorizationView.Of(current, accessor.FirmId, accessor.Subject));
    }

    /// <summary>Sign the filing authorization.</summary>
    /// <remarks>Signs the current authorization text; the version and digest posted must be the current text's. Needs a recent sign-in (403 <c>ReauthenticationRequired</c> otherwise).</remarks>
    internal static async Task<Created<FilingAuthorizationView>> SignAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessLog = context.AccessLog();
        var accessor = await context.CurrentAccessorAsync();
        var fresh = context.RequireFreshSignIn(FilingAuthorizationRules.SignatureMaxAgeSeconds);
        var signed = await FilingAuthorizationRules.SignAsync(
            await context.BodyAsync(MaxRequestBytes),
            accessor.FirmId,
            accessor.Subject,
            fresh,
            context.Clock().GetUtcNow(),
            context.Port<IFilingAuthorizationStore>(),
            accessLog,
            context.Clock(),
            cancellationToken);
        context.Log(Logger, "filing authorization signed");
        return NoStore(context, TypedResults.Created((string?)null, FilingAuthorizationView.Of(signed, accessor.FirmId, accessor.Subject)));
    }

    /// <summary>Withdraw my filing authorization.</summary>
    /// <remarks>Destroys every credential enrolled under it, and answers how many.</remarks>
    internal static async Task<AuthorizationWithdrawn> WithdrawAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var destroyed = await FilingCredentialRules.WithdrawAuthorizationAsync(
            accessor.FirmId, accessor.Subject, context.Port<IFilingAuthorizationStore>(), context.Port<IFilingCredentialStore>(), context.AccessLog(), context.Clock(), cancellationToken);
        context.Log(Logger, "filing authorization withdrawn");
        return NoStore(context, new AuthorizationWithdrawn(destroyed));
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex CredentialId();
}
