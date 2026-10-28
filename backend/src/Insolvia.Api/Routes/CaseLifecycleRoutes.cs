using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

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
/// <c>to_chapter</c>), a date no earlier than the petition or the previous
/// court event (400 on <c>effective_on</c>) — and records nothing else: a
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
        routes.MapPost("/v1/cases/{caseId}/conversions", ConvertAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapPut("/v1/cases/{caseId}/archived", ArchiveAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}", DeleteAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapPost("/v1/cases/{caseId}/copy", CopyAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit).Requires(Feature.Clients, PermissionLevel.ViewOnly);
    }

    // Not access-logged, like the assignee list: a fact about the handling.
    private static async Task<IResult> StatusHistoryAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var history = await context.Cases().LifecycleAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["history"] = new JsonArray([.. history.Where(recorded => recorded.Kind == LifecycleEventKind.Status).Select(recorded => (JsonNode)CaseViews.StatusChange(recorded))]),
        });
    }

    private static async Task<IResult> LifecycleAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var history = await context.Cases().LifecycleAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["events"] = new JsonArray([.. history.OrderBy(recorded => recorded.RecordedAt).Select(recorded => (JsonNode)CaseViews.LifecycleEvent(recorded))]),
        });
    }

    private static async Task<IResult> ConvertAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var (toChapter, effectiveOn) = ParseConversion(await context.BodyAsync(MaxRequestBytes));
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
        return FlaskJson.Result(
            new JsonObject { ["case"] = CaseViews.Case(updated), ["event"] = CaseViews.LifecycleEvent(write.Event) },
            StatusCodes.Status201Created);
    }

    // Archive or restore; idempotent, any status. Logged as the edit it is.
    private static async Task<IResult> ArchiveAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var archived = CaseRules.ParseArchive(await context.BodyAsync(MaxRequestBytes));
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
        return FlaskJson.Result(CaseViews.Case(updated));
    }

    // SOFT, a firm admin's act, and never a filed case (its petition is a court record).
    private static async Task<IResult> DeleteAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
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
        return FlaskJson.NoContent();
    }

    // A NEW case from this one's data. ORDER IS THE TRANSACTION'S SUBSTITUTE:
    // the entities and sealed tax ids land first under the new id, while no
    // case record reaches them; the case, its assignment and debtors last, in
    // the case store's one transaction. The body is ignored.
    private static async Task<IResult> CopyAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
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
                        Provenance = debtor.Provenance.Where(pair => pair.Key != "tax_id").ToValueDictionary(),
                    };
                }
                else
                {
                    await taxIds.PutAsync(copy.Case.Id, sealedId, cancellationToken);
                }
            }

            copiedDebtors.Add(kept);
        }

        foreach (var entity in copy.Entities)
        {
            await entityStore.CreateAsync(entity, cancellationToken);
        }

        try
        {
            await store.CreateAsync(copy.Case, copy.Assignment, copiedDebtors, cancellationToken);
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
        return FlaskJson.Result(CaseViews.Case(copy.Case), StatusCodes.Status201Created);
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

    // ADR 0027's wire: {toChapter, effectiveOn}. Field errors carry the
    // snake_case names the writer uses (to_chapter, effective_on), so one
    // key names one fault whichever check found it.
    private static (int ToChapter, DateOnly EffectiveOn) ParseConversion(JsonObject payload)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        int? toChapter = null;
        if (!Json.TryGetWholeNumber(payload["toChapter"], out var chapter))
        {
            errors["to_chapter"] = "Chapter must be a number.";
        }
        else if (!CaseRules.Chapters.Contains((int)Math.Clamp(chapter, int.MinValue, int.MaxValue)))
        {
            errors["to_chapter"] = "Chapter must be one of " + string.Join(", ", CaseRules.Chapters) + ".";
        }
        else
        {
            toChapter = (int)chapter;
        }

        DateOnly? effectiveOn = null;
        var raw = Json.AsString(payload["effectiveOn"]);
        var stripped = raw is null ? string.Empty : PyText.Strip(raw);
        if (DateOnly.TryParseExact(stripped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && FlaskJson.Date(parsed) == stripped)
        {
            effectiveOn = parsed;
        }
        else
        {
            errors["effective_on"] = "Must be a date in YYYY-MM-DD form.";
        }

        if (errors.Count > 0 || toChapter is null || effectiveOn is null)
        {
            throw new FieldValidationException(errors);
        }

        return (toChapter.Value, effectiveOn.Value);
    }
}
