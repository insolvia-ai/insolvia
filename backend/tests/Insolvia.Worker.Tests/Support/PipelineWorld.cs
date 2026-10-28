using Insolvia.Aws.Sealing;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Memory;
using Insolvia.Core.TaxIds;
using Insolvia.Forms;
using Insolvia.Forms.Tests.Support;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Tests.Support;

/// <summary>A clock a test sets.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// test_packet_assembly.py's <c>build_deps</c>: a <see cref="CaseData"/> laid
/// into the memory twins exactly as the stores would hold it, its tax ids
/// SEALED the way the debtor route seals them — so the worker's logged read
/// (not the fixture's <c>TaxIds</c>) is what puts the number on B121.
/// </summary>
internal sealed class PipelineWorld
{
    public static readonly DateOnly Today = new(2026, 10, 1);

    private PipelineWorld(CaseData data)
    {
        Data = data;
        Debtors = new MemoryDebtorStore();
        Cases = new MemoryCaseStore(Debtors);
        Packets = new MemoryPacketStore(Cases);
    }

    public CaseData Data { get; }

    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public MemoryDebtorStore Debtors { get; }

    public MemoryCaseStore Cases { get; }

    public MemoryCaseEntityStore Entities { get; } = new();

    public MemoryPacketStore Packets { get; }

    public MemoryDocumentBlobStore Blobs { get; } = new();

    public MemoryAccessLog AccessLog { get; } = new();

    public MemoryTaxIdStore TaxIds { get; } = new();

    public LocalTaxIdCipher Cipher { get; } = new();

    public MemoryDocumentStore Documents { get; } = new();

    public MemoryCandidateStore Candidates { get; } = new();

    public RecordingJobLog Log { get; } = new();

    public string CaseId => Data.Case.Id;

    public PacketAssemblyDeps PacketDeps => new(Cases, Debtors, Entities, Packets, Blobs, AccessLog, TaxIds, Cipher) { Clock = Clock, Log = Log };

    /// <summary>The CASE id every reference world uses — a uuid, as the object keys require.</summary>
    public const string ReferenceCaseId = "11111111-2222-4333-8444-000000000001";

    /// <summary>A reference case under a server-shaped (uuid) case id, as the Python tests' CASE_ID.</summary>
    public static CaseData Reference(string name)
    {
        var file = CaseFiles.Named(name);
        var moved = file with { Case = file.Case with { Id = ReferenceCaseId }, Debtors = [.. file.Debtors.Select(debtor => debtor with { CaseId = ReferenceCaseId })] };
        return CaseDatas.Of(moved);
    }

    public static async Task<PipelineWorld> SeedAsync(CaseData data)
    {
        var world = new PipelineWorld(data);
        var ct = TestContext.Current.CancellationToken;
        await world.Cases.CreateAsync(data.Case, CaseRules.Assign(data.Case, "subject-1", "subject-1"), data.Debtors, ct);
        foreach (var (_, entities) in data.Collections())
        {
            foreach (var entity in entities)
            {
                await world.Entities.CreateAsync(entity, ct);
            }
        }

        foreach (var debtor in data.Debtors)
        {
            if (debtor.TaxId is { } reference && data.TaxIds.TryGetValue(debtor.FilingRole.ToWire(), out var digits))
            {
                await TaxIdRules.StoreAsync(new TaxIdInput(reference.Kind, digits, null), reference, data.Case.FirmId, data.Case.Id, world.Cipher, world.TaxIds, cancellationToken: ct);
            }
        }

        return world;
    }
}
