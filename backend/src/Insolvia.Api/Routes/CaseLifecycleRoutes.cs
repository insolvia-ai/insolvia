using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.AccessLog;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.FirmClients;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Insolvia.Domain.TaxIds;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// The case lifecycle's own routes (routes/case_lifecycle.py, issue 14.3 /
/// #355, and ADR 0027): the history, archive, soft delete, Copy case, and a
/// conversion. Status moves themselves are the case PATCH (<see cref="CaseRoutes"/>).
/// Every route resolves the case through the access rule first, which answers
/// no case for a deleted one.
/// </summary>
/// <remarks>
/// <para><b>The history</b> is ADR 0027's one series of events:
/// <c>GET …/lifecycle</c> returns them all, oldest first. <c>GET …/status-history</c>
/// stays because the app's client still calls it (client.ts
/// <c>getCaseStatusHistory</c>); it is the same history's STATUS events in
/// the old <c>status_change_json</c> shape. Retire it with the client.</para>
/// <para><b>A conversion</b> (<c>POST …/conversions {toChapter, effectiveOn}</c>,
/// #509) goes through <see cref="CaseLifecycle.Convert"/>, which owns the
/// checks — filed only (409), a real chapter that changes (400 on
/// <c>toChapter</c>), a date no earlier than the petition or the previous
/// court event (400 on <c>effectiveOn</c>) — and records nothing else: a
/// conversion into 11 or 12 is a court fact and is recorded. It answers 201
/// with the case as written and the event.</para>
/// <para><b>Not here</b>: <c>POST …/confirmations</c>. #509 requires its
/// <c>planVersionId</c> to name a filed, unwithdrawn plan version of the
/// current Chapter 13 period, filed on or before the ruling — and plan
/// versions (#507) do not exist yet, so there is nothing to check it against.</para>
/// </remarks>
public sealed class CaseLifecycleRoutes : IEndpointModule
{
    private const int MaxRequestBytes = 64 * 1024;
    private const string Logger = "insolvia_api.api.routes.case_lifecycle";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/cases/{caseId}/status-history", StatusHistoryAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}/lifecycle", LifecycleAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/conversions", ConvertAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes)).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapPut("/v1/cases/{caseId}/archived", ArchiveAsync).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes)).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}", DeleteAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/copy", CopyAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit).Requires(Feature.Clients, PermissionLevel.ViewOnly);
    }

    /// <summary>A case's status moves.</summary>
    /// <remarks>
    /// The lifecycle history's status events, in the older shape the app still
    /// reads; <c>GET …/lifecycle</c> has every event. Not access-logged, like
    /// the assignee list: a fact about the handling.
    /// </remarks>
    internal static async Task<StatusHistory> StatusHistoryAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var history = await context.Cases().LifecycleAsync(caseId, cancellationToken);
        return new StatusHistory([.. history.Where(recorded => recorded.Kind == LifecycleEventKind.Status).Select(StatusChangeView.Of)]);
    }

    /// <summary>A case's lifecycle history, oldest first.</summary>
    internal static async Task<LifecycleList> LifecycleAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var history = await context.Cases().LifecycleAsync(caseId, cancellationToken);
        return new LifecycleList([.. history.OrderBy(recorded => recorded.RecordedAt).Select(LifecycleEventView.Of)]);
    }

    /// <summary>Record the court's conversion of a filed case to another chapter.</summary>
    /// <remarks>
    /// Filed cases only (409). The new chapter must differ from the current one
    /// (400 on <c>toChapter</c>), and the date may not precede the petition or
    /// the previous court event (400 on <c>effectiveOn</c>). The case's
    /// deadlines are not regenerated.
    /// </remarks>
    internal static async Task<Created<ConversionView>> ConvertAsync(HttpContext context, string caseId, ConversionRequest request, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var (toChapter, effectiveOn) = (request.ToChapter, request.EffectiveOn!.Value);
        var accessor = await context.CurrentAccessorAsync();

        var existing = await store.GetAsync(caseId, accessor, cancellationToken);
        Case? updated = null;
        LifecycleWrite? write = null;
        if (existing is not null)
        {
            write = CaseLifecycle.Convert(existing, await store.LifecycleAsync(caseId, cancellationToken), toChapter, effectiveOn, accessor.Subject, clock: context.Clock());
            updated = await store.RecordAsync(write, cancellationToken);
        }

        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseUpdate, updated is not null, cancellationToken);
        if (updated is null || write is null)
        {
            throw new NotFoundException(CaseRouteSupport.CaseNotFound);
        }

        context.Log(Logger, "case converted", ("case_id", caseId), ("to_chapter", toChapter));
        return TypedResults.Created((string?)null, new ConversionView(CaseView.Of(updated), LifecycleEventView.Of(write.Event)));
    }

    /// <summary>Archive or restore a case.</summary>
    /// <remarks>Idempotent, in any status; archiving an archived case keeps its first stamp.</remarks>
    internal static async Task<CaseView> ArchiveAsync(HttpContext context, string caseId, CaseArchiveRequest request, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var archived = request.Archived!.Value;
        var accessor = await context.CurrentAccessorAsync();

        var existing = await store.GetAsync(caseId, accessor, cancellationToken);
        var updated = existing is null
            ? null
            : await store.UpdateAsync(CaseRules.SetArchived(existing, archived, accessor.Subject, context.Clock()), existing.Status, cancellationToken);
        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseUpdate, updated is not null, cancellationToken);
        if (updated is null)
        {
            throw new NotFoundException(CaseRouteSupport.CaseNotFound);
        }

        context.Log(Logger, "case archive changed", ("case_id", caseId));
        return CaseView.Of(updated);
    }

    /// <summary>Delete a case.</summary>
    /// <remarks>A soft delete, by a firm admin only (403), and never of a filed case (409): its petition is a court record.</remarks>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var accessor = await context.CurrentAccessorAsync();
        var existing = await store.GetAsync(caseId, accessor, cancellationToken);
        if (existing is null)
        {
            await context.RecordCaseAccessAsync(caseId, AccessAction.CaseDelete, allowed: false, cancellationToken);
            throw new NotFoundException(CaseRouteSupport.CaseNotFound);
        }

        if (!accessor.User.IsAdmin)
        {
            throw new ForbiddenException("only a firm admin may delete a case");
        }

        if (CaseRules.DeletionRefusal(existing) is { } refusal)
        {
            throw new ConflictException(refusal);
        }

        var deleted = await store.UpdateAsync(CaseRules.MarkDeleted(existing, accessor.Subject, context.Clock()), existing.Status, cancellationToken);
        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseDelete, deleted is not null, cancellationToken);
        if (deleted is null)
        {
            throw new NotFoundException(CaseRouteSupport.CaseNotFound);
        }

        context.Log(Logger, "case deleted", ("case_id", caseId));
        return TypedResults.NoContent();
    }

    // A NEW case from this one's data, ALL OR NONE in one unit of work: the
    // case, its assignment and debtors first (the records' and sealed tax
    // ids' foreign keys need the case row), then the records and tax ids.
    // Python ordered the writes the other way, entities first, because
    // DynamoDB had no foreign keys and order was its transaction's
    // substitute; Postgres refuses that order.

    /// <summary>Open a new case from a copy of this one's data.</summary>
    /// <remarks>
    /// All or nothing: the debtors, their sealed tax ids and every record are
    /// copied. Debtor 1 must be linked to a client, and no linked client may be
    /// archived or merged (409). Any request body is ignored.
    /// </remarks>
    internal static async Task<Created<CaseView>> CopyAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var debtorStore = context.Port<IDebtorStore>();
        var entityStore = context.Port<ICaseEntityStore>();
        var taxIds = context.Port<ITaxIdStore>();
        var accessor = await context.CurrentAccessorAsync();

        var source = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var debtors = await debtorStore.ListForCaseAsync(source.Id, cancellationToken);
        await RefuseUncopyableClientsAsync(context, debtors, cancellationToken);
        var entities = new List<ICaseEntity>();
        foreach (var kind in CaseCollections.Kinds)
        {
            entities.AddRange(await entityStore.ListForCaseAsync(source.Id, kind, cancellationToken));
        }

        var copy = CaseCopies.Copy(source, accessor.Subject, debtors, entities, context.Clock());

        // The ciphertext copied as it is (its context names the firm and the
        // ref, never the case); a pointer whose item is missing is dropped.
        var copiedDebtors = new List<Debtor>();
        var sealedIds = new List<SealedTaxId>();
        foreach (var debtor in copy.Debtors)
        {
            var kept = debtor;
            if (debtor.TaxId is not null)
            {
                var sealedId = await taxIds.GetAsync(source.Id, debtor.TaxId.Ref, cancellationToken);
                if (sealedId is null)
                {
                    kept = debtor with
                    {
                        TaxId = null,
                        Provenance = debtor.Provenance.Where(pair => pair.Key != "taxId").ToValueDictionary(),
                    };
                }
                else
                {
                    sealedIds.Add(sealedId);
                }
            }

            copiedDebtors.Add(kept);
        }

        try
        {
            await context.Port<IUnitOfWork>().RunAsync(
                async token =>
                {
                    await store.CreateAsync(copy.Case, copy.Assignment, copiedDebtors, token);
                    foreach (var sealedId in sealedIds)
                    {
                        await taxIds.PutAsync(copy.Case.Id, sealedId, token);
                    }

                    foreach (var entity in copy.Entities)
                    {
                        await entityStore.CreateAsync(entity, token);
                    }
                },
                // A lost commit acknowledgement: the replay meets its own
                // case row. The copy's id is generated, so its presence is
                // this commit — the case store's own check, which a joined
                // call skips.
                landed: async token => await store.GetAsync(copy.Case.Id, accessor, token) is not null,
                cancellationToken);
        }
        catch (ClientUnavailableException refused)
        {
            var reread = await context.FirmStore().GetClientAsync(accessor.FirmId, refused.ClientId, cancellationToken);
            throw new ConflictException(FirmClientRules.RefusalForRefusedLink(reread), refused);
        }

        await context.RecordCaseAccessAsync(copy.Case.Id, AccessAction.CaseCreate, allowed: true, cancellationToken);
        if (copiedDebtors.Any(debtor => debtor.FilingRole is FilingRole.Debtor1 or FilingRole.Debtor2))
        {
            await CaseRoutes.StampFirstRetainedAsync(context, copy.Case.Id, cancellationToken);
        }

        context.Log(Logger, "case copied", ("case_id", copy.Case.Id), ("source_case_id", source.Id));
        return TypedResults.Created((string?)null, CaseView.Of(copy.Case));
    }

    // The copy obeys opening a case's rules: Debtor 1 names a client, and no
    // linked client is archived, merged or mid-merge. 409 — the SOURCE is in
    // a state that cannot be copied. Each client read is logged.
    private static async Task RefuseUncopyableClientsAsync(HttpContext context, IReadOnlyList<Debtor> debtors, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (!debtors.Any(debtor => debtor.FilingRole == FilingRole.Debtor1 && debtor.ClientId is not null))
        {
            throw new ConflictException("This case's Debtor 1 is not linked to a client, so a copy would be a case opened for nobody. Link the client first.");
        }

        foreach (var debtor in debtors)
        {
            if (debtor.ClientId is null)
            {
                continue;
            }

            var client = await context.FirmStore().GetClientAsync(accessor.FirmId, debtor.ClientId, cancellationToken);
            await context.RecordClientAccessAsync(debtor.ClientId, AccessAction.ClientRead, client is not null, cancellationToken);
            var refusal = client is null ? "This case names a client that no longer exists." : client.RefusalForNewCase();
            if (refusal is not null)
            {
                throw new ConflictException(refusal);
            }
        }
    }
}
