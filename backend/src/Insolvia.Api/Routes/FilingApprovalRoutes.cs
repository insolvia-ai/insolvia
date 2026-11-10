using Insolvia.Api.Auth;
using Insolvia.Api.Filing;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.Auth;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Courts;
using Insolvia.Domain.Filings;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Forms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Insolvia.Api.Routes;

/// <summary>
/// The attorney's per-filing approval (ADR 0024, guardrail 1;
/// routes/filing_approval.py) under <c>/v1/cases/{caseId}/filing-approval</c>
/// — GET what would be approved (and the current approval, VOIDED if the
/// filing set changed), POST approve <c>{digest, credential_id?}</c> (201; the
/// one producer of a filing job), DELETE cancel a pending one — and
/// <c>POST /v1/cases/{caseId}/filings/{filingId}/resolution</c>, the
/// attorney's answer to a hand-back (ADR 0024 PR 8).
/// </summary>
/// <remarks>
/// <para><b>The gates, in order</b>: auth; <c>cases</c> view (the case resolved
/// under the caller's accessor, an undistinguishing 404, the read recorded);
/// <c>electronic_filing</c>; then, for POST, a sign-in older than
/// <see cref="FilingApprovals.SignInMaxAgeSeconds"/> (403
/// ReauthenticationRequired, here AND in the rule) and everything
/// <see cref="FilingApprovals.ApproveAsync"/> refuses.</para>
/// <para><b>What is shown is what is approved</b>: the client renders
/// <c>basis</c> and posts its digest back; the route recomputes the basis
/// from the stores and refuses a mismatch.</para>
/// <para>Successes are <c>Cache-Control: no-store</c> — an approval is an
/// instruction to file.</para>
/// </remarks>
public sealed class FilingApprovalRoutes : IEndpointModule
{
    // A digest and maybe a credential id.
    private const int MaxRequestBytes = 1024;
    private const string Logger = "insolvia_api.api.routes.filing_approval";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/filing-approval", ReadAsync).RequireAuth()
            .Requires(Feature.Cases, PermissionLevel.ViewOnly).Requires(Feature.ElectronicFiling, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/filing-approval", ApproveAsync).RequestBody<FilingApproveRequest>()
            .Produces<FilingSetNotReadyBody>(StatusCodes.Status409Conflict).Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable).RequireAuth()
            .Requires(Feature.Cases, PermissionLevel.ViewOnly).Requires(Feature.ElectronicFiling, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/filing-approval", CancelAsync).RequireAuth()
            .Requires(Feature.Cases, PermissionLevel.ViewOnly).Requires(Feature.ElectronicFiling, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/filings/{filingId}/resolution", ResolveAsync).RequestBody<FilingResolutionRequest>()
            .Produces<ResolutionRefusedBody>(StatusCodes.Status409Conflict).RequireAuth()
            .Requires(Feature.Cases, PermissionLevel.AddEdit).Requires(Feature.ElectronicFiling, PermissionLevel.AddEdit);
    }

    private static async Task<(Case Case, CaseData Data, ApprovalBasis Basis)> BasisAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        // Every port this module needs, checked before anything is read.
        _ = context.Port<IFilingApprovalStore>();
        _ = context.Port<IFilingStore>();
        var packets = context.Port<IPacketStore>();
        var @case = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var (data, basis) = await BasisOfAsync(context, @case, packets, cancellationToken);
        return (@case, data, basis);
    }

    private static Task<(CaseData Data, ApprovalBasis Basis)> BasisOfAsync(HttpContext context, Case @case, IPacketStore packets, CancellationToken cancellationToken) =>
        FilingApprovals.BasisForCaseAsync(
            @case,
            context.Port<IDebtorStore>(),
            context.Port<ICaseEntityStore>(),
            packets,
            context.Today(),
            context.RequestServices.GetService<ICourtRegistry>() as CourtRegistry,
            cancellationToken);

    private static async Task<FilingApprovalState> ViewAsync(HttpContext context, ApprovalBasis basis, FilingApproval? approval, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var filing = approval is null ? null : await context.Port<IFilingStore>().GetAsync(approval.CaseId, approval.FilingId, cancellationToken);
        return new FilingApprovalState(
            ApprovalBasisView.Of(basis),
            approval is null ? null : FilingApprovalView.Of(approval, now),
            filing is null ? null : FilingView.Of(filing));
    }

