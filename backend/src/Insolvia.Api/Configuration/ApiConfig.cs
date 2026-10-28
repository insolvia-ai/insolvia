namespace Insolvia.Api.Configuration;

/// <summary>
/// The service configuration, parsed once at composition (Python's
/// <c>AppConfig</c> / <c>load_config</c> in services/api core/config.py). The
/// variable names are the Python service's, so one environment drives either
/// backend; <see cref="Load"/> is the only place that reads them.
/// </summary>
/// <remarks>
/// <para>Every string is "set and non-empty, or null" — Python's
/// <c>source.get(NAME) or None</c>.</para>
/// <para>The DynamoDB-era names (<c>WAITLIST_TABLE_NAME</c>,
/// <c>CASE_TABLE_NAME</c>, <c>CASE_ACCESS_LOG_TABLE_NAME</c>,
/// <c>FIRM_TABLE_NAME</c>) are read so the two backends see the same
/// environment, but nothing in this host composes a store from them: the
/// stores are Postgres (<c>INSOLVIA_DB_*</c>, owned by Insolvia.Data), and
/// the Lambda boot does not require them (<see cref="LambdaBootRequirements"/>).</para>
/// </remarks>
public sealed record ApiConfig
{
    /// <summary>
    /// The per-environment CORS allowlist (issue #68). Exact origins only — no
    /// wildcard, ever; www is deliberately absent (the marketing site's
    /// waitlist call is server-to-server and sends no Origin).
    /// </summary>
    private static readonly Dictionary<string, string[]> CorsOrigins = new(StringComparer.Ordinal)
    {
        ["production"] = ["https://app.insolvia.ai"],
        ["staging"] = ["https://staging-app.insolvia.ai"],
        ["local"] = [],
    };

    /// <summary>Where the marketing site lives, per environment (issue #80).</summary>
    private static readonly Dictionary<string, string> MarketingOrigins = new(StringComparer.Ordinal)
    {
        ["production"] = "https://www.insolvia.ai",
        ["staging"] = "https://staging-www.insolvia.ai",
        ["local"] = "http://localhost:3000",
    };

    /// <summary>Where the app lives, per environment (the portal invitation's origin, ADR 0023).</summary>
    private static readonly Dictionary<string, string> AppOrigins = new(StringComparer.Ordinal)
    {
        ["production"] = "https://app.insolvia.ai",
        ["staging"] = "https://staging-app.insolvia.ai",
        ["local"] = "http://localhost:3000",
    };

    /// <summary>The environment, from <c>INSOLVIA_ENV</c>.</summary>
    public required InsolviaEnvironment Environment { get; init; }

    /// <summary><c>WAITLIST_TABLE_NAME</c> (DynamoDB era; read, not composed).</summary>
    public string? WaitlistTableName { get; init; }

    /// <summary><c>CASE_TABLE_NAME</c> (DynamoDB era; read, not composed).</summary>
    public string? CaseTableName { get; init; }

    /// <summary><c>CASE_ACCESS_LOG_TABLE_NAME</c> (DynamoDB era; read, not composed).</summary>
    public string? CaseAccessLogTableName { get; init; }

    /// <summary><c>FIRM_TABLE_NAME</c> (DynamoDB era; read, not composed).</summary>
    public string? FirmTableName { get; init; }

    /// <summary><c>AUTH_USER_POOL_ID</c>: the pool <c>POST /v1/firm/users</c> mints accounts in.</summary>
    public string? AuthUserPoolId { get; init; }

    /// <summary><c>CASE_DOCUMENT_BUCKET</c>: the bucket holding a case's source documents.</summary>
    public string? CaseDocumentBucket { get; init; }

    /// <summary><c>JOB_QUEUE_URL</c>: the pipeline queue (ADR 0018). Unset is a real deployed state (503 on accept).</summary>
    public string? JobQueueUrl { get; init; }

