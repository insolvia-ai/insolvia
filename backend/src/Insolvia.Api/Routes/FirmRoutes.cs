using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.Firms;
using Insolvia.Domain.Ports;
using Microsoft.AspNetCore.Http.HttpResults;

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
        routes.MapPatch("/v1/firm", UpdateFirmAsync).RequestBody<FirmUpdateRequest>().RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapGet("/v1/firm/directory", DirectoryAsync).RequireAuth().Requires(Feature.Cases, PermissionLevel.ViewOnly);
        routes.MapGet("/v1/firm/users", ListUsersAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.ViewOnly);
        routes.MapPost("/v1/firm/users", AddUserAsync).RequestBody<FirmUserCreateRequest>().RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapPatch("/v1/firm/users/{subject}", UpdateUserAsync).RequestBody<FirmUserUpdateRequest>().RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
        routes.MapDelete("/v1/firm/users/{subject}", RemoveUserAsync).RequireAuth().Requires(Feature.FirmAdministration, PermissionLevel.AddEdit);
    }

    /// <summary>The caller's firm, without provisioning provenance (#217).</summary>
    internal static async Task<FirmSummaryView> ReadFirmAsync(HttpContext context, CancellationToken cancellationToken) =>
        FirmSummaryView.Of((await context.CurrentAccessorAsync()).Firm);

    /// <summary>Rename the firm, or change its defaults or letterhead.</summary>
    /// <remarks>A status is refused: a firm suspending itself would lock out its own administrators.</remarks>
    internal static async Task<FirmSummaryView> UpdateFirmAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var courts = context.RequestServices.GetRequiredService<ICourtRegistry>();
        var changes = FirmParser.ParseFirmUpdate(await context.BodyAsync(MaxRequestBytes), courts);

        var updated = FirmLifecycle.ApplyFirmChanges(accessor.Firm, changes, context.Clock());
        var written = await store.UpdateFirmAsync(updated, cancellationToken)
            ?? throw new NotFoundException("firm not found"); // vanished between resolving and writing

        context.Log(Logger, "firm updated");
        return FirmSummaryView.Of(written);
    }

    /// <summary>Every member of the firm, by name, for anyone who works cases.</summary>
    /// <remarks>Disabled colleagues are included, so a case's <c>createdBy</c> stays resolvable.</remarks>
    internal static async Task<FirmDirectory> DirectoryAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var people = await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken);
        return new FirmDirectory([.. people.Select(UserSummaryView.Of)]);
    }

    /// <summary>Every member of the firm, whole records, for its administrators.</summary>
    internal static async Task<FirmUserList> ListUsersAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var accessor = await context.CurrentAccessorAsync();
        var users = await context.FirmStore().ListUsersAsync(accessor.FirmId, cancellationToken);
        return new FirmUserList([.. users.Select(UserView.Of)]);
    }

    // Cognito FIRST: the subject it returns is half the row's key. A failure
    // in between leaves a pool user in no firm — a state /v1/me already
    // reports and a retry resolves; the other order leaves an unreachable row.

    /// <summary>Add someone to the firm, creating their sign-in account.</summary>
    /// <remarks>Supplied permissions merge over the role's defaults.</remarks>
    internal static async Task<Created<UserView>> AddUserAsync(HttpContext context, CancellationToken cancellationToken)
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
        return TypedResults.Created((string?)null, UserView.Of(user));
    }

    /// <summary>Change a firm member's name, role, axes, permissions, status or signature block.</summary>
    /// <remarks>
    /// Another firm's subject is the same 404 as none. <c>permissions</c>
    /// replaces the stored map. A change that would leave the firm with no
    /// active administrator is a 409.
    /// </remarks>
    internal static async Task<UserView> UpdateUserAsync(HttpContext context, string subject, CancellationToken cancellationToken)
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
        return UserView.Of(written);
    }

    /// <summary>Remove someone from the firm.</summary>
    /// <remarks>
    /// Removes the membership, not the person: their sign-in account survives.
    /// Removing the last active administrator is a 409.
    /// </remarks>
    internal static async Task<NoContent> RemoveUserAsync(HttpContext context, string subject, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        await RefuseIfLastAdminAsync(store, accessor.FirmId, cancellationToken, removed: subject);

        if (!await store.RemoveUserAsync(accessor.FirmId, subject, cancellationToken))
        {
            throw new NotFoundException(UserNotFound);
        }

        context.Log(Logger, "firm user removed");
        return TypedResults.NoContent();
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
