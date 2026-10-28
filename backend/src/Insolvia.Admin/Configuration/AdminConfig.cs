namespace Insolvia.Admin.Configuration;

/// <summary>
/// The admin service's configuration, parsed once at composition (Python's
/// <c>AppConfig</c> / <c>load_config</c> in services/admin core/config.py).
/// The variable names are the Python service's, so one environment drives
/// either backend; <see cref="Load"/> is the only place that reads them.
/// </summary>
/// <remarks>
/// The DynamoDB-era names (<c>FIRM_TABLE_NAME</c>, <c>ADMIN_AUDIT_TABLE_NAME</c>)
/// are not read: this host's stores are Postgres (<c>INSOLVIA_DB_*</c>, owned
/// by Insolvia.Data), and the firm store and audit log move together on that
/// one switch (Hosting/AdminComposition.cs).
/// </remarks>
public sealed record AdminConfig
{
    /// <summary>
    /// The Workspace domain staff tokens must carry in <c>hd</c>. A constant,
    /// not an environment variable, as in Python: it is decided in this repo,
    /// changes with a code review, and a typo'd variable would lock every
    /// staff member out (or, empty, fail closed into a support mystery).
    /// </summary>
    public const string WorkspaceDomainName = "insolvia.ai";

    /// <summary>
    /// The admin portal's origins and nothing else, per environment. Exact
    /// origins only; loopback is handled apart (<see cref="CorsAllowLocalhost"/>)
    /// because the portal's dev server owns :3100 but the check deliberately
    /// does not depend on the port.
    /// </summary>
    private static readonly Dictionary<string, string[]> CorsOrigins = new(StringComparer.Ordinal)
    {
        ["production"] = ["https://admin.insolvia.ai"],
        ["staging"] = ["https://staging-admin.insolvia.ai"],
        ["local"] = [],
    };

    /// <summary>The environment, from <c>INSOLVIA_ENV</c>.</summary>
    public required InsolviaEnvironment Environment { get; init; }

    /// <summary>
    /// <c>FIRM_USER_POOL_ID</c>: the FIRM pool, where provisioning mints the
    /// first administrator's account. Deliberately not <c>AUTH_USER_POOL_ID</c>:
    /// this service never verifies tokens from that pool.
    /// </summary>
    public string? FirmUserPoolId { get; init; }

    /// <summary>
    /// <c>GOOGLE_CLIENT_ID</c>: the environment's OAuth client id (public).
    /// Unset fails CLOSED: every staff route answers 401.
    /// </summary>
    public string? GoogleClientId { get; init; }

    /// <summary>The <c>hd</c> a staff token must carry. Always <see cref="WorkspaceDomainName"/> outside tests.</summary>
    public string WorkspaceDomain { get; init; } = WorkspaceDomainName;

    /// <summary>
    /// <c>GOOGLE_JWKS_URL</c>, honoured LOCALLY ONLY (<c>INSOLVIA_ENV=local</c>
    /// and not inside Lambda): where the verifier fetches signing keys, so a
    /// developer can mint a Google-shaped token with a key of their own and
    /// drive the admin portal against <c>dotnet run</c>. The issuer, audience
    /// and <c>hd</c> checks are unchanged. A deployed service ignores it and
    /// reads Google's keys, whatever the environment says.
    /// </summary>
    public Uri? LocalJwksUrl { get; init; }

    /// <summary>The exact origins CORS echoes in this environment.</summary>
    public IReadOnlyList<string> CorsAllowedOrigins { get; init; } = [];

    /// <summary>Whether loopback dev origins pass CORS — everywhere but production.</summary>
    public bool CorsAllowLocalhost { get; init; } = true;

    /// <summary>
    /// Builds the configuration from <paramref name="read"/>. Throws
    /// <see cref="ArgumentException"/> for an <c>INSOLVIA_ENV</c> outside the
    /// three names, and for a <c>GOOGLE_JWKS_URL</c> that is not an absolute
    /// http(s) URL.
    /// </summary>
    /// <param name="read">Reads one variable; null when it is unset.</param>
    /// <returns>The parsed configuration.</returns>
    public static AdminConfig Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var environment = InsolviaEnvironment.FromVariable(read("INSOLVIA_ENV"));
        string? Optional(string name) => read(name) is { } value && value.Trim().Length > 0 ? value.Trim() : null;

        Uri? jwks = null;
        if (environment.Name == "local" && Optional("GOOGLE_JWKS_URL") is { } jwksText)
        {
            if (!Uri.TryCreate(jwksText, UriKind.Absolute, out jwks) || jwks.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException($"GOOGLE_JWKS_URL must be an absolute http(s) URL, got '{jwksText}'.", nameof(read));
            }
        }

        return new AdminConfig
        {
            Environment = environment,
            FirmUserPoolId = Optional("FIRM_USER_POOL_ID"),
            GoogleClientId = Optional("GOOGLE_CLIENT_ID"),
            LocalJwksUrl = jwks,
            CorsAllowedOrigins = CorsOrigins[environment.Name],
            CorsAllowLocalhost = environment.Name != "production",
        };
    }
}
