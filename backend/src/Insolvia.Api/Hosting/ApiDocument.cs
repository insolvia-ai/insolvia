using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Domain;
using Insolvia.Domain.Candidates;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Debtors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

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
            metadata => metadata.OfType<PermissionRequirement>().Select(p => $"{p.Feature.ToWire()}:{p.Level.ToWire()}"),
            RefineAsync)));

    // What the handlers' types cannot say: a candidate's payload is its
    // entityType's body, and a job's result is its kind's.
    private static async Task RefineAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components!.Schemas!;
        var payloads = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        foreach (var kind in CaseCollections.Kinds)
        {
            var name = kind.BodyType.Name;
            schemas[name] = await context.GetOrCreateSchemaAsync(kind.BodyType, null, cancellationToken).ConfigureAwait(false);
            payloads[kind.Collection] = new OpenApiSchemaReference(name, document);
        }

        schemas[nameof(DebtorBody)] = await context.GetOrCreateSchemaAsync(typeof(DebtorBody), null, cancellationToken).ConfigureAwait(false);
        payloads[CandidateRules.Debtors] = new OpenApiSchemaReference(nameof(DebtorBody), document);
        WireOpenApi.Discriminate(document, nameof(CandidateView), "entityType", payloads.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, IOpenApiSchema>)new Dictionary<string, IOpenApiSchema> { ["payload"] = pair.Value, ["correctedPayload"] = pair.Value }));

        var results = new Dictionary<string, IReadOnlyDictionary<string, IOpenApiSchema>>(StringComparer.Ordinal);
        foreach (var (kind, types) in JobResults.ByKind)
        {
            var options = new List<IOpenApiSchema>();
            foreach (var type in types)
            {
                schemas[type.Name] = await context.GetOrCreateSchemaAsync(type, null, cancellationToken).ConfigureAwait(false);
                options.Add(new OpenApiSchemaReference(type.Name, document));
            }

            results[kind] = new Dictionary<string, IOpenApiSchema> { ["result"] = options.Count == 1 ? options[0] : new OpenApiSchema { AnyOf = options } };
        }

        WireOpenApi.Discriminate(document, nameof(JobView), "kind", results);
    }

    private const string Description = """
        The API behind the Insolvia app and the client portal.

        **Signing in.** A route that needs a caller takes a Cognito access token as `Authorization: Bearer <token>`: a firm user's (`staff`) or a portal client's (`client`), as the route says. Pick the scheme under Authentication below and paste the token to try a route.

        **Permissions.** A staff route that needs more than membership of the firm lists them under `x-insolvia-permissions`, as `feature:level`. A firm's administrators hold every permission.

        **Errors.** Every refusal is a JSON `ErrorBody`: `error` is a stable code to branch on, `message` is for people, and a validation error names the `fields` at fault.

        **Absent, not null.** A member a schema does not require is left out when it is unset, never sent as `null`.

        **Times** are ISO 8601 in UTC; court dates are `YYYY-MM-DD`.
        """;
}
