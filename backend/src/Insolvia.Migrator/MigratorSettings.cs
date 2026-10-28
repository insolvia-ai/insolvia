using System.Globalization;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Insolvia.Data.Connections;
using Npgsql;

namespace Insolvia.Migrator;

/// <summary>
/// Where the migrator connects, and as whom. It is the one service that does
/// not log in as its own IAM role: creating roles needs the RDS master user,
/// whose RDS-managed secret only the migrator may read (ADR 0028 § Data).
/// </summary>
/// <remarks>
/// <para>Local: <c>INSOLVIA_DB_CONNECTION_STRING</c> (the compose superuser)
/// and <c>INSOLVIA_DB_ROLE_PASSWORD</c>, the password the service roles get.</para>
/// <para>Deployed: <c>INSOLVIA_DB_ENDPOINT</c>, <c>INSOLVIA_DB_PORT</c>,
/// <c>INSOLVIA_DB_DATABASE_NAME</c> and <c>INSOLVIA_DB_MASTER_SECRET_ARN</c>
/// — the SSM contract's <c>/insolvia/&lt;env&gt;/db/{endpoint,port,database-name,master-secret-arn}</c>.</para>
/// </remarks>
public sealed record MigratorSettings
{
    /// <summary>A local connection string.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The cluster's writer endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The cluster's port.</summary>
    public int Port { get; init; } = 5432;

    /// <summary>The database name.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>The ARN of the RDS-managed master user secret.</summary>
    public string? MasterSecretArn { get; init; }

    /// <summary>The service roles' password on a database without <c>rds_iam</c>.</summary>
    public string? RolePassword { get; init; }

    /// <summary>Reads the settings from environment variables.</summary>
    public static MigratorSettings FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var connectionString = getVariable(DatabaseSettings.ConnectionStringVariable);
        var rolePassword = getVariable("INSOLVIA_DB_ROLE_PASSWORD");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new MigratorSettings { ConnectionString = connectionString, RolePassword = rolePassword };
        }

        var endpoint = getVariable("INSOLVIA_DB_ENDPOINT");
        var databaseName = getVariable("INSOLVIA_DB_DATABASE_NAME");
        var secretArn = getVariable("INSOLVIA_DB_MASTER_SECRET_ARN");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(secretArn))
        {
            throw new InvalidOperationException(
                $"The migrator is not configured. Set {DatabaseSettings.ConnectionStringVariable} (local), or INSOLVIA_DB_ENDPOINT, INSOLVIA_DB_DATABASE_NAME and INSOLVIA_DB_MASTER_SECRET_ARN (deployed).");
        }

        var portText = getVariable("INSOLVIA_DB_PORT");
        return new MigratorSettings
        {
            Endpoint = endpoint,
            Port = string.IsNullOrWhiteSpace(portText) ? 5432 : int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture),
            DatabaseName = databaseName,
            MasterSecretArn = secretArn,
            RolePassword = rolePassword,
        };
    }

    /// <summary>
    /// The data source the run uses. Deployed, this reads the master secret
    /// (it rotates, so it is read per run, never cached in configuration) and
    /// requires TLS.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateDataSourceAsync(CancellationToken cancellationToken)
    {
        if (ConnectionString is not null)
        {
            return InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = ConnectionString, ApplicationName = "insolvia-migrator" });
        }

        using var secrets = new AmazonSecretsManagerClient();
        var secret = await secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = MasterSecretArn }, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(secret.SecretString);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Endpoint,
            Port = Port,
            Database = DatabaseName,
            Username = json.RootElement.GetProperty("username").GetString(),
            Password = json.RootElement.GetProperty("password").GetString(),
            SslMode = SslMode.Require,
            // Aurora at minimum capacity 0 resumes in ~15 s.
            Timeout = 30,
            // A migration may rewrite a table; the Lambda's own timeout is the real bound.
            CommandTimeout = 300,
            MaxPoolSize = 2,
            ApplicationName = "insolvia-migrator",
        };
        return InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = builder.ConnectionString });
    }
}
