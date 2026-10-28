using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Clients;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>Every identifier is obviously fake: this repo is public.</summary>
internal static class Ids
{
    public const string Firm = "00000000-0000-4000-8000-00000000f18a";
    public const string OtherFirm = "00000000-0000-4000-8000-00000000f18b";
    public const string Alice = "00000000-0000-4000-8000-00000000a11c";
    public const string Bob = "00000000-0000-4000-8000-00000000b0b0";
    public const string Case = "00000000-0000-4000-8000-0000000ca5e1";
    public const string OtherCase = "00000000-0000-4000-8000-0000000ca5e2";
    public const string Pat = "00000000-0000-4000-8000-0000000000a1";
    public const string Sam = "00000000-0000-4000-8000-0000000000a2";
    public const string Kim = "00000000-0000-4000-8000-0000000000a3";
}

/// <summary>A clock that stands still unless told to move, on a whole microsecond (timestamptz's precision).</summary>
internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The records the suites store — the Core tests' builders, restated here.</summary>
internal static class Make
{
    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    public static Firm Firm(string firmId = Ids.Firm, string name = "Example & Partners") =>
        new(firmId, name, FirmStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public static FirmUser User(
        string subject = Ids.Alice,
        string firmId = Ids.Firm,
        string firstName = "Alice",
        string lastName = "Attorney",
        FirmRole role = FirmRole.Attorney) =>
        new(
            firmId,
            subject,
            "alice@example.test",
            firstName,
            lastName,
            role,
            IsAdmin: false,
            AccessAllCases: false,
            Permissions.DefaultFor(role),
            FirmUserStatus.Active,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    public static LibraryCreditor Creditor(string firmId = Ids.Firm, string name = "Acme Collections") =>
        LibraryCreditorRules.Create(LibraryCreditorRules.Parse(Json($$"""{"name": "{{name}}", "preferred": true}""")), firmId);

    public static FirmClient Client(string firmId = Ids.Firm, string surname = "Example", string? retained = null)
    {
        var retainedField = retained is null ? "" : $$""", "first_retained_at": "{{retained}}" """;
        return FirmClientRules.Create(
            FirmClientRules.Parse(Json($$"""{"name": {"given": "Jordan", "surname": "{{surname}}"}{{retainedField}}}""")),
            firmId,
            Ids.Alice);
    }

    public static ClientBinding Binding(
        string subject = Ids.Pat,
        ClientRole[]? roles = null,
        BindingStatus status = BindingStatus.Invited,
        string caseId = Ids.Case,
        string firmId = Ids.Firm,
        DateTimeOffset? createdAt = null)
    {
        var at = createdAt ?? DateTimeOffset.UnixEpoch;
        return new(
            firmId,
            caseId,
            subject,
            $"{subject[^2..]}@example.test",
            "Pat Example",
            (roles ?? [ClientRole.Debtor1]).ToValueCollection(),
            status,
            Ids.Alice,
            at,
            at);
    }
}

/// <summary>
/// One fresh, MIGRATED database per test — the real migration chain, not
/// <c>EnsureCreated</c>, so the suite also proves the migration builds the
/// schema the stores need. Each <see cref="NewContext"/> is another
/// connection to the same database: two stores over two contexts race the
/// way two requests do.
/// </summary>
internal sealed class PostgresHarness(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly List<InsolviaDbContext> _contexts = [];
    private NpgsqlDataSource? _dataSource;
    private DbContextOptions<InsolviaDbContext>? _options;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ConnectionString = await postgres.CreateDatabaseAsync();
        _dataSource = InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = ConnectionString });
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(_dataSource);
        _options = options.Options;
        await using var migrate = new InsolviaDbContext(_options);
        await migrate.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public InsolviaDbContext NewContext()
    {
        var context = new InsolviaDbContext(_options ?? throw new InvalidOperationException("not initialised"));
        _contexts.Add(context);
        return context;
    }

    /// <summary>The two firms every tenant row in these suites hangs off (the foreign key the memory store has no twin of).</summary>
    public async Task SeedFirmsAsync()
    {
        var firms = new PostgresFirmStore(NewContext());
        foreach (var firmId in new[] { Ids.Firm, Ids.OtherFirm })
        {
            if (await firms.GetFirmAsync(firmId, TestContext.Current.CancellationToken) is null)
            {
                await firms.CreateFirmAsync(Make.Firm(firmId, "Seeded " + firmId[^1..]), TestContext.Current.CancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
