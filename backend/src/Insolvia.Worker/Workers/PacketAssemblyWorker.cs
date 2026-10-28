using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;
using Insolvia.Forms;

namespace Insolvia.Worker.Workers;

/// <summary>What the packet worker composes — the hosts build it from Postgres/S3/KMS, tests from the memory twins.</summary>
public sealed record PacketAssemblyDeps(
    ICaseStore Cases,
    IDebtorStore Debtors,
    ICaseEntityStore Entities,
    IPacketStore Packets,
    IDocumentBlobStore Blobs,
    IAccessLog AccessLog,
    ITaxIdStore TaxIds,
    ITaxIdCipher TaxIdCipher)
{
    /// <summary>The clock: "today" for effective dating, the stamps' instant, and the record times.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The log.</summary>
    public JobLog Log { get; init; } = JobLog.Console;
}

/// <summary>
/// Packet assembly's worker half (core/packet_assembly.py's
/// <c>run_packet_assembly</c>; issue #96). Two SUCCEEDED shapes:
/// <c>{"outcome": "blocked", "problems": [...]}</c> (the gate refused — the
/// list is the deliverable) and <c>{"outcome": "assembled", "packet": {...}}</c>
/// (stored, and the case pinned). A <see cref="JobException"/> is reserved for
/// what a fix-and-retry changes: the case vanished, or changed under the run.
/// </summary>
public static class PacketAssemblyWorker
{
    /// <summary>The registry entry.</summary>
    public static JobWorker For(PacketAssemblyDeps deps) => (job, cancellationToken) => RunAsync(job, deps, cancellationToken);

    /// <summary>Run one job.</summary>
    public static async Task<JsonObject> RunAsync(Job job, PacketAssemblyDeps deps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deps);
        var found = await deps.Cases.ReadForWorkerAsync(job.CaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new JobException("The case this job was accepted against no longer exists.", "case_not_found");

        // The whole file is a case-data read, against the preparer whose accept caused it.
        await deps.AccessLog.RecordAsync(AccessEvents.Record(job.CreatedBy, AccessAction.PacketAssemble, caseId: found.Id, clock: deps.Clock), cancellationToken).ConfigureAwait(false);
        var data = await CaseDataReads.ReadAsync(found, deps.Debtors, deps.Entities, cancellationToken).ConfigureAwait(false);
        var now = deps.Clock.GetUtcNow();
        var options = job.Options is null ? FormOverlay.DefaultOutputOptions : FormOverlay.ParseOutputOptions(job.Options, allowForms: true);
        if (PacketAssembly.PrintsB121(options))
        {
            // The full tax identifier, for B121 alone — one audited read per debtor.
            data = await CaseDataReads.DiscloseTaxIdsAsync(data, job.CreatedBy, "b121", deps.TaxIds, deps.TaxIdCipher, deps.AccessLog, cancellationToken).ConfigureAwait(false);
        }

        var (assembled, problems) = PacketAssembly.Assemble(data, DateOnly.FromDateTime(now.UtcDateTime), now, options);
        if (assembled is null)
        {
            // GLBA: the count alone — a problem message names case facts.
            deps.Log.Info("packet assembly blocked", ("case_id", found.Id), ("job_id", job.Id), ("problems", problems.Count));
            return Blocked(problems);
        }

        var content = PacketAssembly.PacketZip(assembled.Parts);
        var packet = Packets.NewPacket(
            found.Id,
            found.Chapter,
            job.Id,
            content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            assembled.FormRevisions,
            assembled.ConstantsSetId,
            assembled.CreditorCount,
            job.CreatedBy,
            options,
            PdfMeasure.MeasureParts(assembled.Parts),
            deps.Clock);

        // Bytes first, record second: an object with no record is invisible;
        // a record with no object would be a download that 404s.
        await deps.Blobs.PutBytesAsync(packet.StorageRef, content, Packets.PacketContentType, cancellationToken).ConfigureAwait(false);

        // An amendment never re-pins the case: its pins describe the ORIGINAL
        // filing; this run's resolution is on the packet alone.
        var pinned = options.AmendedOnly ? found : CaseRules.Pin(found, assembled.FormRevisions, assembled.ConstantsSetId, deps.Clock);
        var stored = await deps.Packets.CreateAsync(Packets.ToItem(packet), pinned, found.UpdatedAt, options.AmendedOnly, cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            throw new JobException("The case changed while its packet was being assembled — run assembly again.", "case_changed");
        }

        deps.Log.Info("packet assembled", ("case_id", found.Id), ("job_id", job.Id), ("packet_id", packet.Id));
        return new JsonObject { ["outcome"] = "assembled", ["packet"] = Packets.PacketJson(packet) };
    }

    /// <summary>The gate's refusal as a job result — the same shape the review's blocked answer uses.</summary>
    public static JsonObject Blocked(IEnumerable<PacketProblem> problems) => new()
    {
        ["outcome"] = "blocked",
        ["problems"] = new JsonArray([.. problems.Select(problem => (JsonNode)PacketAssembly.ProblemJson(problem))]),
    };
}
