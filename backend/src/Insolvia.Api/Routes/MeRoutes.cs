using Insolvia.Api.Auth;
using Insolvia.Api.Firms;
using Insolvia.Api.Hosting;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Firms;

namespace Insolvia.Api.Routes;

/// <summary>
/// <c>/v1/me</c> (services/api routes/me.py): who the signed-in caller is,
/// and the one self-service write — their own name and signature block.
/// </summary>
/// <remarks>
/// <para><b>The one route that resolves the accessor without requiring it.</b>
/// A signed-in caller in no firm is a 403 everywhere else; here <c>firm</c>
/// is simply absent, so "signed in but not provisioned" is a state a client
/// can render. Everything else comes from the token's own claims — no call
/// to Cognito, and no email (the access token carries none).</para>
/// <para><b>PATCH needs no permission</b> — the axes govern what an admin may
/// do to others — only an active membership (403 without), and answers the
/// GET body so the client re-renders without a second request.</para>
/// </remarks>
public sealed class MeRoutes : IEndpointModule
{
    // A first and a last name of at most 100 characters each, and a signature block.
    private const int MaxRequestBytes = 4 * 1024;
    private const string Logger = "insolvia_api.api.routes.me";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/v1/me", ReadAsync).RequireAuth();
        routes.MapPatch("/v1/me", UpdateAsync).RequireAuth();
    }

    private static async Task<IResult> ReadAsync(HttpContext context, CancellationToken cancellationToken) =>
        FlaskJson.Result(FirmViews.Me(context.CurrentPrincipal(), await context.ResolveAccessorAsync()));

    private static async Task<IResult> UpdateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var store = context.FirmStore();
        var accessor = await context.CurrentAccessorAsync();
        var changes = FirmParser.ParseSelfUpdate(await context.BodyAsync(MaxRequestBytes));

        var updated = FirmLifecycle.ApplyUserChanges(accessor.User, changes, context.Clock());
        var written = await store.UpdateUserAsync(updated, cancellationToken)
            ?? throw new NotFoundException("firm user not found"); // removed between resolving and writing

        context.Log(Logger, "self name updated");
        return FlaskJson.Result(FirmViews.Me(context.CurrentPrincipal(), new Accessor(accessor.Firm, written)));
    }
}
