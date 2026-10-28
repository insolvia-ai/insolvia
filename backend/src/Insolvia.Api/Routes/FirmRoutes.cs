using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// A firm's own record and staff list (services/api routes/firm.py).
/// EVERY ROUTE IS SCOPED TO THE CALLER'S FIRM: no path names a firm, so
/// "may I administer this firm" has one answer, already decided by the
/// accessor. Two audiences: <c>/v1/firm/directory</c> is names for every
/// member (gated on cases), <c>/v1/firm/users</c> the whole record for
/// administrators.
/// </summary>
public sealed class FirmRoutes : IEndpointModule
{
    // A handful of short fields plus a permission map; anything larger is a mistake.
    private const int MaxRequestBytes = 16 * 1024;
    private const string Logger = "insolvia_api.api.routes.firm";
    private const string UserNotFound = "firm user not found";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/firm", ReadFirmAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.ViewOnly);
        routes.MapPatch("/v1/firm", UpdateFirmAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/directory", DirectoryAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/firm/users", ListUsersAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/users", AddUserAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/firm/users/{subject}", UpdateUserAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/users/{subject}", RemoveUserAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    // The firm's record without provisioning provenance (#217).
    private static async Task<IResult> ReadFirmAsync(HttpContext context, CancellationToken cancellationToken) =>
        FlaskJson.Result(FirmViews.FirmSummary((await context.CurrentAccessorAsync()).Firm));

    // Rename, defaults and letterhead. A status is refused by the parser —
    // a firm suspending ITSELF would lock out its own administrators.
    private static async Task<IResult> UpdateFirmAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var courts = context.RequestServices.GetService<ICourtRegistry>() ?? UncomposedCourtRegistry.Instance;
        var changes = FirmParser.ParseFirmUpdate(await context.BodyAsync(MaxRequestBytes), courts);

        var updated = FirmLifecycle.ApplyFirmChanges(accessor.Firm, changes, context.Clock());
        var written = await store.UpdateFirmAsync(updated, cancellationToken)
            ?? throw new NotFoundException("firm not found"); // vanished between resolving and writing

        context.Log(Logger, "firm updated");
        return FlaskJson.Result(FirmViews.FirmSummary(written));
    }

    // Everyone, disabled colleagues INCLUDED: a case's createdBy must stay resolvable.
    private static async Task<IResult> DirectoryAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var people = await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["people"] = new JsonArray([.. people.Select(person => (JsonNode)FirmViews.UserSummary(person))]) });
    }

    private static async Task<IResult> ListUsersAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var users = await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken);
        return FlaskJson.Result(new JsonObject { ["users"] = new JsonArray([.. users.Select(user => (JsonNode)FirmViews.User(user))]) });
    }

    // Cognito FIRST: the subject it returns is half the row's key. A failure
    // in between leaves a pool user in no firm — a state /v1/me already
    // reports and a retry resolves; the other order leaves an unreachable row.
    private static async Task<IResult> AddUserAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var draft = FirmParser.ParseFirmUserCreation(await context.BodyAsync(MaxRequestBytes));

        // Separate from the store so the read routes work without it.
        var directory = context.RequestServices.GetService<IUserDirectory>()
            ?? throw new InvalidOperationException("user directory is not composed");
        var subject = await directory.CreateUserAsync(draft.Email, cancellationToken);
        var user = FirmLifecycle.CreateFirmUser(draft, accessor.FirmId, subject, context.Clock());
        await store.AddUserAsync(user, cancellationToken);

        context.Log(Logger, "firm user added");
        return FlaskJson.Result(FirmViews.User(user), StatusCodes.Status201Created);
    }

    // Firm-scoped by key: another firm's subject is the same 404 as none.
    private static async Task<IResult> UpdateUserAsync(HttpContext context, string subject, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var changes = FirmParser.ParseFirmUserUpdate(await context.BodyAsync(MaxRequestBytes));

        var existing = await store.GetUserAsync(accessor.FirmId, subject, cancellationToken)
            ?? throw new NotFoundException(UserNotFound);
        var updated = FirmLifecycle.ApplyUserChanges(existing, changes, context.Clock());
        await RefuseIfLastAdminAsync(store, accessor.FirmId, cancellationToken, changed: updated);

        var written = await store.UpdateUserAsync(updated, cancellationToken)
            ?? throw new NotFoundException(UserNotFound);

        context.Log(Logger, "firm user updated");
        return FlaskJson.Result(FirmViews.User(written));
    }

    // The MEMBERSHIP, not the person: the Cognito account survives.
    private static async Task<IResult> RemoveUserAsync(HttpContext context, string subject, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        await RefuseIfLastAdminAsync(store, accessor.FirmId, cancellationToken, removed: subject);

        if (!await store.RemoveUserAsync(accessor.FirmId, subject, cancellationToken))
        {
            throw new NotFoundException(UserNotFound);
        }

        context.Log(Logger, "firm user removed");
        return FlaskJson.NoContent();
    }

    // THE ONE IRRECOVERABLE MISTAKE (self-signup is off). 409, not 403: the
    // caller has the permission; the firm's state refuses.
    private static async Task RefuseIfLastAdminAsync(IFirmStore store, string firmId, CancellationToken cancellationToken, FirmUser? changed = null, string? removed = null)
    {
        var everyone = await store.ListUsersAsync(firmId, cancellationToken);
        if (Permissions.WouldLeaveNoAdmin(everyone, changed, removed))
        {
            throw new ConflictException("a firm must keep at least one active administrator; appoint another before changing this one");
        }
    }
}

/// <summary>
/// The court registry until one is composed: the registry is the case half's
/// (18.7, Core). A PATCH that names no court never asks it; one that does is
/// a 500 that says why, never a 400 blaming a court code that may be valid.
/// </summary>
internal sealed class UncomposedCourtRegistry : ICourtRegistry
{
    public static readonly UncomposedCourtRegistry Instance = new();

    public bool HasDistrict(string courtCode) => throw new InvalidOperationException("court registry is not composed");

    public bool HasDivision(string courtCode, string divisionCode) => throw new InvalidOperationException("court registry is not composed");
}
