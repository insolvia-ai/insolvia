using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Jobs;
using Insolvia.Core.Packets;
using Insolvia.Core.Ports;
using Insolvia.Core.Provenance;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Regulatory;
using Insolvia.Forms;
using Insolvia.Forms.Tests.Support;
using Insolvia.Worker.Tests.Support;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Tests;

/// <summary>test_packet_assembly.py's worker half, on the memory twins: the store calls, the logged tax-id read and the job envelope.</summary>
public sealed class PacketAssemblyWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Job Accept(string caseId, JsonObject? options = null) =>
        JobRules.NewJob(JobRules.PacketAssemblyKind, caseId, "subject-1", options: options);

    private static Dictionary<string, string> PinsAsOf(DateOnly day) =>
        PacketAssembly.AllFormSeries.ToDictionary(series => series, series => FormTemplates.Resolve(series, day).Pin, StringComparer.Ordinal);

    [Fact]
    public async Task The_worker_stores_the_packet_and_pins_the_case_together()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));

        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);

        Assert.Equal("assembled", result["outcome"]!.GetValue<string>());
        var stored = (await world.Packets.GetAsync(world.CaseId, result["packet"]!["id"]!.GetValue<string>(), Ct))!;
        var content = (await world.Blobs.GetBytesAsync(stored.StorageRef, Ct))!;
        Assert.Equal(stored.ByteSize, content.Length);
        Assert.Equal(stored.Sha256, Convert.ToHexStringLower(SHA256.HashData(content)));
        Assert.Equal("application/zip", world.Blobs.ContentTypes[stored.StorageRef]);

        // The pins landed on the case in the same write, and match the packet's own copy.
        var pinned = world.Cases.Cases[world.CaseId];
        Assert.Equal(PinsAsOf(PipelineWorld.Today), new Dictionary<string, string>(pinned.FormRevisions!, StringComparer.Ordinal));
        Assert.Equal(pinned.FormRevisions, stored.FormRevisions);
        Assert.Equal(DollarAmounts.Resolve(PipelineWorld.Today).ReleaseId, pinned.ConstantsSetId);
        Assert.Equal(pinned.ConstantsSetId, stored.ConstantsSetId);

        // A readable archive: the full set (the shared-household reference files no J-2) plus the matrix.
        using var zip = new ZipArchive(new MemoryStream(content));
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Equal(CreditorMatrices.MatrixFileName, names[^1]);
        Assert.Equal(PacketAssembly.PacketFormSeries.Count - 1 + 1, names.Count);
        Assert.Contains(world.AccessLog.Events, e => e.Action == AccessAction.PacketAssemble && e.Principal == "subject-1");
    }

    [Theory]
    [InlineData("reference-ch7", "chapter7-packet.zip")]
    [InlineData("reference-ch13", "chapter13-packet.zip")]
    public async Task The_worker_names_the_packet_by_the_cases_chapter(string name, string fileName)
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference(name));
        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);
        Assert.Equal(fileName, result["packet"]!["fileName"]!.GetValue<string>());
        Assert.Equal(fileName, (await world.Packets.GetAsync(world.CaseId, result["packet"]!["id"]!.GetValue<string>(), Ct))!.FileName);
    }

    [Fact]
    public async Task The_worker_performs_the_logged_read_for_b121_only()
    {
        var data = PipelineWorld.Reference("reference-ch7");
        var world = await PipelineWorld.SeedAsync(data);
        await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);

        var reads = world.AccessLog.Events.Where(e => e.Action == AccessAction.TaxIdRead).Select(e => (e.Principal, e.FilingRole, e.Purpose));
        Assert.Equal([("subject-1", "debtor_1", "b121"), ("subject-1", "debtor_2", "b121")], reads);

        // The printed B121 is byte-identical to one rendered with the
        // fixture's digits: the disclosed number is the sealed one.
        var stored = (await world.Packets.ListForCaseAsync(world.CaseId, Ct))[0];
        using var zip = new ZipArchive(new MemoryStream((await world.Blobs.GetBytesAsync(stored.StorageRef, Ct))!));
        using var printed = new MemoryStream();
        await using (var entry = zip.GetEntry("02-b121.pdf")!.Open())
        {
            await entry.CopyToAsync(printed, Ct);
        }

        var now = world.Clock.GetUtcNow();
        var (expected, _) = PacketAssembly.Assemble(data, PipelineWorld.Today, now);
        Assert.Equal(expected!.Parts.Single(part => part.Name == "02-b121.pdf").Content, printed.ToArray());
    }

    [Fact]
    public async Task A_subset_without_b121_never_opens_an_envelope()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId, JsonNode.Parse("""{"forms": ["b101"]}""")!.AsObject()), world.PacketDeps, Ct);
        Assert.Equal("assembled", result["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(world.AccessLog.Events, e => e.Action == AccessAction.TaxIdRead);
    }

    [Fact]
    public async Task Reassembly_repins_and_keeps_the_old_packet()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var first = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);
        world.Clock.Now = world.Clock.Now.AddMinutes(1);
        var second = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);

        Assert.Equal(2, (await world.Packets.ListForCaseAsync(world.CaseId, Ct)).Count);
        Assert.Equal(first["packet"]!["sha256"]!.GetValue<string>(), second["packet"]!["sha256"]!.GetValue<string>());
        Assert.Equal(PinsAsOf(PipelineWorld.Today), new Dictionary<string, string>(world.Cases.Cases[world.CaseId].FormRevisions!, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_blocked_case_stores_nothing_and_pins_nothing()
    {
        var data = PipelineWorld.Reference("reference-ch7");
        var broken = new CaseEntity<ClaimBody>(ClaimRules.Kind, "claim-broken", data.Case.Id, CaseDatas.EntityTime.AddDays(1), CaseDatas.EntityTime.AddDays(1), ClaimRules.Kind.ParseBody(JsonNode.Parse("""{"creditor_id": "no-such"}""")!.AsObject()), ValueCollections.EmptyDictionary<string, ProvenanceEntry>());
        var world = await PipelineWorld.SeedAsync(data with { Claims = [.. data.Claims, broken] });

        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);

        Assert.Equal("blocked", result["outcome"]!.GetValue<string>());
        Assert.Contains(result["problems"]!.AsArray(), problem => problem!["itemId"]?.GetValue<string>() == "claim-broken");
        Assert.Empty(await world.Packets.ListForCaseAsync(world.CaseId, Ct));
        Assert.Empty(world.Blobs.ContentTypes);
        Assert.Null(world.Cases.Cases[world.CaseId].FormRevisions);
        Assert.Contains(world.Log.Records, record => record.Message == "packet assembly blocked" && record.Fields.Keys.Order().SequenceEqual(["case_id", "job_id", "problems"]));
    }

    [Fact]
    public async Task A_vanished_case_fails_deterministically()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var error = await Assert.ThrowsAsync<JobException>(() => PacketAssemblyWorker.RunAsync(Accept("11111111-2222-4333-8444-000000000099"), world.PacketDeps, Ct));
        Assert.Equal("case_not_found", error.Category);
    }

    [Fact]
    public async Task A_case_that_changed_mid_assembly_fails_the_job()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var error = await Assert.ThrowsAsync<JobException>(() => PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps with { Packets = new RefusingPacketStore() }, Ct));
        Assert.Equal("case_changed", error.Category);
    }

    [Fact]
    public async Task The_memory_packet_store_refuses_a_moved_case()
    {
        var world = await PipelineWorld.SeedAsync(PipelineWorld.Reference("reference-ch7"));
        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId), world.PacketDeps, Ct);
        var packet = (await world.Packets.GetAsync(world.CaseId, result["packet"]!["id"]!.GetValue<string>(), Ct))!;
        var fresh = packet with { Id = "11111111-2222-4333-8444-00000000feed" };
        Assert.False(await world.Packets.CreateAsync(fresh, world.Cases.Cases[world.CaseId], new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), cancellationToken: Ct));
    }

    [Fact]
    public async Task An_amendment_never_opens_a_tax_id_envelope_and_never_repins()
    {
        var data = CaseDatas.Amended(PipelineWorld.Reference("reference-ch7"));
        var world = await PipelineWorld.SeedAsync(data);
        var before = world.Cases.Cases[world.CaseId];

        var result = await PacketAssemblyWorker.RunAsync(Accept(world.CaseId, JsonNode.Parse("""{"amendedOnly": true}""")!.AsObject()), world.PacketDeps, Ct);

        Assert.Equal("assembled", result["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(world.AccessLog.Events, e => e.Action == AccessAction.TaxIdRead);
        var after = world.Cases.Cases[world.CaseId];
        Assert.Equal(CaseStatus.Filed, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.FormRevisions, after.FormRevisions);
    }

    [Fact]
    public void Packet_assembly_is_an_acceptable_job_kind() => Assert.Contains(PacketAssembly.PacketAssemblyKind, JobRules.Kinds);

    private sealed class RefusingPacketStore : IPacketStore
    {
        public Task<bool> CreateAsync(PacketItem packet, Case pinnedCase, DateTimeOffset expectedUpdatedAt, bool allowFiled = false, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<PacketItem?> GetAsync(string caseId, string packetId, CancellationToken cancellationToken = default) => Task.FromResult<PacketItem?>(null);

        public Task<IReadOnlyList<PacketItem>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PacketItem>>([]);
    }
}
