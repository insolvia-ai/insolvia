using Insolvia.Core.AdminAudit;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.AdminAuditing;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="IAdminAuditLog"/> must do, against the memory twin
/// and the Postgres log: keep every row it is given, exactly. The port has no
/// read, so each suite reads back through its own probe.
/// </summary>
public abstract class AdminAuditLogConformance : IAsyncLifetime
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private protected abstract IAdminAuditLog Connect();

    /// <summary>Every row recorded, in event-id order.</summary>
    private protected abstract Task<IReadOnlyList<AdminEvent>> RecordedAsync();

    [Fact]
    public async Task The_log_keeps_every_action_and_its_detail()
    {
        AdminEvent[] events =
        [
            AdminEvents.Record(Ids.Firm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test", "Example & Partners"),
            AdminEvents.Record(Ids.Firm, AdminAction.InviteResend, "google-sub-1", "staff@example.test", "alice@example.test"),
            AdminEvents.Record(Ids.Firm, AdminAction.FirmSuspend, "google-sub-2", "other-staff@example.test"),
            AdminEvents.Record(Ids.OtherFirm, AdminAction.FirmReactivate, "google-sub-2", "other-staff@example.test"),
        ];
        foreach (var adminEvent in events)
        {
            await Connect().RecordAsync(adminEvent, Ct);
        }

        Assert.Equal(events.OrderBy(e => e.EventId, StringComparer.Ordinal), await RecordedAsync());
    }

    [Fact]
    public async Task Two_identical_acts_in_one_millisecond_are_both_kept()
    {
        var first = AdminEvents.Record(Ids.Firm, AdminAction.FirmSuspend, "google-sub-1", "staff@example.test");
        var second = first with { EventId = Guid.NewGuid().ToString() };
        await Connect().RecordAsync(first, Ct);
        await Connect().RecordAsync(second, Ct);
        Assert.Equal(2, (await RecordedAsync()).Count);
    }
}

/// <summary>The oracle: <see cref="MemoryAdminAuditLog"/>.</summary>
public sealed class MemoryAdminAuditLogConformance : AdminAuditLogConformance
{
    private readonly MemoryAdminAuditLog _log = new();

    private protected override IAdminAuditLog Connect() => _log;

    private protected override Task<IReadOnlyList<AdminEvent>> RecordedAsync() =>
        Task.FromResult<IReadOnlyList<AdminEvent>>([.. _log.Events.OrderBy(e => e.EventId, StringComparer.Ordinal)]);
}

/// <summary><see cref="PostgresAdminAuditLog"/> on a real Postgres 17 — plus what only the table enforces.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresAdminAuditLogConformance(PostgresFixture postgres) : AdminAuditLogConformance
{
    private readonly PostgresHarness _harness = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _harness.InitializeAsync();
        await _harness.SeedFirmsAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_replayed_event_is_refused_rather_than_overwriting_the_row()
    {
        var recorded = AdminEvents.Record(Ids.Firm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test", "Example & Partners");
        await Connect().RecordAsync(recorded, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().RecordAsync(recorded with { Detail = "rewritten" }, Ct));
        Assert.Equal([recorded], await RecordedAsync());
    }

    [Fact]
    public async Task An_event_names_a_firm_that_exists()
    {
        var orphan = AdminEvents.Record("00000000-0000-4000-8000-00000000f18f", AdminAction.FirmSuspend, "google-sub-1", "staff@example.test");
        await Assert.ThrowsAsync<DbUpdateException>(() => Connect().RecordAsync(orphan, Ct));
    }

    private protected override IAdminAuditLog Connect() => new PostgresAdminAuditLog(_harness.NewContext());

    private protected override async Task<IReadOnlyList<AdminEvent>> RecordedAsync()
    {
        var rows = await _harness.NewContext().Set<AdminAuditRow>().AsNoTracking().ToListAsync(Ct);
        return [.. rows.Select(PostgresAdminAuditLog.ToDomain).OrderBy(e => e.EventId, StringComparer.Ordinal)];
    }
}
