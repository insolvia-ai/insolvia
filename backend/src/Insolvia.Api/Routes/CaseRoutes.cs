using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Calendar;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Petitions;
using Insolvia.Core.Ports;
using Insolvia.Engines.Exemptions;

namespace Insolvia.Api.Routes;

/// <summary>
/// The case itself (services/api routes/cases.py, courts.py, and
/// firm_clients.py's <c>GET …/clients/{id}/cases</c>): open, list, read and
/// edit a case; who is on it; the client's cases; the court registry.
/// </summary>
/// <remarks>
/// <para><b>A status move is a lifecycle event</b> (ADR 0027): the PATCH
/// applies the changes (<see cref="CaseRules.ApplyChanges"/>), asks
/// <see cref="CaseLifecycle.StatusMove"/> for the event against the history
/// read now, and writes case and event in one transaction
/// (<see cref="ICaseStore.RecordAsync"/>); a PATCH that moves no status is an
/// edit (<see cref="ICaseStore.UpdateAsync"/>), conditioned on the status read.</para>
/// <para><b>A filed case's chapter changes only by a conversion</b>
/// (<c>POST …/conversions</c>) — a different <c>chapter</c> in its PATCH is
/// a 409 (ADR 0027 row 3, #509). Its <c>filed_at</c> stays EDITABLE:
/// a mistyped petition date must be correctable, and the filed event
/// keeps the date it was recorded with (a correction is not a court event).</para>
/// <para><b>The engines' two hooks</b> (Insolvia.Engines): the 106C opt-out
/// refusal before the write, and the deadline regeneration after it
/// (<see cref="CaseDeadlines"/>). A conversion does NOT regenerate: Rule
/// 1019's reset deadlines count from the conversion date, an anchor the
/// rule table does not have yet (#509), and regenerating from the petition
/// date under the new chapter would write dates that are wrong.</para>
/// </remarks>
public sealed class CaseRoutes : IEndpointModule
{
    // A case record is a handful of short fields.
    private const int MaxRequestBytes = 64 * 1024;
    private const string Logger = "insolvia_api.api.routes.cases";

