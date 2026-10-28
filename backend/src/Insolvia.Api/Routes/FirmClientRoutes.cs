using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// The firm's client directory (ADR 0022 / #353; services/api
/// routes/firm_clients.py): list, create, read, whole-record edit, archive
/// as a status write, the prospect funnel, and merge — gated by <c>clients</c>.
/// </summary>
/// <remarks>
/// <para><b>No DELETE</b> — archiving is <c>PUT …/status</c>.</para>
/// <para><b>Access-logged</b>: a client is PII with no case to be logged
/// under, so single-record reads and every write append a <c>client.*</c>
/// row keyed to the client (a refused one <c>denied</c>); the list is not logged.</para>
/// <para><b>Server-owned fields</b> — status, <c>created_by</c>, the tax-id
/// pointer, the stage — never come from the whole-record body.</para>
/// <para>Not here: <c>GET …/{id}/cases</c>, which reads the case store's
/// by-client index and so arrives with the case routes (18.15).</para>
/// </remarks>
public sealed class FirmClientRoutes : IEndpointModule
{
    // A name, two addresses, a handful of aliases and a few short fields.
    private const int MaxRequestBytes = 32 * 1024;
    private const string Logger = "insolvia_api.api.routes.firm_clients";
    private const string NotFound = "client not found";
    private const string Retained = "This client has been retained — the prospect funnel is behind them.";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/firm/clients", ListAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/clients", CreateAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/clients/{clientId}", GetAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/clients/{clientId}", UpdateAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.AddEdit);
        routes.MapPut("/v1/firm/clients/{clientId}/status", SetStatusAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.AddEdit);
        routes.MapPut("/v1/firm/clients/{clientId}/prospect-stage", SetProspectStageAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.AddEdit);
        routes.MapPost("/v1/firm/clients/{clientId}/merge", MergeAsync).RequireAuth().Requires(Feature.Clients, PermissionLevel.AddEdit);
    }

    private static async Task<IResult> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = Stores(context).Firms;
        var clients = await store.ListClientsAsync((await context.CurrentAccessorAsync()).FirmId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["clients"] = new JsonArray([.. clients.Select(client => (JsonNode)FirmViews.Client(client))]) });
    }

    private static async Task<IResult> CreateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var accessor = await context.CurrentAccessorAsync();
        var clock = context.Clock();
        var draft = FirmClientRules.Parse(await context.BodyAsync(MaxRequestBytes), clock: clock);
        var client = FirmClientRules.Create(draft, accessor.FirmId, accessor.Subject, clock);
        await store.CreateClientAsync(client, cancellationToken);
        await LogAsync(context, log, client.Id, AccessAction.ClientCreate, found: true, cancellationToken);

        // GLBA: that a firm added SOMEONE is all a request log needs.
        context.Log(Logger, "client created");
        return FlaskJson.Result(FirmViews.Client(client), StatusCodes.Status201Created);
    }

    // The refused read IS logged: someone walking client ids is what the log should show.
    private static async Task<IResult> GetAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var client = await store.GetClientAsync((await context.CurrentAccessorAsync()).FirmId, clientId, cancellationToken);
        await LogAsync(context, log, clientId, AccessAction.ClientRead, found: client is not null, cancellationToken);
        return client is null ? throw new NotFoundException(NotFound) : FlaskJson.Result(FirmViews.Client(client));
    }

    // Whole-record replace; keeps status, tax-id pointer, stamps and stage.
    private static async Task<IResult> UpdateAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var clock = context.Clock();
        var draft = FirmClientRules.Parse(await context.BodyAsync(MaxRequestBytes), clock: clock);
        var existing = await ExistingOr404Async(context, store, log, clientId, AccessAction.ClientUpdate, cancellationToken);
        RefuseMerged(existing);
        var response = await WriteAsync(context, store, log, FirmClientRules.Replace(existing, draft, clock), cancellationToken);
        context.Log(Logger, "client updated");
        return response;
    }

    // Archive or restore — a status write, never a delete; logged as an update.
    private static async Task<IResult> SetStatusAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var status = FirmClientRules.ParseClientStatus(await context.BodyAsync(MaxRequestBytes));
        var existing = await ExistingOr404Async(context, store, log, clientId, AccessAction.ClientUpdate, cancellationToken);
        RefuseMerged(existing);
        var response = await WriteAsync(context, store, log, FirmClientRules.SetStatus(existing, status, context.Clock()), cancellationToken);
        context.Log(Logger, "client status changed", ("status", status.ToWire()));
        return response;
    }

    // A stage for a PROSPECT, or null to leave the funnel (14.3 / #355). A
    // retained client is a 409, read from the record — never from its cases.
    // The write is conditional on the client still being a prospect.
    private static async Task<IResult> SetProspectStageAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var stage = FirmClientRules.ParseProspectStage(await context.BodyAsync(MaxRequestBytes));
        var firmId = (await context.CurrentAccessorAsync()).FirmId;
        var existing = await ExistingOr404Async(context, store, log, clientId, AccessAction.ClientUpdate, cancellationToken);
        RefuseMerged(existing);
        if (!existing.Prospect)
        {
            throw new ConflictException(Retained);
        }

        var written = await store.SetClientProspectStageAsync(firmId, clientId, stage, cancellationToken);
        await LogAsync(context, log, clientId, AccessAction.ClientUpdate, found: written is not null, cancellationToken);
        if (written is null)
        {
            // Retained (or merged) underneath the caller is a 409; gone is the usual 404.
            var current = await store.GetClientAsync(firmId, clientId, cancellationToken) ?? throw new NotFoundException(NotFound);
            RefuseMerged(current);
            throw new ConflictException(Retained);
        }

        context.Log(Logger, "client prospect stage changed", ("stage", stage?.ToWire()));
        return FlaskJson.Result(FirmViews.Client(written));
    }

    // Fold the named client INTO this one (ADR 0022 PR 7). Answers both
    // clients and no count of the cases moved — a count would include cases
    // the caller may not see. Logged under BOTH ids.
    private static async Task<IResult> MergeAsync(HttpContext context, string clientId, CancellationToken cancellationToken)
    {
        var (store, log) = Stores(context);
        var accessor = await context.CurrentAccessorAsync();
        var mergedId = FirmClientRules.ParseClientMerge(await context.BodyAsync(MaxRequestBytes));
        var survivor = await store.GetClientAsync(accessor.FirmId, clientId, cancellationToken);
        if (survivor is null)
        {
            await LogAsync(context, log, clientId, AccessAction.ClientMerge, found: false, cancellationToken);
            throw new NotFoundException(NotFound);
        }

        var merged = await store.GetClientAsync(accessor.FirmId, mergedId, cancellationToken);
        if (merged is null)
        {
            await LogAsync(context, log, mergedId, AccessAction.ClientMerge, found: false, cancellationToken);
            throw new FieldValidationException(new Dictionary<string, string> { ["merged_client_id"] = "No such client." });
        }

        var debtors = context.RequestServices.GetService<IDebtorClientLinks>()
            ?? throw new InvalidOperationException("debtor store is not composed");
        var result = await ClientMerges.MergeAsync(store, debtors, survivor, merged, context.Clock(), cancellationToken);
        await LogAsync(context, log, merged.Id, AccessAction.ClientMerge, found: true, cancellationToken);
        await LogAsync(context, log, survivor.Id, AccessAction.ClientMerge, found: true, cancellationToken);

        // GLBA: that two records became one — no name, no id.
        context.Log(Logger, "clients merged");
        return FlaskJson.Result(new JsonObject
        {
            ["client"] = FirmViews.Client(result.Survivor),
            ["merged"] = FirmViews.Client(result.Merged),
        });
    }

    private static (IFirmStore Firms, IAccessLog Log) Stores(HttpContext context)
    {
        var firms = context.RequestServices.GetService<IFirmStore>();
        var log = context.RequestServices.GetService<IAccessLog>();
        return firms is null || log is null
            ? throw new InvalidOperationException("firm store or access log is not composed")
            : (firms, log);
    }

    private static async Task<FirmClient> ExistingOr404Async(HttpContext context, IFirmStore store, IAccessLog log, string clientId, AccessAction action, CancellationToken cancellationToken)
    {
        var existing = await store.GetClientAsync((await context.CurrentAccessorAsync()).FirmId, clientId, cancellationToken);
        if (existing is null)
        {
            await LogAsync(context, log, clientId, action, found: false, cancellationToken);
            throw new NotFoundException(NotFound);
        }

        return existing;
    }

    // A row removed or moved between the read and this write answers the
    // same 404 as an id that never existed, rather than being resurrected.
    private static async Task<IResult> WriteAsync(HttpContext context, IFirmStore store, IAccessLog log, FirmClient client, CancellationToken cancellationToken)
    {
        var written = await store.UpdateClientAsync(client, cancellationToken);
        await LogAsync(context, log, client.Id, AccessAction.ClientUpdate, found: written is not null, cancellationToken);
        return written is null ? throw new NotFoundException(NotFound) : FlaskJson.Result(FirmViews.Client(written));
    }

    private static async Task LogAsync(HttpContext context, IAccessLog log, string clientId, AccessAction action, bool found, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        await log.RecordAsync(
            AccessEvents.Record(accessor.Subject, action, clientId: clientId, outcome: found ? AccessOutcome.Allowed : AccessOutcome.Denied, clock: context.Clock()),
            cancellationToken);
    }

    // A merged client is terminal: its edits belong on the survivor. 409 —
    // the caller may see it; its state is what refuses.
    private static void RefuseMerged(FirmClient client)
    {
        if (client.MergedInto is not null)
        {
            throw new ConflictException("This client was merged into another client. Edit that client instead.");
        }
    }
}
