using Insolvia.Api.Auth;
using Insolvia.Domain;

namespace Insolvia.Api.Hosting;

/// <summary>
/// The API's OpenAPI document (<see cref="WireOpenApi"/>): a firm user's or a
/// portal client's token, as the endpoint's <see cref="PrincipalClass"/> says,
/// and the permissions its <see cref="PermissionRequirement"/>s name.
/// <c>docs/reference/openapi/api.json</c> is a copy a test keeps current.
/// </summary>
public static class ApiDocument
{
    /// <summary>Registers the document.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddApiDocument(this IServiceCollection services) =>
        services.AddOpenApi(options => WireOpenApi.Configure(options, new WireOpenApiOptions(
            "Insolvia API",
            Description,
            new Dictionary<string, Microsoft.OpenApi.OpenApiSecurityScheme>
            {
                [PrincipalClass.Staff.Name] = WireOpenApi.Bearer("A firm user's Cognito access token."),
                [PrincipalClass.Client.Name] = WireOpenApi.Bearer("A portal client's Cognito access token."),
            },
            metadata => metadata.OfType<PrincipalClass>().LastOrDefault()?.Name,
            metadata => metadata.OfType<PermissionRequirement>().Select(p => $"{p.Feature.ToWire()}:{p.Level.ToWire()}"))));

    private const string Description = """
        The API behind the Insolvia app and the client portal.

        **Signing in.** A route that needs a caller takes a Cognito access token as `Authorization: Bearer <token>`: a firm user's (`staff`) or a portal client's (`client`), as the route says. Pick the scheme under Authentication below and paste the token to try a route.

        **Permissions.** A staff route that needs more than membership of the firm lists them under `x-insolvia-permissions`, as `feature:level`. A firm's administrators hold every permission.

        **Errors.** Every refusal is a JSON `ErrorBody`: `error` is a stable code to branch on, `message` is for people, and a validation error names the `fields` at fault.

        **Absent, not null.** A member a schema does not require is left out when it is unset, never sent as `null`.

        **Times** are ISO 8601 in UTC; court dates are `YYYY-MM-DD`.
        """;
}
