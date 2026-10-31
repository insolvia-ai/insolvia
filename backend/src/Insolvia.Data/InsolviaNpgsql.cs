using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insolvia.Data;

/// <summary>How a host wires the data tier.</summary>
public static class InsolviaNpgsql
{
    /// <summary>
    /// Points EF Core at <paramref name="dataSource"/> with Insolvia's
    /// settings: snake_case names, migrations kept in this assembly and
    /// recorded in <c>insolvia.__ef_migrations_history</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseInsolviaNpgsql(this DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);

        return options
            .UseNpgsql(dataSource, npgsql => npgsql
                .MigrationsAssembly(typeof(InsolviaDbContext).Assembly.GetName().Name)
                .MigrationsHistoryTable(InsolviaDbContext.MigrationsHistoryTable, InsolviaDbContext.Schema))
            .UseSnakeCaseNamingConvention()

            // No query tracks, by default rather than by each store's
            // .AsNoTracking(): every write is an ExecuteUpdate/Delete or an
            // insert-then-detach, and nothing may linger in the change tracker.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
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
