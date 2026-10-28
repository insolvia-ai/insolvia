namespace Insolvia.Mcp.Configuration;

/// <summary>
/// What a deployed (Lambda) MCP server refuses to boot without — the hard
/// requirements of services/mcp <c>entrypoints/mcp_lambda.py</c>, with its
/// messages. This service has no public tools and no degraded mode: booting
/// without auth would 401 every call (which looks like a harness bug and is
/// discovered by attorneys); refusing to boot is discovered by the deploy.
/// </summary>
/// <remarks>
/// The Python's other two requirements — the case table with its access-log
/// table, and the firm table — became the database: on the Lambda path the
/// composition calls <c>DatabaseSettings.FromEnvironment</c>, which throws
/// when the <c>INSOLVIA_DB_*</c> contract is incomplete, and the access log
/// lives in that same database, so the "case data without its access log"
/// half-state the Python pair rule refused cannot be configured at all.
/// </remarks>
public static class LambdaBootRequirements
{
    /// <summary>The variable the Lambda runtime sets, and the hosting package's own switch.</summary>
    public const string LambdaFunctionVariable = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing requirement.</summary>
    /// <param name="config">The loaded configuration.</param>
    public static void Enforce(McpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.AuthIssuerUrl is null || config.AuthClientIds.Count == 0)
        {
            throw new InvalidOperationException("AUTH_ISSUER_URL and AUTH_CLIENT_IDS must be set for the Lambda entrypoint");
        }
    }
}
