using Insolvia.Admin.Auth;

namespace Insolvia.Admin.Hosting;

/// <summary>
/// The admin service's OpenAPI document (<see cref="WireOpenApi"/>): every
/// route but <c>/health</c> takes a staff Google Workspace ID token
/// (<see cref="StaffRequirement"/>). <c>docs/reference/openapi/admin.json</c>
/// is a copy a test keeps current.
/// </summary>
public static class AdminDocument
{
    private const string Scheme = "google";

    /// <summary>Registers the document.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddAdminDocument(this IServiceCollection services) =>
        services.AddOpenApi(options => WireOpenApi.Configure(options, new WireOpenApiOptions(
            "Insolvia admin service",
            Description,
            new Dictionary<string, Microsoft.OpenApi.OpenApiSecurityScheme>
            {
                [Scheme] = WireOpenApi.Bearer("A staff member's Google Workspace ID token."),
            },
            metadata => metadata.OfType<StaffRequirement>().Any() ? Scheme : null,
            _ => [])));

    private const string Description = """
        The staff portal's service: provisioning and administering firms.

        **Signing in.** Every route but `/health` takes an Insolvia staff member's Google Workspace ID token as `Authorization: Bearer <token>`.

        **Errors.** Every refusal is a JSON `ErrorBody`: `error` is a stable code to branch on, `message` is for people, and a validation error names the `fields` at fault.

        **Absent, not null.** A member a schema does not require is left out when it is unset, never sent as `null`.

        **Times** are ISO 8601 in UTC; court dates are `YYYY-MM-DD`.
        """;
}
