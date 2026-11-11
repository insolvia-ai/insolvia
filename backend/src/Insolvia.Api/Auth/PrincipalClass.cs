using Insolvia.Domain.Firms;

namespace Insolvia.Api.Auth;

/// <summary>
/// Which principal class an endpoint admits, stamped by the filter that
/// authenticates it — <see cref="Staff"/> by
/// <see cref="StaffAuth.RequireAuth{TBuilder}"/>, <see cref="Client"/> by
/// <see cref="ClientAuth.RequireClient{TBuilder}"/> (Python's
/// <c>PRINCIPAL_CLASS_ATTRIBUTE</c>). A route test reads it off every endpoint
/// to prove that every <c>/v1/portal/</c> route admits clients and no other
/// route does (ADR 0023) — so a route added next month is covered without
/// anyone remembering.
/// </summary>
/// <param name="Name">The class's name.</param>
public sealed record PrincipalClass(string Name)
{
    /// <summary>Firm staff, through the web app client.</summary>
    public static readonly PrincipalClass Staff = new("staff");

    /// <summary>A debtor, through the client-portal app client.</summary>
    public static readonly PrincipalClass Client = new("client");
}

/// <summary>
/// A permission an endpoint requires, stamped by
/// <see cref="StaffAuth.Requires{TBuilder}"/> beside its filter, so a test can
/// read every route's requirements without calling it — the OpenAPI document
/// states them, and <c>OpenApiDocumentTests</c> holds it to these.
/// </summary>
/// <param name="Feature">The feature.</param>
/// <param name="Level">The level required.</param>
public sealed record PermissionRequirement(Feature Feature, PermissionLevel Level);

/// <summary>An <see cref="IResult"/> that writes an error body.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The body.</param>
internal sealed record JsonBodyResult(int Status, ErrorBody Body) : IResult
{
    /// <summary>The 401 every authentication failure answers.</summary>
    public static readonly JsonBodyResult Unauthorized = new(StatusCodes.Status401Unauthorized, Insolvia.Http.ErrorResponses.Unauthorized);

    public Task ExecuteAsync(HttpContext httpContext) => Insolvia.Http.ErrorResponses.WriteAsync(httpContext, Status, Body);
}
