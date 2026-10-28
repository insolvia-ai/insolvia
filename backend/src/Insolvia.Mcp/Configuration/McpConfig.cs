using Insolvia.Core;

namespace Insolvia.Mcp.Configuration;

/// <summary>
/// The service configuration (services/mcp <c>core/config.py</c>), parsed and
/// validated once at composition. <see cref="Load"/> is the only real
/// constructor; nothing else in this host reads the environment.
/// </summary>
/// <remarks>
/// Not carried over: <c>CASE_TABLE_NAME</c>, <c>CASE_ACCESS_LOG_TABLE_NAME</c>
/// and <c>FIRM_TABLE_NAME</c>. They named DynamoDB tables; this host's stores
/// are Postgres, whose <c>INSOLVIA_DB_*</c> contract Insolvia.Data validates
/// (ADR 0028). The Cognito pair and the canonical resource URI are kept.
/// </remarks>
public sealed record McpConfig
{
    /// <summary>The three environments, in Python's <c>ENVIRONMENTS</c> order.</summary>
    public static readonly IReadOnlyList<string> Environments = ["local", "staging", "production"];

    // The CANONICAL RESOURCE URI per environment (RFC 8707 / RFC 9728): the
    // string in the protected-resource metadata, the `resource` parameter a
    // harness sends, and the URL a directory listing names — one string,
    // everywhere, which is why the service has its own hostname (ADR 0016).
    // `local` is the development server's default bind.
    private static readonly Dictionary<string, string> ResourceUrls = new(StringComparer.Ordinal)
    {
        ["production"] = "https://mcp.insolvia.ai/mcp",
        ["staging"] = "https://staging-mcp.insolvia.ai/mcp",
        ["local"] = "http://127.0.0.1:8788/mcp",
    };

    /// <summary><c>local</c>, <c>staging</c> or <c>production</c>.</summary>
    public required string Environment { get; init; }

    /// <summary>The Cognito pool's OIDC issuer, or null (every call then answers 401).</summary>
    public string? AuthIssuerUrl { get; init; }

    /// <summary>
    /// The allowlist of MCP app client ids a token may name — one
    /// pre-registered client per harness, DISJOINT from the app's client id
    /// (ADR 0016): the audience check Cognito's aud-less access tokens cannot
    /// carry. Empty means not configured.
    /// </summary>
    public IReadOnlyList<string> AuthClientIds { get; init; } = [];

    /// <summary>The canonical resource URI (<c>…/mcp</c>).</summary>
    public string ResourceUrl { get; init; } = ResourceUrls["local"];

    /// <summary>
    /// Build the configuration. <c>INSOLVIA_ENV</c> defaults to <c>local</c>;
    /// an empty value reads as unset everywhere; <c>MCP_RESOURCE_URL</c>
    /// overrides the canonical URI (local dev on a non-default port), and
    /// staging and prod otherwise take their environment's constant so the
    /// metadata cannot drift from the hostname.
    /// </summary>
    /// <param name="getVariable">Reads one variable (null when unset).</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="ValidationException">An unknown <c>INSOLVIA_ENV</c>.</exception>
    public static McpConfig Load(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        string? Read(string name) => getVariable(name) is { Length: > 0 } value ? value : null;

        var environment = Read("INSOLVIA_ENV") ?? "local";
        if (!Environments.Contains(environment, StringComparer.Ordinal))
        {
            throw new ValidationException($"INSOLVIA_ENV must be one of {string.Join(", ", Environments)}, got '{environment}'");
        }

        return new McpConfig
        {
            Environment = environment,
            AuthIssuerUrl = Read("AUTH_ISSUER_URL"),
            AuthClientIds = ClientIds(Read("AUTH_CLIENT_IDS")),
            ResourceUrl = Read("MCP_RESOURCE_URL") ?? ResourceUrls[environment],
        };
    }

    /// <summary>
    /// The RFC 9728 metadata document's URL for <see cref="ResourceUrl"/>:
    /// the well-known prefix inserted between the origin and the path —
    /// what the 401 challenge's <c>resource_metadata</c> names.
    /// </summary>
    public Uri ResourceMetadataUrl
    {
        get
        {
            var resource = new Uri(ResourceUrl);
            return new Uri(resource.GetLeftPart(UriPartial.Authority) + ResourceMetadata.WellKnownPrefix + resource.AbsolutePath.TrimEnd('/'));
        }
    }

    /// <summary>The path the metadata document is served at on this host.</summary>
    public string ResourceMetadataPath => ResourceMetadata.WellKnownPrefix + new Uri(ResourceUrl).AbsolutePath.TrimEnd('/');

    // Comma-separated, whitespace-tolerant, empties dropped: a trailing comma
    // in an SSM parameter cannot smuggle an empty string into the allowlist.
    private static IReadOnlyList<string> ClientIds(string? raw) =>
        raw is null
            ? []
            : [.. raw.Split(',').Select(value => value.Trim()).Where(value => value.Length > 0)];
}

/// <summary>RFC 9728's well-known location.</summary>
public static class ResourceMetadata
{
    /// <summary>Inserted between the origin and the resource's path.</summary>
    public const string WellKnownPrefix = "/.well-known/oauth-protected-resource";
}