    /// <summary><c>FILING_QUEUE_URL</c>: the filing worker's queue (ADR 0024). Unset: approving answers 503.</summary>
    public string? FilingQueueUrl { get; init; }

    /// <summary><c>ANTHROPIC_API_KEY</c>: carried for the worker; no API path reads it.</summary>
    public string? AnthropicApiKey { get; init; }

    /// <summary><c>MAILER_API_URL</c>: the mailer's base URL; unset means the in-memory mailer.</summary>
    public string? MailerApiUrl { get; init; }

    /// <summary><c>UNSUBSCRIBE_SECRET</c>: the unsubscribe HMAC key. Unset is a refusal, never unsigned tokens.</summary>
    public string? UnsubscribeSecret { get; init; }

    /// <summary><c>AUTH_ISSUER_URL</c>: the Cognito pool's issuer. Unset fails every protected route CLOSED.</summary>
    public string? AuthIssuerUrl { get; init; }

    /// <summary><c>AUTH_CLIENT_ID</c>: the staff (web) app client every staff token must name.</summary>
    public string? AuthClientId { get; init; }

    /// <summary><c>AUTH_PORTAL_CLIENT_ID</c>: the client portal's own app client (ADR 0023), disjoint from the staff one.</summary>
    public string? AuthPortalClientId { get; init; }

    /// <summary>The marketing site's origin for this environment.</summary>
    public string MarketingOrigin { get; init; } = MarketingOrigins["local"];

    /// <summary>The app's origin for this environment.</summary>
    public string AppOrigin { get; init; } = AppOrigins["local"];

    /// <summary>The exact origins CORS echoes in this environment.</summary>
    public IReadOnlyList<string> CorsAllowedOrigins { get; init; } = [];

    /// <summary>Whether loopback dev origins pass CORS — everywhere but production.</summary>
    public bool CorsAllowLocalhost { get; init; } = true;

    /// <summary>
    /// Builds the configuration from <paramref name="read"/> (a variable
    /// name to its value). Throws <see cref="ArgumentException"/> for an
    /// <c>INSOLVIA_ENV</c> outside the three names — the same refusal
    /// <see cref="InsolviaEnvironment.FromVariable"/> makes.
    /// </summary>
    /// <param name="read">Reads one variable; null when it is unset.</param>
    /// <returns>The parsed configuration.</returns>
    public static ApiConfig Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var environment = InsolviaEnvironment.FromVariable(read("INSOLVIA_ENV"));
        string? Optional(string name) => read(name) is { Length: > 0 } value ? value : null;
        return new ApiConfig
        {
            Environment = environment,
            WaitlistTableName = Optional("WAITLIST_TABLE_NAME"),
            CaseTableName = Optional("CASE_TABLE_NAME"),
            CaseAccessLogTableName = Optional("CASE_ACCESS_LOG_TABLE_NAME"),
            FirmTableName = Optional("FIRM_TABLE_NAME"),
            AuthUserPoolId = Optional("AUTH_USER_POOL_ID"),
            CaseDocumentBucket = Optional("CASE_DOCUMENT_BUCKET"),
            JobQueueUrl = Optional("JOB_QUEUE_URL"),
            FilingQueueUrl = Optional("FILING_QUEUE_URL"),
            AnthropicApiKey = Optional("ANTHROPIC_API_KEY"),
            MailerApiUrl = Optional("MAILER_API_URL"),
            UnsubscribeSecret = Optional("UNSUBSCRIBE_SECRET"),
            AuthIssuerUrl = Optional("AUTH_ISSUER_URL"),
            AuthClientId = Optional("AUTH_CLIENT_ID"),
            AuthPortalClientId = Optional("AUTH_PORTAL_CLIENT_ID"),
            MarketingOrigin = MarketingOrigins[environment.Name],
            AppOrigin = AppOrigins[environment.Name],
            CorsAllowedOrigins = CorsOrigins[environment.Name],
            CorsAllowLocalhost = environment.Name != "production",
        };
    }
}
