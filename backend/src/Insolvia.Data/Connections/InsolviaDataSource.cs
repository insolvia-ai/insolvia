using Npgsql;

namespace Insolvia.Data.Connections;

/// <summary>
/// Builds the one <see cref="NpgsqlDataSource"/> a process uses. Everything
/// that opens a connection — EF Core, plain Npgsql, the migrator — goes
/// through a data source made here, so the IAM token and the pool settings
/// are decided once.
/// </summary>
public static class InsolviaDataSource
{
    /// <summary>A token is valid for 15 minutes. Refreshing at 10 leaves a
    /// connection opened just before the refresh five minutes of margin.</summary>
    public static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>After a failed refresh (credentials not yet available, a
    /// throttled clock), try again soon rather than waiting the full interval.</summary>
    public static readonly TimeSpan TokenRetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Builds the data source. Kerberos (GSS) encryption is always off: no
    /// Insolvia database uses it, and probing for it makes Npgsql look for
    /// <c>libgssapi_krb5</c>, which the slim runtime images do not ship.
    /// On the IAM path <paramref name="tokens"/>
    /// defaults to <see cref="RdsIamTokenProvider"/> for the settings' region.
    /// </summary>
    public static NpgsqlDataSource Create(DatabaseSettings settings, IDatabaseTokenProvider? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Authentication == DatabaseAuthentication.ConnectionString)
        {
            var local = new NpgsqlDataSourceBuilder(settings.ConnectionString);
            local.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
            if (settings.ApplicationName is not null && local.ConnectionStringBuilder.ApplicationName is null)
            {
                local.ConnectionStringBuilder.ApplicationName = settings.ApplicationName;
            }

            return local.Build();
        }

        var builder = new NpgsqlDataSourceBuilder();
        var connection = builder.ConnectionStringBuilder;
        connection.Host = settings.Endpoint;
        connection.Port = settings.Port;
        connection.Database = settings.DatabaseName;
        connection.Username = settings.Username;
        connection.SslMode = settings.SslMode;
        connection.GssEncryptionMode = GssEncryptionMode.Disable;
        connection.ApplicationName = settings.ApplicationName;

        // Lambda-shaped pool: few connections, none kept warm on purpose, and
        // idle ones closed quickly — a frozen instance's sockets are often
        // dead by the time it thaws.
        connection.MaxPoolSize = settings.MaxPoolSize;
        connection.MinPoolSize = 0;
        connection.ConnectionIdleLifetime = 60;

        // Aurora Serverless v2 at minimum capacity 0 takes ~15 s to resume
        // (ADR 0028 § Data). The default 15 s connect timeout would fail the
        // first request after idle, so wait for it instead.
        connection.Timeout = 30;

        var provider = tokens ?? new RdsIamTokenProvider(
            settings.Region ?? throw new InvalidOperationException("IAM database authentication needs a region (AWS_REGION)."));
        var host = settings.Endpoint!;
        var port = settings.Port;
        var username = settings.Username!;
        builder.UsePeriodicPasswordProvider(
            (_, cancellationToken) => provider.GetTokenAsync(host, port, username, cancellationToken),
            TokenRefreshInterval,
            TokenRetryInterval);

        return builder.Build();
    }
}
