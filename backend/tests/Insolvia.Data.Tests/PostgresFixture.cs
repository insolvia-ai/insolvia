using Npgsql;
using Testcontainers.PostgreSql;

namespace Insolvia.Data.Tests;

/// <summary>
/// One Postgres 17 container for the whole test run (ADR 0028: "Testcontainers
/// Postgres, one per test run"), started once and shared through the
/// <see cref="PostgresTests"/>. A test asks for a fresh database rather
/// than sharing tables, so no test sees another's rows. Needs Docker: a
/// laptop's Docker Desktop, or the Docker GitHub's ubuntu runners ship with.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    /// <summary>The superuser's connection string for the container's default database.</summary>
    public string SuperuserConnectionString => _container.GetConnectionString();

    /// <summary>The container's host and mapped port, for tests that build their own connection.</summary>
    public (string Host, int Port) Address => (_container.Hostname, _container.GetMappedPublicPort(5432));

    /// <summary>Creates an empty database and returns a superuser connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnection(SuperuserConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA2100 // The name is a GUID minted above.
            var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
#pragma warning restore CA2100
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        return new NpgsqlConnectionStringBuilder(SuperuserConnectionString) { Database = name }.ConnectionString;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);
}

/// <summary>The tests that share <see cref="PostgresFixture"/>'s container.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection's name, for <c>[Collection(PostgresTests.Name)]</c>.</summary>
    public const string Name = "postgres";
}
