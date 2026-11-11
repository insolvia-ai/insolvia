using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

            foreach (var query in metadata.OfType<QueryParameterMetadata>())
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = query.Name,
                    In = ParameterLocation.Query,
                    Required = query.Required,
                    Description = query.Description,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
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
            if (host.Refine is { } refine)
            {
                await refine(document, context, cancellationToken).ConfigureAwait(false);
            }

            foreach (var schema in document.Components.Schemas.Values.OfType<OpenApiSchema>().SelectMany(s => s.OneOf?.OfType<OpenApiSchema>().Prepend(s) ?? [s]))
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
    /// Documents a query-string parameter the route reads. Only documents, like
    /// <see cref="RequestBody{TRequest}"/>: the route reads the query itself,
    /// and its parser owns the 400s.
    /// </summary>
    /// <param name="builder">The route.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="description">What it does, and its form.</param>
    /// <param name="required">Whether the route refuses a request without it.</param>
    /// <returns>The same route.</returns>
    public static RouteHandlerBuilder QueryParameter(this RouteHandlerBuilder builder, string name, string description, bool required = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new QueryParameterMetadata(name, description, required));
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

    private const string AbsentWhenUnset = "insolvia:absent";

    // Per object schema: a [Flatten]ed member's own members, as its type
    // documents them; a [JsonExtensionData] bag as additionalProperties; a
    // member the serializer leaves out when unset (WireJson gives each one a
    // ShouldSerialize) as not required — and a request body's nullable member
    // as not required either: the parsers take absent and null alike.
    private static async Task OptionalMembers(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo;
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        schema.Required ??= new HashSet<string>();
        foreach (var flattened in type.Type.GetProperties().Where(p => p.IsDefined(typeof(FlattenAttribute), inherit: true)))
        {
            Merge(schema, await context.GetOrCreateSchemaAsync(flattened.PropertyType, null, cancellationToken).ConfigureAwait(false));
        }

        if (type.Properties.Any(p => p.IsExtensionData))
        {
            schema.AdditionalProperties = new OpenApiSchema();
        }

        foreach (var property in type.Properties.Where(p => p.ShouldSerialize is not null))
        {
            schema.Required.Remove(property.Name);
            Absent(schema).Add(property.Name);
        }

        if (RequestBodies(context.ApplicationServices).Contains(type.Type))
        {
            var nullability = new NullabilityInfoContext();
            foreach (var property in type.Properties.Where(p => p.AttributeProvider is PropertyInfo info && nullability.Create(info).ReadState == NullabilityState.Nullable))
            {
                schema.Required.Remove(property.Name);
                // A body's schema arrives inline, where a nullable object is not marked so.
                // (Its type, not a oneOf: the generator walks into it after this to transform it.)
                if (schema.Properties.TryGetValue(property.Name, out var member) && member is OpenApiSchema { Type: JsonSchemaType.Object } inline)
                {
                    inline.Type = JsonSchemaType.Object | JsonSchemaType.Null;
                }
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/>'s members into <paramref name="target"/>,
    /// as a <see cref="FlattenAttribute"/> member's are: each property, whether
    /// it is required, and whether it is absent when unset.
    /// </summary>
    /// <param name="target">The object schema that gains the members.</param>
    /// <param name="source">The object schema whose members they are.</param>
    public static void Merge(OpenApiSchema target, IOpenApiSchema source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Properties ??= new Dictionary<string, IOpenApiSchema>();
        target.Required ??= new HashSet<string>();
        foreach (var (name, member) in source.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            target.Properties[name] = member;
            if (source.Required?.Contains(name) == true)
            {
                target.Required.Add(name);
            }
        }

        if ((source as OpenApiSchema)?.Metadata?.TryGetValue(AbsentWhenUnset, out var absent) == true)
        {
            Absent(target).UnionWith((HashSet<string>)absent);
        }
    }

    /// <summary>
    /// Replaces the component <paramref name="component"/> with one variant per
    /// value of its <paramref name="discriminator"/> member — a <c>oneOf</c>,
    /// each a copy of the component whose discriminator is that value and
    /// whose listed members have that value's schemas. For a member the wire
    /// shapes by a sibling (a candidate's <c>payload</c> by its
    /// <c>entityType</c>), which the serializer cannot say from a CLR type.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="component">The component's name.</param>
    /// <param name="discriminator">The member whose value picks the variant.</param>
    /// <param name="variants">Each value, and the members it reshapes.</param>
    public static void Discriminate(OpenApiDocument document, string component, string discriminator, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IOpenApiSchema>> variants)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(variants);
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("The document has no schemas.");
        if (!schemas.TryGetValue(component, out var found) || found is not OpenApiSchema { Properties: not null } original)
        {
            throw new InvalidOperationException($"The document has no object schema {component}.");
        }

        schemas[component] = new OpenApiSchema
        {
            Description = original.Description,
            OneOf = [.. variants.Select(variant =>
            {
                var copy = Copy(original);
                copy.Description = null;
                var members = copy.Properties!;
                members[discriminator] = new OpenApiSchema { Type = JsonSchemaType.String, Const = variant.Key };
                foreach (var (name, shape) in variant.Value)
                {
                    members[name] = shape;
                }

                return (IOpenApiSchema)copy;
            })],
        };
    }

    /// <summary>A copy of <paramref name="schema"/> whose properties and required list can change without changing it.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The copy.</returns>
    public static OpenApiSchema Copy(IOpenApiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var copy = (OpenApiSchema)schema.CreateShallowCopy();
        copy.Properties = new Dictionary<string, IOpenApiSchema>(schema.Properties ?? new Dictionary<string, IOpenApiSchema>());
        copy.Required = new HashSet<string>(schema.Required ?? new HashSet<string>());
        if (copy.Metadata is { } metadata)
        {
            copy.Metadata = new Dictionary<string, object>(metadata);
            if (metadata.TryGetValue(AbsentWhenUnset, out var absent))
            {
                copy.Metadata[AbsentWhenUnset] = new HashSet<string>((HashSet<string>)absent, StringComparer.Ordinal);
            }
        }

        return copy;
    }

    private static HashSet<string> Absent(OpenApiSchema schema)
    {
        schema.Metadata ??= new Dictionary<string, object>();
        if (!schema.Metadata.TryGetValue(AbsentWhenUnset, out var names))
        {
            schema.Metadata[AbsentWhenUnset] = names = new HashSet<string>(StringComparer.Ordinal);
        }

        return (HashSet<string>)names;
    }

    // Every type a route declares as its body (RequestBody<T>), and the host
    // types inside one (a domain type's members follow the domain's rule).
    private static HashSet<Type> RequestBodies(IServiceProvider services)
    {
        var found = new HashSet<Type>();
        var pending = new Stack<Type>(services.GetRequiredService<EndpointDataSource>().Endpoints.SelectMany(e => e.Metadata.OfType<RequestBodyMetadata>()).Select(m => m.Type));
        while (pending.TryPop(out var type))
        {
            if (type.Namespace?.StartsWith("Insolvia.Domain", StringComparison.Ordinal) != false || !type.Namespace.StartsWith("Insolvia.", StringComparison.Ordinal) || !found.Add(type))
            {
                continue;
            }

            foreach (var member in type.GetProperties().Select(p => p.PropertyType).SelectMany(t => t.IsGenericType ? t.GetGenericArguments() : [t]))
            {
                pending.Push(Nullable.GetUnderlyingType(member) ?? member);
            }
        }

        return found;
    }

    // ...and a member left out when unset is absent, never null: the wire has
    // no such member that is null. (A request's nullable member keeps its
    // null — a PATCH clears with it.)
    private static void NeverNullWhenOptional(OpenApiSchema schema)
    {
        if (schema.Metadata?.TryGetValue(AbsentWhenUnset, out var names) != true)
        {
            return;
        }

        foreach (var name in (HashSet<string>)names!)
        {
            if (schema.Properties?.TryGetValue(name, out var member) != true || member is not OpenApiSchema concrete)
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

/// <summary>Endpoint metadata: a query-string parameter the route reads (<see cref="WireOpenApi.QueryParameter"/>).</summary>
/// <param name="Name">Its name.</param>
/// <param name="Description">What it does, and its form.</param>
/// <param name="Required">Whether the route refuses a request without it.</param>
public sealed record QueryParameterMetadata(string Name, string Description, bool Required);

/// <summary>What one host tells <see cref="WireOpenApi"/>.</summary>
/// <param name="Title">The document's title.</param>
/// <param name="Description">What the document opens with (Markdown): who calls it, and how.</param>
/// <param name="Schemes">Its security schemes, by name.</param>
/// <param name="SchemeOf">The scheme an endpoint's metadata requires; null for an open route.</param>
/// <param name="PermissionsOf">The permissions an endpoint's metadata requires, as <c>feature:level</c>.</param>
/// <param name="Refine">The host's own pass over the finished document, for shapes its types cannot state (<see cref="WireOpenApi.Discriminate"/>).</param>
public sealed record WireOpenApiOptions(
    string Title,
    string Description,
    IReadOnlyDictionary<string, OpenApiSecurityScheme> Schemes,
    Func<IList<object>, string?> SchemeOf,
    Func<IList<object>, IEnumerable<string>> PermissionsOf,
    Func<OpenApiDocument, OpenApiDocumentTransformerContext, CancellationToken, Task>? Refine = null);
