using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;

namespace Insolvia.Api.Routes;

/// <summary>
/// The debtors of a case (routes/debtors.py; issue 8.5, ADR 0022): one record
/// per filing role, a whole-record PUT, the client link, the copy from and to
/// the client record. Gated by <c>intake</c>; the client half also by <c>clients</c>.
/// </summary>
/// <remarks>
/// <para><b>Debtor 1 and Debtor 2 are clients' copies</b>: their record is
/// created by linking a client (<c>PUT …/client</c>), never by typing one —
/// a PUT to an empty client role is a 400 on <c>client_id</c>. The non-filing
/// spouse may be typed.</para>
/// <para><b>The tax id</b> is sealed on the way in (<see cref="TaxIdRules.StoreAsync"/>)
/// and only its last four ever leave (<see cref="CaseViews.TaxId"/>).</para>
/// <para><b><c>differs_from_client</c></b> is computed only for a caller who
/// may see the client directory: a caller without <c>clients</c> is not told
/// how a client record reads.</para>
/// </remarks>
public sealed class DebtorRoutes : IEndpointModule
{
    // A debtor body with its other names, addresses and provenance.
    private const int MaxRequestBytes = 256 * 1024;
    private const string Logger = "insolvia_api.api.routes.debtors";
    private const string ClientGone = "This debtor's client is no longer in the firm.";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPut("/v1/cases/{caseId}/debtors/{filingRole}", PutAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit);
        routes.MapGet("/v1/cases/{caseId}/debtors", ListAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/cases/{caseId}/debtors/{filingRole}/client", LinkClientAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit).Requires(Feature.Clients, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/debtors/{filingRole}/copy-from-client", CopyFromClientAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.AddEdit).Requires(Feature.Clients, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/cases/{caseId}/debtors/{filingRole}/copy-to-client", CopyToClientAsync).RequireAuth().Requires(Feature.Intake, PermissionLevel.ViewOnly).Requires(Feature.Clients, PermissionLevel.AddEdit);
    }

    private static async Task<IResult> PutAsync(HttpContext context, string caseId, string filingRole, CancellationToken cancellationToken)
    {
        var debtors = context.Port<IDebtorStore>();
        var accessor = await context.CurrentAccessorAsync();
        var clock = context.Clock();

        var role = DebtorRules.ParseFilingRole(JsonValue.Create(filingRole));
        var draft = DebtorRules.Parse(await context.BodyAsync(MaxRequestBytes));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);