    // client_ids[0] is Debtor 1, client_ids[1] Debtor 2 — B101's order.
    private static readonly FilingRole[] OpeningRoles = [FilingRole.Debtor1, FilingRole.Debtor2];

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/v1/cases", CreateAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit).Requires(Feature.Clients, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases", ListAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/cases/{caseId}", GetAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapPatch("/v1/cases/{caseId}", UpdateAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/assignees", ListAssigneesAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/assignees/{subject}", AssignAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/cases/{caseId}/assignees/{subject}", UnassignAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/clients/{clientId}/cases", ListClientCasesAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.ViewOnly).Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/courts", ListCourtsAsync).RequireAuth();
    }

    /// <summary>
    /// The RETAINED TRANSITION (ADR 0022, #355): each filing debtor's client
    /// leaves the prospect funnel — <c>first_retained_at</c> today if unset, the
    /// stage removed — in the firm store's one conditional write. After the
    /// case write, whatever the caller's <c>clients</c> grant, each stamp
    /// access-logged as the <c>client.update</c> it is.
    /// </summary>
    internal static async Task StampFirstRetainedAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var firms = context.FirmStore();
        var today = context.Today();
        foreach (var debtor in await context.Port<IDebtorStore>().ListForCaseAsync(caseId, cancellationToken))
        {
            if (debtor.FilingRole is not (FilingRole.Debtor1 or FilingRole.Debtor2) || debtor.ClientId is null)
            {
                continue;
            }

            var client = await firms.GetClientAsync(accessor.FirmId, debtor.ClientId, cancellationToken);
            if (client is null || (client.FirstRetainedAt is not null && client.ProspectStage is null))
            {
                continue;
            }

            var written = await firms.MarkClientRetainedAsync(accessor.FirmId, client.Id, today, cancellationToken);
            await context.RecordClientAccessAsync(client.Id, AccessAction.ClientUpdate, written is not null, cancellationToken);
        }
    }

    // The firm comes from the accessor, never the body. The creator is linked
    // and the debtors copied from the clients in the case's own transaction.
    private static async Task<IResult> CreateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var draft = CaseRules.ParseCreation(await context.BodyAsync(MaxRequestBytes));
        var accessor = await context.CurrentAccessorAsync();
        var clients = await ClientsToOpenForAsync(context, draft.ClientIds, cancellationToken);
        var clock = context.Clock();

        var (created, assignment) = CaseRules.Create(draft, accessor.FirmId, accessor.Subject, clock);
        var debtors = clients.Select((client, index) => ClientCopies.DebtorFromClient(client, created, OpeningRoles[index], clock)).ToList();
        try
        {
            await store.CreateAsync(created, assignment, debtors, cancellationToken);
        }
        catch (ClientUnavailableException refused)
        {
            // A merge claimed (or someone archived) the client after the read.
            var reread = await context.FirmStore().GetClientAsync(accessor.FirmId, refused.ClientId, cancellationToken);
            throw new FieldValidationException(new Dictionary<string, string> { ["client_ids"] = FirmClientRules.RefusalForRefusedLink(reread) });
        }

        await context.RecordCaseAccessAsync(created.Id, AccessAction.CaseCreate, allowed: true, cancellationToken);
        await StampFirstRetainedAsync(context, created.Id, cancellationToken);

        // GLBA: the case id and nothing about its contents; never the firm id.
        context.Log(Logger, "case created", ("case_id", created.Id));
        return FlaskJson.Result(CaseViews.Case(created), StatusCodes.Status201Created);
    }

    // Each client resolved IN THE CALLER'S FIRM (another firm's and an unknown
    // id answer alike), each read logged; archived and merged are refused.
    private static async Task<List<FirmClient>> ClientsToOpenForAsync(HttpContext context, IReadOnlyList<string> clientIds, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var firms = context.FirmStore();
        var clients = new List<FirmClient>();
        foreach (var clientId in clientIds)
        {
            var client = await firms.GetClientAsync(accessor.FirmId, clientId, cancellationToken);
            await context.RecordClientAccessAsync(clientId, AccessAction.ClientRead, client is not null, cancellationToken);
            if (client is null)
            {
                throw new FieldValidationException(new Dictionary<string, string> { ["client_ids"] = "No such client." });
            }

            if (client.RefusalForNewCase() is { } refused)
            {
                throw new FieldValidationException(new Dictionary<string, string> { ["client_ids"] = refused });
            }

            clients.Add(client);
        }

        return clients;
    }

    // NOT access-logged: the log is keyed by case, and a list touches none in particular.
    private static async Task<IResult> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var limit = CaseRules.ParseListLimit(Query(context, "limit"));
        var cursor = Query(context, "cursor") is { Length: > 0 } given ? given : null;
        var archived = ParseArchivedView(Query(context, "archived"));
        var page = await context.Cases().ListForAccessorAsync(await context.CurrentAccessorAsync(), limit, cursor, archived, cancellationToken);

        var body = new JsonObject { ["cases"] = new JsonArray([.. page.Cases.Select(found => (JsonNode)CaseViews.Case(found))]) };

        // Absent rather than null when there is no next page — the contract distinguishes them.
        if (page.NextCursor is not null)
        {
            body["nextCursor"] = page.NextCursor;
        }

        return FlaskJson.Result(body);
    }

    private static async Task<IResult> GetAsync(HttpContext context, string caseId, CancellationToken cancellationToken) =>
        FlaskJson.Result(CaseViews.Case(await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken)));

    private static async Task<IResult> UpdateAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var store = context.Cases();
        var changes = CaseRules.ParseUpdate(await context.BodyAsync(MaxRequestBytes));
        var accessor = await context.CurrentAccessorAsync();

        var existing = await store.GetAsync(caseId, accessor, cancellationToken);
        Case? updated = null;
        if (existing is not null)
        {
            if (CaseRules.IsFiled(existing.Status) && changes.Chapter is { } chapter && chapter != existing.Chapter)
            {
                throw new ConflictException("A filed case's chapter changes only by a conversion — record the conversion instead.");
            }

            await RefuseForbiddenElectionAsync(context, caseId, changes, cancellationToken);

            // Refuses a move off the map (409) or a filing without its docket facts (400).
            var changed = CaseRules.ApplyChanges(existing, changes, context.Clock());
            var move = CaseLifecycle.StatusMove(existing, changed, await store.LifecycleAsync(caseId, cancellationToken), accessor.Subject, changes.EffectiveOn);
            updated = move is null
                ? await store.UpdateAsync(changed, existing.Status, cancellationToken)
                : await store.RecordAsync(move, cancellationToken);
        }

        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseUpdate, updated is not null, cancellationToken);
        if (updated is null)
        {
            throw new NotFoundException(CaseRouteSupport.CaseNotFound);
        }

        // The deadline engine's hook: a filed date, a § 341 date or a chapter
        // change rewrites the generated events — after the case write, so a
        // failure leaves the case right and the calendar a request behind.
        if (changes.TouchesDeadlineAnchors)
        {
            await CaseDeadlines.RegenerateAsync(context, updated, accessor.Subject, cancellationToken);
        }

        context.Log(Logger, "case updated", ("case_id", updated.Id));
        return FlaskJson.Result(CaseViews.Case(updated));
    }

    // 106C line 1's opt-out rule at the write (#346): an opted-out state's
    // debtor cannot elect § 522(d). The state is Debtor 1's residence; the
    // registry resolves as of the expected filing date, else today. A case
    // that cannot yet say its state is not refused.
    private static async Task RefuseForbiddenElectionAsync(HttpContext context, string caseId, CaseChanges changes, CancellationToken cancellationToken)
    {
        if (changes.ExemptionSet is not { } election)
        {
            return;
        }

        var debtors = await context.Port<IDebtorStore>().ListForCaseAsync(caseId, cancellationToken);
        var state = debtors.FirstOrDefault(debtor => debtor.FilingRole == FilingRole.Debtor1)?.Body.ResidenceAddress.State;
        var petitions = await context.Port<ICaseEntityStore>().ListTypedAsync(caseId, PetitionRules.Petition, cancellationToken);
        var (asOf, _) = ExemptionAnalyzer.ResolutionDate(petitions.Count > 0 ? petitions[0].Body : null, context.Today());
        var refusal = ExemptionAnalyzer.ElectionRefusal(
            election.ToWire(),
            state is null || TextRules.Strip(state).Length == 0 ? null : TextRules.Strip(state).ToUpperInvariant(),
            asOf);
        if (refusal is not null)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["exemption_set"] = refusal });
        }
    }

    // Subjects, not names — the directory turns one into a person.
    private static async Task<IResult> ListAssigneesAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        var assignees = await context.Cases().AssigneesAsync(caseId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["assignees"] = new JsonArray([.. assignees.Select(assignment => (JsonNode)CaseViews.Assignee(assignment))]) });
    }

    // PUT: idempotent. The subject must be ACTIVE in the caller's firm — a
    // 404 otherwise, so this is no probe for who works where.
    private static async Task<IResult> AssignAsync(HttpContext context, string caseId, string subject, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var target = await context.VisibleCaseAsync(caseId, cancellationToken);
        var colleague = await context.FirmStore().GetUserAsync(accessor.FirmId, subject, cancellationToken);
        if (colleague is null || colleague.Status != FirmUserStatus.Active)
        {
            throw new NotFoundException("firm user not found");
        }

        await context.Cases().AssignAsync(CaseRules.Assign(target, subject, accessor.Subject, context.Clock()), cancellationToken);
        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseUpdate, allowed: true, cancellationToken);
        context.Log(Logger, "case assignee added", ("case_id", caseId));
        return FlaskJson.NoContent();
    }

    // Unlinking the last person is allowed: the firm's admins still reach the case.
    private static async Task<IResult> UnassignAsync(HttpContext context, string caseId, string subject, CancellationToken cancellationToken)
    {
        await context.VisibleCaseAsync(caseId, cancellationToken);
        if (!await context.Cases().UnassignAsync(caseId, subject, cancellationToken))
        {
            throw new NotFoundException("assignee not found");
        }

        await context.RecordCaseAccessAsync(caseId, AccessAction.CaseUpdate, allowed: true, cancellationToken);
        context.Log(Logger, "case assignee removed", ("case_id", caseId));
        return FlaskJson.NoContent();
    }

    // The client's cases THE CALLER MAY SEE, newest first, with the client's
    // role on each; the rest leave no trace. The client read is logged.
    private static async Task<IResult> ListClientCasesAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var client = await context.FirmStore().GetClientAsync(accessor.FirmId, clientId, cancellationToken);
        await context.RecordClientAccessAsync(clientId, AccessAction.ClientRead, client is not null, cancellationToken);
        if (client is null)
        {
            throw new NotFoundException("client not found");
        }

        var entries = await context.Cases().ListForClientAsync(client.Id, accessor, cancellationToken);
        return FlaskJson.Result(new JsonObject
        {
            ["cases"] = new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
            {
                ["filing_role"] = entry.FilingRole.ToWire(),
                ["case"] = CaseViews.Case(entry.Case),
            })]),
        });
    }

    // Authenticated and IN A FIRM (current_accessor), no feature: every
    // member picks a court when opening a case.
    private static async Task<IResult> ListCourtsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await context.CurrentAccessorAsync();
        return FlaskJson.Result(CaseViews.Courts(CourtRegistry.Shipped.Current));
    }

    private static bool ParseArchivedView(string? raw) => raw switch
    {
        null or "" or "false" => false,
        "true" => true,
        _ => throw new ValidationException("archived must be true or false"),
    };

    // Flask's request.args.get: the first value, or null.
    internal static string? Query(HttpContext context, string name)
    {
        var values = context.Request.Query[name];
        return values.Count > 0 ? values[0] : null;
    }
}
