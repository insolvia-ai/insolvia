using Insolvia.Core.AccessLog;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.AccessLogging;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What every <see cref="IAccessLog"/> must do, against the memory twin and
/// the Postgres log: keep every row it is given, exactly. The port has no
/// read, so each suite reads back through its own probe.
/// </summary>
public abstract class AccessLogConformance : IAsyncLifetime
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

    private protected abstract IAccessLog Connect();

    /// <summary>Every row recorded, in event-id order.</summary>
    private protected abstract Task<IReadOnlyList<AccessEvent>> RecordedAsync();

    [Fact]
    public async Task The_log_keeps_what_it_recorded()
    {
        var accessEvent = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        await Connect().RecordAsync(accessEvent, Ct);
        Assert.Equal([accessEvent], await RecordedAsync());
    }

    [Fact]
    public async Task Every_kind_of_subject_and_every_optional_fact_round_trips()
    {
        AccessEvent[] events =
        [
            AccessEvents.Record(Ids.Alice, AccessAction.TaxIdRead, caseId: Ids.Case, filingRole: "debtor_1", purpose: "b101"),
            AccessEvents.Record(Ids.Alice, AccessAction.ClientInvite, caseId: Ids.Case, roles: ["debtor_1", "debtor_2"]),
            AccessEvents.Record(Ids.Bob, AccessAction.ClientRead, clientId: "client-1", outcome: AccessOutcome.Denied),
            AccessEvents.Record(Ids.Alice, AccessAction.CredentialOpen, credentialId: "credential-1", purpose: "file", filingId: "filing-1"),
            AccessEvents.Record(Ids.Alice, AccessAction.AuthorizationSign, authorizationId: "authorization-1"),
            AccessEvents.Record(Ids.Pat, AccessAction.PortalAnswer, caseId: Ids.OtherCase),
        ];
        foreach (var accessEvent in events)
        {
            await Connect().RecordAsync(accessEvent, Ct);
        }

        Assert.Equal(events.OrderBy(e => e.EventId, StringComparer.Ordinal), await RecordedAsync());
    }

    [Fact]
    public async Task Two_rows_about_the_same_subject_are_both_kept()
    {
        var first = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        var second = AccessEvents.Record(Ids.Alice, AccessAction.CaseRead, caseId: Ids.Case);
        await Connect().RecordAsync(first, Ct);
        await Connect().RecordAsync(second, Ct);
        Assert.Equal(2, (await RecordedAsync()).Count);
    }
}

/// <summary>The oracle: <see cref="MemoryAccessLog"/>.</summary>
public sealed class MemoryAccessLogConformance : AccessLogConformance
{
    private readonly MemoryAccessLog _log = new();

    private protected override IAccessLog Connect() => _log;

    private protected override Task<IReadOnlyList<AccessEvent>> RecordedAsync() =>
        Task.FromResult<IReadOnlyList<AccessEvent>>([.. _log.Events.OrderBy(e => e.EventId, StringComparer.Ordinal)]);
}

/// <summary><see cref="PostgresAccessLog"/> on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresAccessLogConformance(PostgresFixture postgres) : AccessLogConformance
{
    private readonly PostgresHarness _harness = new(postgres);

    public override async ValueTask InitializeAsync() => await _harness.InitializeAsync();

    public override async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override IAccessLog Connect() => new PostgresAccessLog(_harness.NewContext());

    private protected override async Task<IReadOnlyList<AccessEvent>> RecordedAsync()
    {
        var rows = await _harness.NewContext().Set<AccessLogRow>().AsNoTracking().ToListAsync(Ct);
        return [.. rows.Select(PostgresAccessLog.ToDomain).OrderBy(e => e.EventId, StringComparer.Ordinal)];
    }
}
