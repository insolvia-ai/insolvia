using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Jobs;
using Insolvia.Core.Packets;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What the job and packet stores must do (test_jobs.py's store half and
/// test_packet_assembly.py's <c>the_memory_packet_store_refuses_a_moved_or_filed_case</c>):
/// once-only create, the case scope, list order, the status compare-and-swap
/// — and its race, two workers claiming one job, exactly one wins — and the
/// packet's one transaction with the case's pins.
/// </summary>
public abstract class PipelineStoreConformance : CaseConformance
{
    // ── Jobs ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_job_round_trips_every_member()
    {
        var owner = await CreateCaseAsync();
        var job = Job(owner.Id) with
        {
            Kind = JobRules.PacketAssemblyKind,
            DocumentId = "doc-1",
            Options = Make.Json("""{"draftWatermark": true, "forms": ["b101"]}"""),
            Attempts = 2,
            Status = JobStatus.Failed,
            Failure = new JobFailure("internal", "The job failed unexpectedly and will be retried."),
            Result = Make.Json("""{"outcome": "blocked", "problems": [{"source": "case", "message": "x"}]}"""),
        };
        await Connect().Jobs.CreateAsync(job, Ct);
        AssertSameJob(job, await Connect().Jobs.GetAsync(owner.Id, job.Id, Ct));
    }

    [Fact]
    public async Task A_job_is_created_once_and_scoped_to_its_case()
    {
        var owner = await CreateCaseAsync();
        var other = await CreateCaseAsync();
        var job = Job(owner.Id);
        await Connect().Jobs.CreateAsync(job, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Jobs.CreateAsync(job, Ct));
        Assert.Null(await Connect().Jobs.GetAsync(other.Id, job.Id, Ct));
        Assert.Empty(await Connect().Jobs.ListForCaseAsync(other.Id, Ct));
    }

    [Fact]
    public async Task Jobs_list_in_creation_order_with_an_id_tiebreak()
    {
        var owner = await CreateCaseAsync();
        var later = Job(owner.Id, "00000000-0000-4000-8000-0000000000e1", Opened.AddMinutes(1));
        var tieB = Job(owner.Id, "00000000-0000-4000-8000-0000000000eb", Opened);
        var tieA = Job(owner.Id, "00000000-0000-4000-8000-0000000000ea", Opened);
        foreach (var job in new[] { later, tieB, tieA })
        {
            await Connect().Jobs.CreateAsync(job, Ct);
        }

        Assert.Equal([tieA.Id, tieB.Id, later.Id], (await Connect().Jobs.ListForCaseAsync(owner.Id, Ct)).Select(job => job.Id));
    }

    [Fact]
    public async Task An_update_lands_only_over_the_status_it_read()
    {
        var owner = await CreateCaseAsync();
        var job = Job(owner.Id);
        await Connect().Jobs.CreateAsync(job, Ct);

        var running = JobRules.StartAttempt(job);
        Assert.Null(await Connect().Jobs.UpdateAsync(running, JobStatus.Running, Ct));
        Assert.Equal(JobStatus.Queued, (await Connect().Jobs.GetAsync(owner.Id, job.Id, Ct))!.Status);

        Assert.NotNull(await Connect().Jobs.UpdateAsync(running, JobStatus.Queued, Ct));
        var done = JobRules.Complete(running, Make.Json("""{"echo": "ok"}"""));
        Assert.NotNull(await Connect().Jobs.UpdateAsync(done, JobStatus.Running, Ct));
        AssertSameJob(done, await Connect().Jobs.GetAsync(owner.Id, job.Id, Ct));

        // A row that is gone answers the lost race, never a resurrection.
        Assert.Null(await Connect().Jobs.UpdateAsync(Job(owner.Id), JobStatus.Queued, Ct));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Failed)]
    public async Task Two_workers_claiming_one_job_race_for_a_single_winner(JobStatus from)
    {
        // The SQS redelivery race: every delivery read the same row and asks
        // to move it from what it read. Exactly one claim may land.
        var owner = await CreateCaseAsync();
        var job = Job(owner.Id) with { Status = from };
        await Connect().Jobs.CreateAsync(job, Ct);
        var read = (await Connect().Jobs.GetAsync(owner.Id, job.Id, Ct))!;
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Connect().Jobs.UpdateAsync(JobRules.StartAttempt(read), read.Status, Ct), Ct)));
        Assert.Single(claims, claim => claim is not null);
        var stored = (await Connect().Jobs.GetAsync(owner.Id, job.Id, Ct))!;
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(JobStatus.Running, stored.Status);
    }

    // ── Packets ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_packet_lands_with_the_cases_pins_in_one_write()
    {
        var owner = await CreateCaseAsync();
        var packet = Packet(owner.Id);
        var pinned = Pin(owner);

        Assert.True(await Connect().Packets.CreateAsync(packet, pinned, owner.UpdatedAt, cancellationToken: Ct));

        AssertSamePacket(packet, await Connect().Packets.GetAsync(owner.Id, packet.Id, Ct));
        var stored = (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!;
        Assert.Equal(pinned.FormRevisions, stored.FormRevisions);
        Assert.Equal("dollar_amounts/2025-04-01", stored.ConstantsSetId);
        Assert.Equal(pinned.UpdatedAt, stored.UpdatedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Packets.CreateAsync(packet, Pin(stored), stored.UpdatedAt, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_moved_case_takes_neither_the_packet_nor_the_pins()
    {
        var owner = await CreateCaseAsync();
        var packet = Packet(owner.Id);

        Assert.False(await Connect().Packets.CreateAsync(packet, Pin(owner), owner.UpdatedAt.AddSeconds(-1), cancellationToken: Ct));
        Assert.Null(await Connect().Packets.GetAsync(owner.Id, packet.Id, Ct));
        Assert.Null((await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!.ConstantsSetId);
    }

    [Fact]
    public async Task A_filed_case_refuses_unless_the_packet_is_an_amendment()
    {
        var owner = await CreateCaseAsync();
        await FileAsync(owner);
        var stored = (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!;
        Assert.Equal(CaseStatus.Filed, stored.Status);

        Assert.False(await Connect().Packets.CreateAsync(Packet(owner.Id), Pin(stored), stored.UpdatedAt, cancellationToken: Ct));
        var amendment = Packet(owner.Id);
        Assert.True(await Connect().Packets.CreateAsync(amendment, stored, stored.UpdatedAt, allowFiled: true, Ct));
        Assert.Equal([amendment.Id], (await Connect().Packets.ListForCaseAsync(owner.Id, Ct)).Select(p => p.Id));
        Assert.Equal(stored, await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task Packets_list_newest_first_and_stay_in_their_case()
    {
        var owner = await CreateCaseAsync();
        var other = await CreateCaseAsync();
        var older = Packet(owner.Id, Opened);
        var newer = Packet(owner.Id, Opened.AddMinutes(5));
        foreach (var packet in new[] { older, newer })
        {
            var current = (await Connect().Cases.ReadForWorkerAsync(owner.Id, Ct))!;
            Assert.True(await Connect().Packets.CreateAsync(packet, Pin(current), current.UpdatedAt, cancellationToken: Ct));
        }

        Assert.Equal([newer.Id, older.Id], (await Connect().Packets.ListForCaseAsync(owner.Id, Ct)).Select(p => p.Id));
        Assert.Empty(await Connect().Packets.ListForCaseAsync(other.Id, Ct));
        Assert.Null(await Connect().Packets.GetAsync(other.Id, older.Id, Ct));
    }

    /// <summary>Move the stored case to filed through the port the suite owns.</summary>
    private protected abstract Task FileAsync(Case filed);

    private static void AssertSameJob(Job expected, Job? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(JobRules.ToJson(expected).ToJsonString(), JobRules.ToJson(actual).ToJsonString(), JsonEquality.Instance);
        Assert.Equal(expected.CaseId, actual.CaseId);
    }

    private static void AssertSamePacket(PacketItem expected, PacketItem? actual)
    {
        Assert.NotNull(actual);
        JsonArray none = [];
        Assert.Equal(expected with { Options = null, Parts = none }, actual with { Options = null, Parts = none });
        Assert.True(JsonNode.DeepEquals(expected.Options, actual.Options));
        Assert.True(JsonNode.DeepEquals(expected.Parts, actual.Parts));
    }

    private static Job Job(string caseId, string? id = null, DateTimeOffset? at = null) =>
        new(id ?? NewId(), caseId, JobRules.EchoKind, JobStatus.Queued, Ids.Alice, at ?? Opened, at ?? Opened);

    private static Case Pin(Case current) =>
        CaseRules.Pin(current, new Dictionary<string, string>(StringComparer.Ordinal) { ["form/b101"] = "form/b101/2024-06-01" }, "dollar_amounts/2025-04-01");

    private static PacketItem Packet(string caseId, DateTimeOffset? at = null)
    {
        var id = NewId();
        return new PacketItem(
            id,
            caseId,
            NewId(),
            "chapter7-packet.zip",
            "application/zip",
            1234,
            new string('a', 64),
            $"cases/{caseId}/packets/{id}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["form/b101"] = "form/b101/2024-06-01" }.ToValueDictionary(),
            "dollar_amounts/2025-04-01",
            3,
            Ids.Alice,
            at ?? Opened,
            Make.Json("""{"draftWatermark": false, "printDate": false, "signaturePages": "all", "signElectronically": false, "amendedOnly": false}"""),
            [new JsonObject { ["name"] = "01-b101.pdf", ["byteSize"] = 1000, ["pageCount"] = 9, ["sha256"] = new string('b', 64) }]);
    }

    /// <summary>JSON text equal up to object member order (jsonb normalises it).</summary>
    private sealed class JsonEquality : IEqualityComparer<string>
    {
        public static readonly JsonEquality Instance = new();

        public bool Equals(string? x, string? y) => JsonNode.DeepEquals(JsonNode.Parse(x!), JsonNode.Parse(y!));

        public int GetHashCode(string obj) => 0;
    }
}

/// <summary>The memory twins — the oracle.</summary>
public sealed class MemoryPipelineStoreConformance : PipelineStoreConformance
{
    private readonly MemoryCaseWorld _world = new();

    private protected override CaseStores Connect() => _world.Connect();

    private protected override Task FileAsync(Case filed) => FileThroughLifecycleAsync(Connect(), filed);

    internal static async Task FileThroughLifecycleAsync(CaseStores stores, Case filed)
    {
        var current = (await stores.Cases.ReadForWorkerAsync(filed.Id, TestContext.Current.CancellationToken))!;
        var history = await stores.Cases.LifecycleAsync(filed.Id, TestContext.Current.CancellationToken);
        var write = CaseLifecycle.StatusMove(
            current,
            current with { Status = CaseStatus.Filed, FiledAt = new DateOnly(2026, 10, 2), CaseNumber = "8:26-bk-01234", UpdatedAt = current.UpdatedAt.AddMinutes(2) },
            history,
            Ids.Alice)!;
        Assert.NotNull(await stores.Cases.RecordAsync(write, TestContext.Current.CancellationToken));
    }
}

/// <summary>The Postgres job and packet stores on a real Postgres 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresPipelineStoreConformance(PostgresFixture postgres) : PipelineStoreConformance
{
    private readonly PostgresCaseWorld _world = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _world.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override CaseStores Connect() => _world.Connect();

    private protected override Task FileAsync(Case filed) => MemoryPipelineStoreConformance.FileThroughLifecycleAsync(Connect(), filed);
}
