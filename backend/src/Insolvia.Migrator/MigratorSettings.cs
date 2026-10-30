using System.Globalization;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Insolvia.Data.Connections;
using Npgsql;

namespace Insolvia.Migrator;

/// <summary>
/// Where the migrator connects, and as whom. Migrations, role provisioning
/// and the purge log in as the RDS master user, whose RDS-managed secret only
/// the migrator may read: creating roles needs it (ADR 0028 § Data). The
/// seeder logs in as the <see cref="ServiceRoles.Migrator"/> role itself —
/// with an IAM token deployed, like every other service role — so its
/// statements carry that role's grants and no more.
/// </summary>
/// <remarks>
/// <para>Local: <c>INSOLVIA_DB_CONNECTION_STRING</c> (the compose superuser)
/// and <c>INSOLVIA_DB_ROLE_PASSWORD</c>, the password the service roles get.</para>
/// <para>Deployed: <c>INSOLVIA_DB_ENDPOINT</c>, <c>INSOLVIA_DB_PORT</c>,
/// <c>INSOLVIA_DB_DATABASE_NAME</c> and <c>INSOLVIA_DB_MASTER_SECRET_ARN</c>
/// — the SSM contract's <c>/insolvia/&lt;env&gt;/db/{endpoint,port,database-name,master-secret-arn}</c>
/// — and <c>AWS_REGION</c> (set by Lambda), which signs the IAM tokens.</para>
/// <para>The master must never become a member of a role that holds
/// <c>rds_iam</c>: RDS then refuses its password (staging, 2026-10-30,
/// <c>28000: PAM authentication failed</c>). <see cref="RoleProvisioner"/>
/// keeps that true, and <see cref="OpenAsMasterAsync"/> gets back in with a
/// token when it was not.</para>
/// </remarks>
public sealed record MigratorSettings
{
    // A migration may rewrite a table; the Lambda's own timeout is the real bound.
    private const int CommandTimeoutSeconds = 300;

    private const int PoolSize = 2;

    private const string ApplicationName = "insolvia-migrator";

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

    /// <summary>The AWS region that signs IAM tokens (deployed).</summary>
    public string? Region { get; init; }

    /// <summary>The service roles' password on a database without <c>rds_iam</c>.</summary>
    public string? RolePassword { get; init; }

