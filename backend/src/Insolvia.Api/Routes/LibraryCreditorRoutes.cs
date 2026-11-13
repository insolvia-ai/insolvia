using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Domain;
using Insolvia.Domain.Firms;
using Insolvia.Domain.LibraryCreditors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Insolvia.Api.Routes;

/// <summary>
/// The firm's reusable creditor library (13.9 / #350; services/api
/// routes/library_creditors.py), gated by <c>creditor_library</c> and scoped
/// to the caller's firm. Whole-record POST/PUT. A case
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
        var sizeLimit = new RequestSizeLimitAttribute(MaxRequestBytes);
        routes.MapPost("/v1/firm/creditors", AddAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/creditors/{creditorId}", GetAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.ViewOnly);
        routes.MapPut("/v1/firm/creditors/{creditorId}", UpdateAsync).WithMetadata(sizeLimit).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/creditors/{creditorId}", DeleteAsync).RequireAuth().Requires(Feature.CreditorLibrary, PermissionLevel.AddEdit);
    }

    /// <summary>List the creditor library.</summary>
    /// <remarks>Every creditor the firm keeps for copying into its cases.</remarks>
    internal static async Task<LibraryCreditorList> ListAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var creditors = await context.FirmStore().ListLibraryCreditorsAsync(accessor.FirmId, cancellationToken);
        return new LibraryCreditorList([.. creditors.Select(LibraryCreditorView.Of)]);
    }

    /// <summary>Add a creditor to the library.</summary>
    internal static async Task<Created<LibraryCreditorView>> AddAsync(HttpContext context, LibraryCreditorDraft draft, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var creditor = LibraryCreditorRules.Create(draft, accessor.FirmId, context.Clock());
        await context.FirmStore().CreateLibraryCreditorAsync(creditor, cancellationToken);

        // GLBA: no creditor name, no address.
        context.Log(Logger, "library creditor added");
        return TypedResults.Created((string?)null, LibraryCreditorView.Of(creditor));
    }

    /// <summary>Read a library creditor.</summary>
    internal static async Task<LibraryCreditorView> GetAsync(HttpContext context, string creditorId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var creditor = await context.FirmStore().GetLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken)
            ?? throw new NotFoundException(NotFound);
        return LibraryCreditorView.Of(creditor);
    }

    /// <summary>Save a library creditor, whole.</summary>
    /// <remarks>PUT, not PATCH: a field the body omits is cleared. A case that copied it is never touched.</remarks>
    internal static async Task<LibraryCreditorView> UpdateAsync(HttpContext context, string creditorId, LibraryCreditorDraft draft, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();

        var existing = await store.GetLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken)
            ?? throw new NotFoundException(NotFound);
        var written = await store.UpdateLibraryCreditorAsync(LibraryCreditorRules.Replace(existing, draft, context.Clock()), cancellationToken)
            ?? throw new NotFoundException(NotFound); // removed between the read and the write

        context.Log(Logger, "library creditor updated");
        return LibraryCreditorView.Of(written);
    }

    /// <summary>Remove a creditor from the library.</summary>
    internal static async Task<NoContent> DeleteAsync(HttpContext context, string creditorId, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        if (!await context.FirmStore().DeleteLibraryCreditorAsync(accessor.FirmId, creditorId, cancellationToken))
        {
            throw new NotFoundException(NotFound);
        }

        context.Log(Logger, "library creditor removed");
        return TypedResults.NoContent();
    }
}
