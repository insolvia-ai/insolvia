namespace Insolvia.Api.Configuration;

/// <summary>
/// What a deployed (Lambda) API refuses to boot without — the hard
/// requirements of services/api entrypoints/api_lambda.py, in its order and
/// with its messages. Refusing to boot is discovered by the deploy; booting
/// without a verifier would 401 every protected route and be discovered by
/// users. <c>dotnet run</c>, docker compose and the test host are the
/// development server's shape instead: missing auth is tolerated there and
/// every protected route fails CLOSED with a 401.
/// </summary>
/// <remarks>
/// Not carried over: <c>WAITLIST_TABLE_NAME</c>, <c>CASE_TABLE_NAME</c> +
/// <c>CASE_ACCESS_LOG_TABLE_NAME</c> and <c>FIRM_TABLE_NAME</c>. They name
/// DynamoDB tables; this host's stores are Postgres, whose connection
/// settings Insolvia.Data validates (ADR 0028). The two that still name
/// something this backend will use — the Cognito pool and the document
/// bucket — are kept. The KMS key aliases are <see cref="SealingKeys"/>'
/// (resolved at composition, so the boot refuses without them too).
/// </remarks>
public static class LambdaBootRequirements
{
    /// <summary>The variable the Lambda runtime sets, and the hosting package's own switch.</summary>
    public const string LambdaFunctionVariable = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing requirement.</summary>
    /// <param name="config">The loaded configuration.</param>
    public static void Enforce(ApiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.AuthIssuerUrl is null || config.AuthClientId is null)
        {
            throw new InvalidOperationException("AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint");
        }

        // Not required — an unset portal client fails every portal route
        // closed and leaves staff routes alone — but the one misconfiguration
        // that would make the portal an open door is refused.
        if (config.AuthPortalClientId is not null && config.AuthPortalClientId == config.AuthClientId)
        {
            throw new InvalidOperationException(
                "AUTH_PORTAL_CLIENT_ID must differ from AUTH_CLIENT_ID — the disjoint client ids ARE the audience check between staff and clients (ADR 0023)");
        }

        if (config.AuthUserPoolId is null)
        {
            throw new InvalidOperationException("AUTH_USER_POOL_ID must be set for the Lambda entrypoint");
        }

        if (config.CaseDocumentBucket is null)
        {
            throw new InvalidOperationException("CASE_DOCUMENT_BUCKET must be set for the Lambda entrypoint");
        }
    }
}