        var stored = await debtors.GetAsync(caseId, role, cancellationToken);
        DebtorRules.RequireClientProvenanceKept(draft, stored);
        if (stored is null && role is FilingRole.Debtor1 or FilingRole.Debtor2)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = "Link a client to this debtor before entering their details." });
        }

        if (stored is null)
        {
            var fresh = DebtorRules.Create(draft, caseId, role, await ResolveTaxIdAsync(context, draft, null, target, cancellationToken), clock: clock);
            if (await debtors.CreateAsync(fresh, cancellationToken))
            {
                LogSaved(context, caseId, fresh);
                return FlaskJson.Result(await ViewAsync(context, accessor, target, fresh, cancellationToken: cancellationToken), StatusCodes.Status201Created);
            }

            // Lost the race for the role: write over the winner instead.
            stored = await debtors.GetAsync(caseId, role, cancellationToken)
                ?? throw new InvalidOperationException("debtor vanished between a refused create and a read");
        }

        var debtor = DebtorRules.Replace(stored, draft, await ResolveTaxIdAsync(context, draft, stored, target, cancellationToken), clock);
        await debtors.PutAsync(debtor, cancellationToken);
        LogSaved(context, caseId, debtor);
        return FlaskJson.Result(await ViewAsync(context, accessor, target, debtor, cancellationToken: cancellationToken));
    }

    private static async Task<IResult> ListAsync(HttpContext context, string caseId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var views = new JsonArray();
        foreach (var debtor in await context.Port<IDebtorStore>().ListForCaseAsync(caseId, cancellationToken))
        {
            views.Add(await ViewAsync(context, accessor, target, debtor, cancellationToken: cancellationToken));
        }

        return FlaskJson.Result(new JsonObject { ["debtors"] = views });
    }

    // Link a client to a role: creates the record as a copy of the client
    // when the role is empty, else links the existing record. One client, one
    // role per case; the client's row must still be linkable — all in the
    // store's one conditional write.
    private static async Task<IResult> LinkClientAsync(HttpContext context, string caseId, string filingRole, CancellationToken cancellationToken)
    {
        var debtors = context.Port<IDebtorStore>();
        var accessor = await context.CurrentAccessorAsync();
        var clock = context.Clock();
        var role = DebtorRules.ParseFilingRole(JsonValue.Create(filingRole));
        var clientId = ClientIdOf(await context.BodyAsync(MaxRequestBytes));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        var client = await LinkableClientAsync(context, target, clientId, cancellationToken);

        var stored = await debtors.GetAsync(caseId, role, cancellationToken);
        if (stored is null)
        {
            var fresh = ClientCopies.DebtorFromClient(client, target, role, clock);
            var outcome = await debtors.LinkAsync(fresh, create: true, target.FirmId, cancellationToken);
            if (outcome == LinkOutcome.Written)
            {
                LogSaved(context, caseId, fresh);
                return FlaskJson.Result(await ViewAsync(context, accessor, target, fresh, client, cancellationToken), StatusCodes.Status201Created);
            }

            await RefuseRefusedLinkAsync(context, outcome, target, client.Id, cancellationToken);
            stored = await debtors.GetAsync(caseId, role, cancellationToken)
                ?? throw new InvalidOperationException("debtor vanished between a refused create and a read");
        }

        var linked = DebtorRules.LinkClient(stored, client.Id, target.CreatedAt, clock);
        await RefuseRefusedLinkAsync(context, await debtors.LinkAsync(linked, create: false, target.FirmId, cancellationToken), target, client.Id, cancellationToken);
        LogSaved(context, caseId, linked);
        return FlaskJson.Result(await ViewAsync(context, accessor, target, linked, client, cancellationToken));
    }

    // Re-copy the client's identity over this debtor. Refused once filed —
    // that would change a filed petition; amend it instead.
    private static async Task<IResult> CopyFromClientAsync(HttpContext context, string caseId, string filingRole, CancellationToken cancellationToken)
    {
        var debtors = context.Port<IDebtorStore>();
        var accessor = await context.CurrentAccessorAsync();
        var role = DebtorRules.ParseFilingRole(JsonValue.Create(filingRole));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseUpdate, cancellationToken);
        if (CaseRules.IsFiled(target.Status))
        {
            throw new ConflictException("This case is filed — re-copying would change a filed petition. Amend it instead.");
        }

        var stored = await LinkedDebtorAsync(debtors, caseId, role, cancellationToken);
        var client = await LinkableClientAsync(context, target, stored.ClientId!, cancellationToken);
        var recopied = ClientCopies.RecopyFromClient(stored, client, context.Clock());
        await debtors.PutAsync(recopied, cancellationToken);
        LogSaved(context, caseId, recopied);
        return FlaskJson.Result(await ViewAsync(context, accessor, target, recopied, client, cancellationToken));
    }

    // Write this debtor's identity BACK to the client record — a client edit,
    // hence clients:add_edit, and only a read of the case.
    private static async Task<IResult> CopyToClientAsync(HttpContext context, string caseId, string filingRole, CancellationToken cancellationToken)
    {
        var debtors = context.Port<IDebtorStore>();
        var accessor = await context.CurrentAccessorAsync();
        var role = DebtorRules.ParseFilingRole(JsonValue.Create(filingRole));
        var target = await context.ReachableCaseAsync(caseId, AccessAction.CaseRead, cancellationToken);
        var stored = await LinkedDebtorAsync(debtors, caseId, role, cancellationToken);
        var firms = context.FirmStore();
        var client = await firms.GetClientAsync(target.FirmId, stored.ClientId!, cancellationToken);
        if (client is null)
        {
            await context.RecordClientAccessAsync(stored.ClientId!, AccessAction.ClientUpdate, allowed: false, cancellationToken);
            throw new ConflictException(ClientGone);
        }

        var written = await firms.UpdateClientAsync(ClientCopies.ClientUpdatedFromDebtor(client, stored, context.Clock()), cancellationToken);
        await context.RecordClientAccessAsync(client.Id, AccessAction.ClientUpdate, written is not null, cancellationToken);
        if (written is null)
        {
            throw new ConflictException(ClientGone);
        }

        context.Log(Logger, "client updated from case", ("case_id", caseId));
        return FlaskJson.Result(await ViewAsync(context, accessor, target, stored, written, cancellationToken));
    }

    private static async Task<Debtor> LinkedDebtorAsync(IDebtorStore debtors, string caseId, FilingRole role, CancellationToken cancellationToken)
    {
        var stored = await debtors.GetAsync(caseId, role, cancellationToken);
        return stored is null || stored.ClientId is null
            ? throw new ConflictException("This debtor is not linked to a client.")
            : stored;
    }

    // The client, IN THE CASE'S FIRM, logged as client.read; absent or
    // archived/merged is a 400 on client_id.
    private static async Task<FirmClient> LinkableClientAsync(HttpContext context, Case target, string clientId, CancellationToken cancellationToken)
    {
        var client = await context.FirmStore().GetClientAsync(target.FirmId, clientId, cancellationToken);
        await context.RecordClientAccessAsync(clientId, AccessAction.ClientRead, client is not null, cancellationToken);
        if (client is null)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = "No such client." });
        }

        return client.RefusalForNewCase() is { } refused
            ? throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = refused })
            : client;
    }

    private static async Task RefuseRefusedLinkAsync(HttpContext context, LinkOutcome outcome, Case target, string clientId, CancellationToken cancellationToken)
    {
        if (outcome == LinkOutcome.ClientTaken)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = "That client is already another debtor on this case." });
        }

        if (outcome == LinkOutcome.ClientUnavailable)
        {
            var reread = await context.FirmStore().GetClientAsync(target.FirmId, clientId, cancellationToken);
            throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = FirmClientRules.RefusalForRefusedLink(reread) });
        }
    }

    private static string ClientIdOf(JsonObject payload)
    {
        var clientId = Json.AsString(payload["client_id"]);
        return clientId is null || PyText.Strip(clientId).Length == 0
            ? throw new FieldValidationException(new Dictionary<string, string> { ["client_id"] = "Choose a client." })
            : PyText.Strip(clientId);
    }

    private static Task<TaxIdRef?> ResolveTaxIdAsync(HttpContext context, DebtorDraft draft, Debtor? stored, Case target, CancellationToken cancellationToken) =>
        TaxIdRules.StoreAsync(draft.TaxId, stored?.TaxId, target.FirmId, target.Id, context.Port<ITaxIdCipher>(), context.Port<ITaxIdStore>(), context.Clock(), cancellationToken);

    // differs_from_client only for a caller who may see the client directory.
    private static async Task<JsonObject> ViewAsync(HttpContext context, Accessor accessor, Case target, Debtor debtor, FirmClient? client = null, CancellationToken cancellationToken = default)
    {
        if (debtor.ClientId is null || !accessor.May(Feature.Clients, PermissionLevel.ViewOnly))
        {
            return CaseViews.Debtor(debtor);
        }

        if (client is null || client.Id != debtor.ClientId)
        {
            client = await context.FirmStore().GetClientAsync(target.FirmId, debtor.ClientId, cancellationToken);
        }

        return client is null ? CaseViews.Debtor(debtor) : CaseViews.Debtor(debtor, ClientCopies.DiffersFromClient(debtor, client));
    }

    // GLBA: ids and the role, never a name or a number.
    private static void LogSaved(HttpContext context, string caseId, Debtor debtor) =>
        context.Log(Logger, "debtor saved", ("case_id", caseId), ("debtor_id", debtor.Id), ("role", debtor.FilingRole.ToWire()));
}
