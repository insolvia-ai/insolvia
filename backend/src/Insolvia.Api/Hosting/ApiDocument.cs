using Insolvia.Api.Auth;
using Insolvia.Api.Cases;
using Insolvia.Domain;
using Insolvia.Domain.Candidates;
using Insolvia.Domain.CaseEntities;
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

    private const string CollectionSegment = "{collection}";
    private const string Json = "application/json";

    // What the handlers' types cannot say: a collection route's record is the
    // collection's in the URL, a candidate's payload is its entityType's, and
    // a job's result is its kind's.
    private static async Task RefineAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components!.Schemas!;
        var payloads = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        var templates = document.Paths.Where(path => path.Key.Contains(CollectionSegment, StringComparison.Ordinal)).ToList();
        var request = templates.SelectMany(path => path.Value.Operations!.Values).Select(op => op.RequestBody?.Content?[Json].Schema).OfType<OpenApiSchema>().First();
        foreach (var kind in CaseCollections.Kinds)
        {
            var name = kind.BodyType.Name.Replace("Body", "", StringComparison.Ordinal);
            var record = WireOpenApi.Copy(schemas[nameof(EntityView)]);
            record.AdditionalProperties = null;
            record.Description = $"A record of `{kind.Collection}`: the server's fields, and beside them the collection's, each unanswered one absent.";
            WireOpenApi.Merge(record, await context.GetOrCreateSchemaAsync(kind.BodyType, null, cancellationToken).ConfigureAwait(false));
            schemas[$"{name}Record"] = record;

            // A request's body fields are all optional, and null clears one (progressive intake).
            var input = WireOpenApi.Copy(request);
            input.AdditionalProperties = null;
            input.Required = new HashSet<string>();
            input.Metadata = null;
            input.Description = $"A record of `{kind.Collection}` as written: its fields, all optional.";
            foreach (var (field, shape) in (await context.GetOrCreateSchemaAsync(kind.BodyType, null, cancellationToken).ConfigureAwait(false)).Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                input.Properties![field] = shape;
            }

            schemas[$"{name}RecordRequest"] = input;
            payloads[kind.Collection] = new OpenApiSchemaReference($"{name}RecordRequest", document);
            foreach (var (path, item) in templates)
            {
                document.Paths.Add(path.Replace(CollectionSegment, kind.Collection, StringComparison.Ordinal), ForCollection(item, kind, name, document));
            }
        }

        foreach (var (path, _) in templates)
        {
            document.Paths.Remove(path);
        }

        schemas[nameof(DebtorRequest)] = await context.GetOrCreateSchemaAsync(typeof(DebtorRequest), null, cancellationToken).ConfigureAwait(false);
        payloads[CandidateRules.Debtors] = new OpenApiSchemaReference(nameof(DebtorRequest), document);
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

    // One collection's copy of a /{collection} path: no collection parameter,
    // and the collection's own record where the template has EntityView.
    private static OpenApiPathItem ForCollection(IOpenApiPathItem template, IEntityKind kind, string name, OpenApiDocument document)
    {
        var operations = new Dictionary<HttpMethod, OpenApiOperation>();
        foreach (var (method, original) in template.Operations!)
        {
            var operation = new OpenApiOperation(original)
            {
                Summary = $"{original.Summary}: {kind.Collection}",
                Parameters = [.. original.Parameters!.Where(parameter => parameter.Name != "collection")],
                Responses = [],
            };
            if (original.RequestBody is { } body)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = body.Required,
                    Content = new Dictionary<string, OpenApiMediaType> { [Json] = new() { Schema = new OpenApiSchemaReference($"{name}RecordRequest", document) } },
                };
            }

            foreach (var (status, response) in original.Responses!)
            {
                operation.Responses[status] = response.Content?.TryGetValue(Json, out var media) == true && media.Schema is { } schema
                    ? new OpenApiResponse { Description = response.Description, Content = new Dictionary<string, OpenApiMediaType> { [Json] = new() { Schema = ForCollection(schema, kind, name, document) } } }
                    : response;
            }

            operations[method] = operation;
        }

        return new OpenApiPathItem { Parameters = template.Parameters, Operations = operations };
    }

    // EntityView itself, or the listing's { "<collection>": [EntityView] }.
    private static IOpenApiSchema ForCollection(IOpenApiSchema schema, IEntityKind kind, string name, OpenApiDocument document)
    {
        var record = new OpenApiSchemaReference($"{name}Record", document);
        return schema is OpenApiSchemaReference { Reference.Id: nameof(EntityView) }
            ? record
            : new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema> { [kind.Collection] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = record } },
                Required = new HashSet<string> { kind.Collection },
            };
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
