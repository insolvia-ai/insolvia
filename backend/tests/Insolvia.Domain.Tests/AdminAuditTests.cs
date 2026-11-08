using Insolvia.Core.AdminAudit;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

// services/admin's core/audit.py had no unit tests of its own (its routes'
// tests asserted the rows); these pin the domain half the port carries.
public sealed class AdminAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_wire_strings_are_pythons_actions_in_order()
    {
        Assert.Equal(["firm.provision", "firm.suspend", "firm.reactivate", "invite.resend"], Wire.Names<AdminAction>());
    }

    [Fact]
    public void A_record_is_stamped_to_the_millisecond_with_a_fresh_id()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).AddTicks(12_345));

        var first = AdminEvents.Record(Ids.Firm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test", "Example & Partners", clock);
        var second = AdminEvents.Record(Ids.Firm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test", clock: clock);

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 12, 0, 0, 1, TimeSpan.Zero), first.RecordedAt);
        Assert.Equal("Example & Partners", first.Detail);
        Assert.Equal("", second.Detail);
        Assert.NotEqual(first.EventId, second.EventId);
        Assert.True(Guid.TryParse(first.EventId, out _));
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AdminEvents.Record(Ids.Firm, (AdminAction)99, "google-sub-1", "staff@example.test"));
    }

    [Fact]
    public async Task The_memory_log_keeps_every_row_in_order()
    {
        var log = new MemoryAdminAuditLog();
        var provision = AdminEvents.Record(Ids.Firm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test");
        var suspend = AdminEvents.Record(Ids.Firm, AdminAction.FirmSuspend, "google-sub-1", "staff@example.test");

        await log.RecordAsync(provision, Ct);
        await log.RecordAsync(suspend, Ct);

        Assert.Equal([provision, suspend], log.Events);
    }
}