    /// <summary>What would be approved now, and the case's current approval and filing run.</summary>
    /// <remarks>An approval whose filing set has changed since is shown VOIDED.</remarks>
    internal static async Task<FilingApprovalState> ReadAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var (@case, _, basis) = await BasisAsync(context, caseId, cancellationToken);
        var now = context.Clock().GetUtcNow();
        var accessor = await context.CurrentAccessorAsync();
        var approval = await FilingApprovals.CurrentAsync(@case.Id, basis, accessor.Subject, now, context.Port<IFilingApprovalStore>(), context.AccessLog(), cancellationToken);
        return FilingCredentialRoutes.NoStore(context, await ViewAsync(context, basis, approval, now, cancellationToken));
    }

    /// <summary>Approve filing the case as shown, and queue the filing.</summary>
    /// <remarks>
    /// Post back the digest of the basis you were shown; a mismatch is refused.
    /// Needs a recent sign-in (403 <c>ReauthenticationRequired</c>). 409
    /// <c>FilingSetNotReady</c> lists what blocks it; 503 when filing is not
    /// configured or the queue refused the job (the approval is then voided).
    /// </remarks>
    internal static async Task<Results<Created<FilingApprovalState>, JsonHttpResult<FilingSetNotReadyBody>, JsonHttpResult<ErrorBody>>> ApproveAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var approvals = context.Port<IFilingApprovalStore>();
        var credentials = context.Port<IFilingCredentialStore>();
        var authorizations = context.Port<IFilingAuthorizationStore>();
        if (context.RequestServices.GetService<IFilingJobSender>() is not { } queue)
        {
            // The image ahead of the infra: refuse before anything is recorded.
            return FilingCredentialRoutes.NoStore(context, Unavailable("Filing is not configured in this environment."));
        }

        var payload = await RequestBodies.ReadSilentAsync(context, MaxRequestBytes);
        var (@case, data, basis) = await BasisAsync(context, caseId, cancellationToken);
        var accessor = await context.CurrentAccessorAsync();
        var now = context.Clock().GetUtcNow();
        try
        {
            // The route's own check, before the rule repeats it.
            context.RequireFreshSignIn(FilingApprovals.SignInMaxAgeSeconds);
        }
        catch (ReauthenticationRequiredException)
        {
            await context.AccessLog().RecordAsync(
                AccessEvents.Record(accessor.Subject, AccessAction.FilingApprove, caseId: @case.Id, outcome: AccessOutcome.Denied, purpose: "reauthentication_required", clock: context.Clock()),
                cancellationToken);
            throw;
        }

        FilingApproval approval;
        try
        {
            approval = await FilingApprovals.ApproveAsync(
                payload,
                data,
                basis,
                accessor.FirmId,
                accessor.Subject,
                accessor.User.Role,
                context.CurrentPrincipal().AuthenticatedAt,
                now,
                credentials,
                authorizations,
                approvals,
                context.Port<IFilingStore>(),
                queue,
                context.AccessLog(),
                cancellationToken);
        }
        catch (FilingSetNotReadyException notReady)
        {
            return TypedResults.Json(new FilingSetNotReadyBody("FilingSetNotReady", notReady.Message, [.. notReady.Blockers]), statusCode: StatusCodes.Status409Conflict);
        }
        catch (FilingQueueUnavailableException unavailable)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Logger).Write(LogLevel.Error, "filing queue send failed; the approval was voided", null);
            return Unavailable(unavailable.Message);
        }

        // Metadata only: that an approval happened, never who or which case.
        context.Log(Logger, "filing approved and queued");
        return FilingCredentialRoutes.NoStore(context, TypedResults.Created((string?)null, await ViewAsync(context, basis, approval, now, cancellationToken)));
    }

    /// <summary>Cancel the case's pending approval.</summary>
    internal static async Task<FilingApprovalState> CancelAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var (@case, _, basis) = await BasisAsync(context, caseId, cancellationToken);
        var now = context.Clock().GetUtcNow();
        var accessor = await context.CurrentAccessorAsync();
        var voided = await FilingApprovals.CancelAsync(@case.Id, accessor.Subject, now, context.Port<IFilingApprovalStore>(), context.AccessLog(), cancellationToken);
        context.Log(Logger, "filing approval cancelled");
        return FilingCredentialRoutes.NoStore(context, await ViewAsync(context, basis, voided, now, cancellationToken));
    }

    /// <summary>Resolve a handed-back filing: what the court's docket shows.</summary>
    /// <remarks>409 <c>ResolutionRefused</c>, with its reason, when the filing cannot be resolved that way.</remarks>
    internal static async Task<Results<Ok<FilingApprovalState>, JsonHttpResult<ResolutionRefusedBody>>> ResolveAsync(HttpContext context, string caseId, string filingId, CancellationToken cancellationToken)
    {
        var approvals = context.Port<IFilingApprovalStore>();
        var filings = context.Port<IFilingStore>();
        var packets = context.Port<IPacketStore>();
        var documents = context.Port<IDocumentStore>();
        var body = await RequestBodies.ReadSilentAsync(context, MaxRequestBytes);
        var @case = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var filing = await filings.GetAsync(@case.Id, filingId, cancellationToken) ?? throw new NotFoundException("filing not found");
        var request = FilingOutcomes.Parse(body);
        var accessor = await context.CurrentAccessorAsync();
        var now = context.Clock().GetUtcNow();
        try
        {
            await FilingOutcomes.ResolveAsync(request, filing, @case, accessor.Subject, now, filings, context.Cases(), documents, context.AccessLog(), cancellationToken);
        }
        catch (ResolutionRefusedException refused)
        {
            return TypedResults.Json(new ResolutionRefusedBody("ResolutionRefused", refused.Message, refused.Reason), statusCode: StatusCodes.Status409Conflict);
        }

        // Metadata only: that a resolution happened and which way, never who.
        context.Log(Logger, "filing resolved: " + request.Outcome.ToWire());
        var fresh = await context.Cases().GetAsync(@case.Id, accessor, cancellationToken) ?? @case;
        var (_, basis) = await BasisOfAsync(context, fresh, packets, cancellationToken);
        var approval = await FilingApprovals.CurrentAsync(fresh.Id, basis, accessor.Subject, now, approvals, context.AccessLog(), cancellationToken);
        return FilingCredentialRoutes.NoStore(context, TypedResults.Ok(await ViewAsync(context, basis, approval, now, cancellationToken)));
    }

    private static JsonHttpResult<ErrorBody> Unavailable(string message) =>
        TypedResults.Json(new ErrorBody("FilingQueueUnavailable", message, null), statusCode: StatusCodes.Status503ServiceUnavailable);
}
