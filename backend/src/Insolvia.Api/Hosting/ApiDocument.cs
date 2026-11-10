using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Insolvia.Api.Auth;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Insolvia.Api.Hosting;

/// <summary>
/// The API's OpenAPI document, generated from the endpoints themselves: the
/// schemas from the handlers' typed results (<see cref="WireJson"/>), the
/// summaries from their doc comments, and from each endpoint's metadata who
/// may call it (<see cref="PrincipalClass"/>) and what it requires
/// (<see cref="PermissionRequirement"/>). Served at <c>/openapi/v1.json</c>;
/// <c>docs/reference/openapi/api.json</c> is a copy a test keeps current.
/// </summary>
public static class ApiDocument
{
    /// <summary>Where a route lists the permissions it requires, as <c>feature:level</c>.</summary>
    public const string PermissionsExtension = "x-insolvia-permissions";

    /// <summary>Registers the document and its transformers.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddApiDocument(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer(OptionalMembers);
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (metadata.OfType<PrincipalClass>().LastOrDefault() is { } principal)
                {
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(principal.Name, context.Document)] = [] }];
                }

                var permissions = metadata.OfType<PermissionRequirement>().Select(p => (JsonNode?)$"{p.Feature.ToWire()}:{p.Level.ToWire()}").ToArray();
                if (permissions.Length > 0)
                {
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                    operation.Extensions[PermissionsExtension] = new JsonNodeExtension(new JsonArray(permissions));
                }

                operation.Responses ??= [];
                operation.Responses["4XX"] = new OpenApiResponseReference(ErrorResponse, context.Document);
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer(async (document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo { Title = "Insolvia API", Version = "v1" };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [PrincipalClass.Staff.Name] = Bearer("A firm user's Cognito access token."),
                    [PrincipalClass.Client.Name] = Bearer("A portal client's Cognito access token."),
                };
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                document.Components.Schemas[nameof(ErrorBody)] = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), null, cancellationToken);
                document.Components.Responses = new Dictionary<string, IOpenApiResponse>
                {
                    [ErrorResponse] = new OpenApiResponse
                    {
                        Description = "Refused: the error code, and a message or the fields at fault.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new() { Schema = new OpenApiSchemaReference(nameof(ErrorBody), document) },
                        },
                    },
                };
                foreach (var schema in document.Components.Schemas.Values.OfType<OpenApiSchema>())
                {
                    NeverNullWhenOptional(schema);
                }
            });
        });

    private const string ErrorResponse = "Error";

    private static OpenApiSecurityScheme Bearer(string description) =>
        new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = description };

    // A member the serializer leaves out when it is unset (WireJson gives
    // each one a ShouldSerialize) is not required.
    private static Task OptionalMembers(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object && schema.Required is { } required)
        {
            foreach (var property in context.JsonTypeInfo.Properties.Where(p => p.ShouldSerialize is not null))
            {
                required.Remove(property.Name);
            }
        }

        return Task.CompletedTask;
    }

    // ...and absent, never null: the wire has no optional member that is null.
    private static void NeverNullWhenOptional(OpenApiSchema schema)
    {
        foreach (var (name, member) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (schema.Required?.Contains(name) == true || member is not OpenApiSchema concrete)
            {
                continue;
            }

            if (concrete.Type is { } type)
            {
                concrete.Type = type & ~JsonSchemaType.Null;
            }

            if (concrete.OneOf is { Count: 2 } options && options.FirstOrDefault(o => o is not OpenApiSchema { Type: JsonSchemaType.Null }) is { } only)
            {
                schema.Properties![name] = only;
            }
        }
    }
}
