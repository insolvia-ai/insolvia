using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Insolvia.Http;

/// <summary>
/// A host's OpenAPI document, generated from its endpoints: the schemas from
/// the handlers' typed results (<see cref="WireJson"/>, which the host's
/// <c>ConfigureHttpJsonOptions</c> must apply), the summaries from their doc
/// comments, a shared 4XX <see cref="ErrorBody"/>, and — from each
/// endpoint's metadata, through the host's <see cref="WireOpenApiOptions"/> —
/// who may call it and what it requires. Served at <c>/openapi/v1.json</c>,
/// and rendered for people at <c>/docs</c> (<see cref="MapReference"/>).
/// </summary>
public static partial class WireOpenApi
{
    /// <summary>Where the reference page is served.</summary>
    public const string ReferencePath = "/docs";

    /// <summary>Where a route lists the permissions it requires.</summary>
    public const string PermissionsExtension = "x-insolvia-permissions";

    private const string ErrorResponse = "Error";

    /// <summary>
    /// Configures the document. A host calls <c>services.AddOpenApi(options =&gt;
    /// WireOpenApi.Configure(options, …))</c> in its OWN code: the package's
    /// source generator, which turns the host's doc comments into summaries,
    /// hooks that call where it is written.
    /// </summary>
    /// <param name="options">The host's OpenAPI options.</param>
    /// <param name="host">The host's title, schemes and metadata readers.</param>
    public static void Configure(OpenApiOptions options, WireOpenApiOptions host)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        options.AddSchemaTransformer(OptionalMembers);
        options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (host.SchemeOf(metadata) is { } scheme)
            {
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] }];
            }

            var permissions = host.PermissionsOf(metadata).Select(p => (JsonNode?)p).ToArray();
            if (permissions.Length > 0)
            {
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions[PermissionsExtension] = new JsonNodeExtension(new JsonArray(permissions));
            }

            if (metadata.OfType<RequestBodyMetadata>().LastOrDefault() is { } body)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = await context.GetOrCreateSchemaAsync(body.Type, null, cancellationToken).ConfigureAwait(false) },
                    },
                };
            }

            operation.Responses ??= [];
            operation.Responses["4XX"] = new OpenApiResponseReference(ErrorResponse, context.Document);
        });
        options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            document.Info = new OpenApiInfo { Title = host.Title, Version = "v1", Description = host.Description };
            Tidy(document);
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = host.Schemes.ToDictionary(pair => pair.Key, pair => (IOpenApiSecurityScheme)pair.Value);
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
            document.Components.Schemas[nameof(ErrorBody)] = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), null, cancellationToken).ConfigureAwait(false);
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
    }

    /// <summary>
    /// Documents the JSON body the route reads as <typeparamref name="TRequest"/>.
    /// Only documents: the route still parses its body itself (its parser owns
    /// the 400s), and unlike <c>Accepts&lt;T&gt;</c> this does not make routing
    /// refuse a request with another content type.
    /// </summary>
    /// <typeparam name="TRequest">The body's shape.</typeparam>
    /// <param name="builder">The route.</param>
    /// <returns>The same route.</returns>
    public static RouteHandlerBuilder RequestBody<TRequest>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequestBodyMetadata(typeof(TRequest)));
    }

    /// <summary>
    /// Serves the reference page at <see cref="ReferencePath"/>, over the
    /// host's <c>/openapi/v1.json</c>: each route's permissions shown, and
    /// none of the page's AI chat, MCP, telemetry or vendor toolbar.
    /// </summary>
    /// <param name="app">The host.</param>
    /// <param name="title">The page's title.</param>
    /// <returns>The page's endpoint.</returns>
    public static IEndpointConventionBuilder MapReference(this IEndpointRouteBuilder app, string title) =>
        app.MapScalarApiReference(ReferencePath, options => options
            .WithTitle(title)
            .WithShowExtensions(PermissionsExtension)
            .SortTagsAlphabetically()
            .EnablePersistentAuthentication()
            .HideDeveloperTools()
            .DisableAgent()
            .DisableMcp()
            .DisableTelemetry());

    /// <summary>A bearer-token security scheme.</summary>
    /// <param name="description">Which token.</param>
    /// <returns>The scheme.</returns>
    public static OpenApiSecurityScheme Bearer(string description) =>
        new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = description };

    // A handler's doc comment arrives whole as its summary, and its class's
    // name (DebtorRoutes) as its tag. A reader wants a short title over the
    // rest of the comment, under a feature's name ("Debtor").
    private static void Tidy(OpenApiDocument document)
    {
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? []))
        {
            if (operation.Summary is { } summary)
            {
                var text = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var end = text.IndexOf(". ", StringComparison.Ordinal);
                operation.Summary = (end < 0 ? text : text[..end]).TrimEnd('.');
                if (end >= 0)
                {
                    operation.Description ??= text[(end + 2)..];
                }
            }

            if (operation.Tags is { } tags)
            {
                operation.Tags = tags.Select(tag => new OpenApiTagReference(Feature(tag.Name!), document)).ToHashSet();
            }
        }

        document.Tags = document.Tags?.Select(tag => new OpenApiTag { Name = Feature(tag.Name!), Description = tag.Description })
            .OrderBy(tag => tag.Name, StringComparer.Ordinal)
            .ToHashSet();
    }

    /// <summary>A route class's feature: <c>FirmQuestionnaireRoutes</c> is "Firm questionnaire".</summary>
    /// <param name="type">The class's name.</param>
    /// <returns>The feature's name.</returns>
    internal static string Feature(string type)
    {
        var words = WordBreak().Replace(RouteSuffix().Replace(type, ""), " ");
        return words.Length == 0 ? type : words[..1] + words[1..].ToLowerInvariant();
    }

    [GeneratedRegex("(Routes|Endpoints?)$")]
    private static partial Regex RouteSuffix();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();

    // A member the serializer leaves out when it is unset (WireJson gives
    // each one a ShouldSerialize) is not required...
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

/// <summary>Endpoint metadata: the shape of the JSON body the route reads (<see cref="WireOpenApi.RequestBody{TRequest}"/>).</summary>
/// <param name="Type">The body's type.</param>
public sealed record RequestBodyMetadata(Type Type);

/// <summary>What one host tells <see cref="WireOpenApi"/>.</summary>
/// <param name="Title">The document's title.</param>
/// <param name="Description">What the document opens with (Markdown): who calls it, and how.</param>
/// <param name="Schemes">Its security schemes, by name.</param>
/// <param name="SchemeOf">The scheme an endpoint's metadata requires; null for an open route.</param>
/// <param name="PermissionsOf">The permissions an endpoint's metadata requires, as <c>feature:level</c>.</param>
public sealed record WireOpenApiOptions(
    string Title,
    string Description,
    IReadOnlyDictionary<string, OpenApiSecurityScheme> Schemes,
    Func<IList<object>, string?> SchemeOf,
    Func<IList<object>, IEnumerable<string>> PermissionsOf);
