using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;

namespace Insolvia.Api.Routes;

/// <summary>
/// The firm's reusable creditor library (13.9 / #350; services/api
/// routes/library_creditors.py), gated by <c>creditor_library</c> and scoped
/// to the caller's firm. Whole-record POST/PUT, snake_case wire. A case
/// copies from the library with provenance; editing the library never
/// touches a case.
/// </summary>
public sealed class LibraryCreditorRoutes : IEndpointModule
{
    // A name, an address, a handful of notice parties and a notes box.
    private const int MaxRequestBytes = 32 * 1024;
    private const string Logger = "insolvia_api.api.routes.library_creditors";
    private const string NotFound = "library creditor not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/firm/creditors", ListAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/creditors", AddAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/creditors/{creditorId}", GetAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/creditors/{creditorId}", UpdateAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/creditors/{creditorId}", DeleteAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
    }

    private static async Task<IResult> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var creditors = await context.FirmStore().ListLibraryCreditorsAsync(accessor.FirmId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["creditors"] = new JsonArray([.. creditors.Select(creditor => (JsonNode)FirmViews.LibraryCreditor(creditor))]) });
    }

    private static async Task<IResult> AddAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var draft = LibraryCreditorRules.Parse(await context.BodyAsync(MaxRequestBytes));
        var creditor = LibraryCreditorRules.Create(draft, accessor.FirmId, context.Clock());
        await context.FirmStore().CreateLibraryCreditorAsync(creditor, cancellationToken);

        // GLBA: no creditor name, no address.
        context.Log(Logger, "library creditor added");
        return FlaskJson.Result(FirmViews.LibraryCreditor(creditor), StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetAsync(HttpContext context, string creditorId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var creditor = await context.FirmStore().GetLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken)
            ?? throw new NotFoundException(NotFound);
        return FlaskJson.Result(FirmViews.LibraryCreditor(creditor));
    }

    // PUT, not PATCH: a field the body omits is cleared.
    private static async Task<IResult> UpdateAsync(HttpContext context, string creditorId, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var draft = LibraryCreditorRules.Parse(await context.BodyAsync(MaxRequestBytes));

        var existing = await store.GetLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken)
            ?? throw new NotFoundException(NotFound);
        var written = await store.UpdateLibraryCreditorAsync(LibraryCreditorRules.Replace(existing, draft, context.Clock()), cancellationToken)
            ?? throw new NotFoundException(NotFound); // removed between the read and the write

        context.Log(Logger, "library creditor updated");
        return FlaskJson.Result(FirmViews.LibraryCreditor(written));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, string creditorId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (!await context.FirmStore().DeleteLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken))
        {
            throw new NotFoundException(NotFound);
        }

        context.Log(Logger, "library creditor removed");
        return FlaskJson.NoContent();
    }
}
