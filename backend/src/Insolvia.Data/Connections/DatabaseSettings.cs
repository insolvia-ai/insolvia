using System.Globalization;
using Npgsql;

namespace Insolvia.Data.Connections;

/// <summary>How a service reaches its database.</summary>
public enum DatabaseAuthentication
{
    /// <summary>A full connection string with a password: local and tests.</summary>
    ConnectionString,

    /// <summary>An RDS IAM token per connection, over TLS: staging and prod.</summary>
    Iam,
}

/// <summary>
/// Where the database is and who to connect as, read from the environment.
/// </summary>
/// <remarks>
/// <para>
/// Local (docker compose, <c>dotnet run</c>, tests) sets
/// <c>INSOLVIA_DB_CONNECTION_STRING</c> and nothing else. A deployed Lambda
/// sets the four values the SSM contract <c>/insolvia/&lt;env&gt;/db/*</c>
/// publishes, plus its own role:
/// </para>
/// <list type="table">
/// <item><term><c>INSOLVIA_DB_ENDPOINT</c></term><description><c>/insolvia/&lt;env&gt;/db/endpoint</c></description></item>
/// <item><term><c>INSOLVIA_DB_PORT</c></term><description><c>/insolvia/&lt;env&gt;/db/port</c> (default 5432)</description></item>
/// <item><term><c>INSOLVIA_DB_DATABASE_NAME</c></term><description><c>/insolvia/&lt;env&gt;/db/database-name</c></description></item>
/// <item><term><c>INSOLVIA_DB_USERNAME</c></term><description>the service's Postgres role (<c>api</c>, <c>worker</c>, …): a literal in that Lambda's Terraform, the same name its <c>rds-db:connect</c> grant carries</description></item>
/// <item><term><c>AWS_REGION</c></term><description>set by Lambda itself; signs the token</description></item>
/// </list>
/// <para>A connection string wins when both are present, so a developer can
/// point a host at a local database without unsetting the deployed values.</para>
/// </remarks>
public sealed record DatabaseSettings
{
    /// <summary>The environment variable holding a local connection string.</summary>
    public const string ConnectionStringVariable = "INSOLVIA_DB_CONNECTION_STRING";

    /// <summary>Lambda serves one request at a time per instance; a handful of
    /// connections covers a request's parallel queries without letting a
    /// burst of instances exhaust Aurora's connection slots.</summary>
    public const int LambdaMaxPoolSize = 5;

    /// <summary>A full Npgsql connection string (local).</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The cluster's writer endpoint (deployed).</summary>
    public string? Endpoint { get; init; }

    /// <summary>The cluster's port (deployed).</summary>
    public int Port { get; init; } = 5432;

    /// <summary>The database name (deployed).</summary>
    public string? DatabaseName { get; init; }

    /// <summary>The Postgres role this service connects as (deployed).</summary>
    public string? Username { get; init; }

    /// <summary>The AWS region that signs the IAM token (deployed).</summary>
    public string? Region { get; init; }

    /// <summary>TLS for the IAM path. A token is a bearer credential for 15
    /// minutes, so it never travels in clear.</summary>
    public SslMode SslMode { get; init; } = SslMode.Require;

    /// <summary>The pool's ceiling for the IAM path.</summary>
    public int MaxPoolSize { get; init; } = LambdaMaxPoolSize;

    /// <summary>Shows in <c>pg_stat_activity</c>: which service holds a connection.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>Which path these settings take.</summary>
    public DatabaseAuthentication Authentication =>
        string.IsNullOrWhiteSpace(ConnectionString) ? DatabaseAuthentication.Iam : DatabaseAuthentication.ConnectionString;

    /// <summary>Reads the settings from environment variables.</summary>
    /// <param name="getVariable">Usually <see cref="Environment.GetEnvironmentVariable(string)"/>; a test passes a dictionary.</param>
    /// <param name="applicationName">The service's name, for <c>pg_stat_activity</c>.</param>
    public static DatabaseSettings FromEnvironment(Func<string, string?> getVariable, string? applicationName = null)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        var connectionString = getVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabaseSettings { ConnectionString = connectionString, ApplicationName = applicationName };
        }

        var endpoint = getVariable("INSOLVIA_DB_ENDPOINT");
        var databaseName = getVariable("INSOLVIA_DB_DATABASE_NAME");
        var username = getVariable("INSOLVIA_DB_USERNAME");
        var region = getVariable("AWS_REGION");
        var missing = new[]
            {
                ("INSOLVIA_DB_ENDPOINT", endpoint),
                ("INSOLVIA_DB_DATABASE_NAME", databaseName),
                ("INSOLVIA_DB_USERNAME", username),
                ("AWS_REGION", region),
            }
            .Where(pair => string.IsNullOrWhiteSpace(pair.Item2))
            .Select(pair => pair.Item1)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database is not configured. Set {ConnectionStringVariable} (local), or all of: {string.Join(", ", missing)} (deployed, IAM authentication).");
        }

        var portText = getVariable("INSOLVIA_DB_PORT");
        var port = 5432;
        if (!string.IsNullOrWhiteSpace(portText)
            && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            throw new InvalidOperationException($"INSOLVIA_DB_PORT is not a port number: '{portText}'.");
        }

        return new DatabaseSettings
        {
            Endpoint = endpoint,
            Port = port,
            DatabaseName = databaseName,
            Username = username,
            Region = region,
            ApplicationName = applicationName,
        };
    }
}