    /// <summary>A test seam: mints the IAM tokens instead of <see cref="RdsIamTokenProvider"/>.</summary>
    internal IDatabaseTokenProvider? Tokens { get; init; }

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
            Region = getVariable("AWS_REGION"),
            RolePassword = rolePassword,
        };
    }

    /// <summary>
    /// The data source the run uses. Deployed, the master's password comes
    /// from the master secret (it rotates, so it is read per run, never cached
    /// in configuration); every deployed connection requires TLS.
    /// </summary>
    /// <param name="cancellationToken">Cancels the secret read and the first open.</param>
    /// <param name="role">
    /// Null: the master's login (migrations, roles, purge). A service role —
    /// the seeder passes <see cref="ServiceRoles.Migrator"/> — logs in AS that
    /// role: with an IAM token deployed (the provisioner gave it <c>rds_iam</c>
    /// and no password), with <c>INSOLVIA_DB_ROLE_PASSWORD</c> locally. It is
    /// never taken with <c>SET ROLE</c> on the master's login, because that
    /// needs the master to be a member of a role holding <c>rds_iam</c>.
    /// </param>
    public async Task<NpgsqlDataSource> CreateDataSourceAsync(CancellationToken cancellationToken, string? role = null)
    {
        if (ConnectionString is not null)
        {
            var local = new NpgsqlConnectionStringBuilder(ConnectionString);
            if (role is not null)
            {
                if (string.IsNullOrEmpty(RolePassword))
                {
                    throw new InvalidOperationException(
                        $"Logging in as the {role} role locally needs its password (INSOLVIA_DB_ROLE_PASSWORD), the one the migrator gave it.");
                }

                local.Username = role;
                local.Password = RolePassword;
            }

            return InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = local.ConnectionString, ApplicationName = ApplicationName });
        }

        if (role is not null)
        {
            return InsolviaDataSource.Create(IamSettings(role), Tokens);
        }

        using var secrets = new AmazonSecretsManagerClient();
        var secret = await secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = MasterSecretArn }, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(secret.SecretString);
        var username = json.RootElement.GetProperty("username").GetString()
            ?? throw new InvalidOperationException("The master secret names no username.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Endpoint,
            Port = Port,
            Database = DatabaseName,
            Username = username,
            Password = json.RootElement.GetProperty("password").GetString(),
            SslMode = SslMode.Require,
            // Aurora at minimum capacity 0 resumes in ~15 s.
            Timeout = 30,
            CommandTimeout = CommandTimeoutSeconds,
            MaxPoolSize = PoolSize,
            ApplicationName = ApplicationName,
        };

        return await OpenAsMasterAsync(
            InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = builder.ConnectionString }),
            () => InsolviaDataSource.Create(IamSettings(username), Tokens),
            Console.WriteLine,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The IAM-token settings for logging in as <paramref name="username"/> (deployed).</summary>
    internal DatabaseSettings IamSettings(string username) => new()
    {
        Endpoint = Endpoint,
        Port = Port,
        DatabaseName = DatabaseName,
        Username = username,
        Region = Region,
        SslMode = SslMode.Require,
        MaxPoolSize = PoolSize,
        CommandTimeout = CommandTimeoutSeconds,
        ApplicationName = ApplicationName,
    };

    /// <summary>
    /// Opens the master's login with its password; when RDS refuses the
    /// password because the master has become an indirect <c>rds_iam</c>
    /// member (<see cref="IsIamOnlyRefusal"/>), opens it once more with an IAM
    /// token for the same user. That is the way back in after the lockout an
    /// earlier migrator caused: the run that gets in with the token has
    /// <see cref="RoleProvisioner"/> revoke the membership, and the password
    /// works again on the next run. <see cref="DatabaseWake.OpenAsync"/>
    /// stays around both opens, so a paused Aurora still resumes first.
    /// </summary>
    /// <param name="withPassword">The master's login with the secret's password; disposed when it is not returned.</param>
    /// <param name="withToken">Builds the master's login with an IAM token.</param>
    /// <param name="log">Where the one fallback line goes (the Lambda's log).</param>
    /// <param name="cancellationToken">Cancels the opens.</param>
    /// <param name="open">A test seam for the open; default <see cref="DatabaseWake.OpenAsync"/>.</param>
    /// <returns>The data source that opened.</returns>
    internal static async Task<NpgsqlDataSource> OpenAsMasterAsync(
        NpgsqlDataSource withPassword,
        Func<NpgsqlDataSource> withToken,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<NpgsqlDataSource, CancellationToken, Task<NpgsqlConnection>>? open = null)
    {
        open ??= (source, token) => DatabaseWake.OpenAsync(source, token);
        try
        {
            var probe = await open(withPassword, cancellationToken).ConfigureAwait(false);
            await probe.DisposeAsync().ConfigureAwait(false);
            return withPassword;
        }
        catch (PostgresException refused) when (IsIamOnlyRefusal(refused))
        {
            await withPassword.DisposeAsync().ConfigureAwait(false);
            log($"migrator: RDS refused the master's password ({refused.SqlState}: {refused.MessageText}): the master reaches rds_iam through the service roles it created, as RDS arranges. Signing in with an IAM token.");
        }
        catch
        {
            await withPassword.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var fallback = withToken();
        try
        {
            var probe = await open(fallback, cancellationToken).ConfigureAwait(false);
            await probe.DisposeAsync().ConfigureAwait(false);
            return fallback;
        }
        catch
        {
            await fallback.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// True when RDS refused a password because the login may only use IAM:
    /// SQLSTATE 28000 from its PAM module. A wrong password is 28P01 and
    /// never takes the fallback.
    /// </summary>
    internal static bool IsIamOnlyRefusal(PostgresException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.SqlState == PostgresErrorCodes.InvalidAuthorizationSpecification
            && error.MessageText.Contains("PAM", StringComparison.Ordinal);
    }
}
