using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insolvia.Data;

/// <summary>How a host wires the data tier.</summary>
public static class InsolviaNpgsql
{
    /// <summary>
    /// How many times EF Core replays a unit of work after a transient
    /// failure (a dropped or timed-out connection, <c>57P03</c> "cannot
    /// connect now", a serialization failure or deadlock — Npgsql's
    /// <c>IsTransient</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized for Aurora Serverless v2 at minimum capacity 0 (ADR 0028 § Data):
    /// after 15 idle minutes it pauses, and the first connection waits ~15 s
    /// (seen up to ~40 s) while it resumes. Two mechanisms cover that:
    /// </para>
    /// <list type="bullet">
    /// <item>A connection Aurora holds while it resumes waits inside one
    /// attempt — the IAM data source's 30 s connect timeout
    /// (<c>InsolviaDataSource</c>).</item>
    /// <item>A connection it refuses or drops fails fast and is retried here.
    /// EF's back-off is <c>(2^n − 1) × 1 s</c> with up to 10 % jitter, capped at
    /// <see cref="MaxRetryDelay"/>: pauses of 0, 1, 3, 5, 5, 5 s — the seventh
    /// and last attempt starts 19 s (≤ 19.4 s with jitter) after the first
    /// failure, plus the time the failed attempts took.</item>
    /// </list>
    /// <para>
    /// The bound is the API, admin and MCP Lambdas' 30 s timeout and API
    /// Gateway's 30 s integration timeout (<c>infra/modules/api_service</c>,
    /// <c>admin_service</c>, <c>mcp_service</c>): ~19 s of waiting leaves room
    /// for the request's own work. A resume slower than that fails one request
    /// whichever way it is cut — it cannot fit in a 30 s request — and the
    /// next one finds the cluster up. The worker (≥ 60 s) and the filing
    /// worker (≥ 60 s) use the same bounds; their queues redeliver.
    /// </para>
    /// </remarks>
    public const int MaxRetryCount = 6;

    /// <summary>The longest pause between two attempts; see <see cref="MaxRetryCount"/>.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Points EF Core at <paramref name="dataSource"/> with Insolvia's
    /// settings: snake_case names, migrations kept in this assembly and
    /// recorded in <c>insolvia.__ef_migrations_history</c>, and the retrying
    /// execution strategy (<see cref="MaxRetryCount"/>).
    /// </summary>
    /// <remarks>
    /// The strategy replays queries (up to opening their reader),
    /// <c>SaveChanges</c>, <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, and every
    /// <c>InTransactionAsync</c> unit whole. It does not replay
    /// <c>ExecuteSql…</c> outside a transaction — that goes through
    /// <c>RetrySafety.ExecuteIdempotentSqlAsync</c> — nor anything on a raw
    /// <see cref="NpgsqlConnection"/>. Every write must be safe to replay,
    /// including after a commit whose acknowledgement was lost:
    /// <c>Concurrency/RetrySafety</c> and backend/CLAUDE.md § the data tier.
    /// A transaction begun outside the strategy is refused by EF; use
    /// <c>InTransactionAsync</c>.
    /// </remarks>
    public static DbContextOptionsBuilder UseInsolviaNpgsql(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);

        return options
            .UseNpgsql(dataSource, npgsql => npgsql
                .MigrationsAssembly(typeof(InsolviaDbContext).Assembly.GetName().Name)
                .MigrationsHistoryTable(InsolviaDbContext.MigrationsHistoryTable, InsolviaDbContext.Schema)
                .EnableRetryOnFailure(MaxRetryCount, MaxRetryDelay, errorCodesToAdd: null))
            .UseSnakeCaseNamingConvention();
    }

    /// <summary>
    /// Registers the process's <see cref="NpgsqlDataSource"/> (a singleton:
    /// it owns the pool and the token refresh) and a scoped
    /// <see cref="InsolviaDbContext"/> over it.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="settings">Usually <see cref="DatabaseSettings.FromEnvironment"/>.</param>
    /// <param name="tokens">Overrides the RDS token provider (tests).</param>
    public static IServiceCollection AddInsolviaData(
        this IServiceCollection services,
        DatabaseSettings settings,
        IDatabaseTokenProvider? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddSingleton(_ => InsolviaDataSource.Create(settings, tokens));
        services.AddDbContext<InsolviaDbContext>((provider, options) =>
            options.UseInsolviaNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
