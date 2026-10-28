namespace Insolvia.Admin.Configuration;

/// <summary>
/// What a deployed (Lambda) admin service refuses to boot without — the hard
/// requirements of services/admin entrypoints/api_lambda.py. EVERYTHING is
/// required here, unlike the API's mailer seam: a deployment that cannot
/// verify staff tokens, reach the firm tables, mint pool accounts or write
/// its audit row is not an admin service with a feature missing — it either
/// refuses every request (confusing) or performs unaudited mutations
/// (forbidden by #178's whole premise). Refusing to boot is discovered by the
/// deploy; the alternatives by an operator mid-provision.
/// </summary>
/// <remarks>
/// <c>FIRM_TABLE_NAME</c> and <c>ADMIN_AUDIT_TABLE_NAME</c> are not carried
/// over: the stores are Postgres, whose <c>INSOLVIA_DB_*</c> contract
/// Insolvia.Data validates at composition.
/// </remarks>
public static class LambdaBootRequirements
{
    /// <summary>The variable the Lambda runtime sets, and the hosting package's own switch.</summary>
    public const string LambdaFunctionVariable = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing requirement.</summary>
    /// <param name="config">The loaded configuration.</param>
    public static void Enforce(AdminConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var (name, value) in new[] { ("FIRM_USER_POOL_ID", config.FirmUserPoolId), ("GOOGLE_CLIENT_ID", config.GoogleClientId) })
        {
            if (value is null)
            {
                throw new InvalidOperationException($"{name} must be set for the admin Lambda entrypoint");
            }
        }
    }
}
