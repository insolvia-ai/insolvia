using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Jobs;
using Insolvia.Core.Packets;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Cases;

/// <summary>
/// The Postgres <see cref="IJobStore"/> (Python's <c>DynamoDbJobStore</c>). Every
/// transition is a compare-and-swap on <c>status</c>: <c>UPDATE … WHERE status = :read</c>,
/// so of two deliveries racing one message exactly one wins the row.
/// </summary>
public sealed class PostgresJobStore(InsolviaDbContext db) : IJobStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var firmId = await CaseFirms.OfAsync(_db, job.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(ToRow(job, firmId), JobRow.PrimaryKey, () => new InvalidOperationException("job id already exists in this case"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Job?> GetAsync(string caseId, string jobId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<JobRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == jobId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<JobRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToDomain).Order(JobRules.ListOrder)];
    }

    /// <inheritdoc />
    public async Task<Job?> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var row = ToRow(job, firmId: "");
        var expected = expectedStatus.ToWire();

        // Python: Put … attribute_exists(SK) AND #status = :expected.
        // A lost acknowledgement would replay into the status this write set
        // and read null — a worker's claim reported as lost to a rival, the job
        // then left running with nobody on it. The row holding this write's
        // status, attempt count and timestamp is this write.
        var written = await _db.WriteOnceAsync(
            token => _db.Set<JobRow>()
            .Where(stored => stored.CaseId == row.CaseId && stored.Id == row.Id && stored.Status == expected)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Kind, row.Kind)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
                    .SetProperty(stored => stored.Attempts, row.Attempts)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.DocumentId, row.DocumentId)
                    .SetProperty(stored => stored.Options, row.Options)
                    .SetProperty(stored => stored.Failure, row.Failure)
                    .SetProperty(stored => stored.Result, row.Result),
                token),
            token => _db.Set<JobRow>().AsNoTracking().AnyAsync(
                stored => stored.CaseId == row.CaseId && stored.Id == row.Id
                    && stored.Status == row.Status && stored.Attempts == row.Attempts && stored.UpdatedAt == row.UpdatedAt,
                token),
            whenLanded: 1,
            cancellationToken).ConfigureAwait(false);
        return written > 0 ? job : null;
    }

    private static JobRow ToRow(Job job, string firmId) => new()
    {
        CaseId = job.CaseId,
        Id = job.Id,
        FirmId = firmId,
        Kind = job.Kind,
        Status = job.Status.ToWire(),
        CreatedBy = job.CreatedBy,
        Attempts = job.Attempts,
        CreatedAt = Inserts.Utc(job.CreatedAt),
        UpdatedAt = Inserts.Utc(job.UpdatedAt),
        DocumentId = job.DocumentId,
        Options = StoredJson.FromNullableNode(job.Options),
        Failure = job.Failure is null ? null : StoredJson.FromNode(JobRules.FailureJson(job.Failure)),
        Result = StoredJson.FromNullableNode(job.Result),
    };

    private static Job ToDomain(JobRow row)
    {
        if (!Wire.TryParse<JobStatus>(row.Status, out var status))
        {
            throw new ValidationException($"stored job item is malformed: status '{row.Status}'");
        }

        JobFailure? failure = null;
        if (row.Failure is not null)
        {
            var raw = StoredJson.ToObject(row.Failure);
            failure = new JobFailure(Json.AsString(raw["category"]) ?? "", Json.AsString(raw["message"]) ?? "");
        }

        return new Job(row.Id, row.CaseId, row.Kind, status, row.CreatedBy, row.CreatedAt, row.UpdatedAt)
        {
            DocumentId = row.DocumentId,
            Options = StoredJson.ToNullableObject(row.Options),
            Attempts = row.Attempts,
            Failure = failure,
            Result = StoredJson.ToNullableObject(row.Result),
        };
    }
}

/// <summary>
/// The Postgres <see cref="IPacketStore"/> (Python's <c>DynamoDbPacketStore</c>):
/// ONE TRANSACTION, the case's pins conditioned on its <c>updated_at</c> as read
/// and (unless an amendment) on it not being filed, then the packet's once-only insert.
/// </summary>
public sealed class PostgresPacketStore(InsolviaDbContext db) : IPacketStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<bool> CreateAsync(PacketItem packet, Case pinnedCase, DateTimeOffset expectedUpdatedAt, bool allowFiled = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(pinnedCase);
        var pins = CaseRowMapping.ToRow(pinnedCase);
        var readAt = Inserts.Utc(expectedUpdatedAt);
        var filed = CaseRules.FiledStatuses.ToList();
        try
        {
            await _db.InTransactionAsync(
                async token =>
                {
                    var rows = _db.Set<CaseRow>().Where(stored => stored.Id == pins.Id && stored.FirmId == pins.FirmId && stored.UpdatedAt == readAt);
                    if (!allowFiled)
                    {
                        rows = rows.Where(stored => !filed.Contains(stored.Status));
                    }

                    // Only the pins and the version move: the worker read
                    // nothing else it may write back.
                    await rows.ExecuteConditionalUpdateAsync(
                        set => set
                            .SetProperty(stored => stored.FormRevisions, pins.FormRevisions)
                            .SetProperty(stored => stored.ConstantsSetId, pins.ConstantsSetId)
                            .SetProperty(stored => stored.UpdatedAt, pins.UpdatedAt),
                        () => new ConditionNotMetException(),
                        token).ConfigureAwait(false);
                    await _db.InsertOnceAsync(ToRow(packet, pins.FirmId), PacketRow.PrimaryKey, () => new InvalidOperationException("packet id already exists in this case"), token).ConfigureAwait(false);
                },
                cancellationToken,
                // A lost commit acknowledgement: the replay's version CAS no
                // longer matches (this unit moved updated_at) and would answer
                // false. The packet's generated id present is this commit.
                landed: token => _db.Set<PacketRow>().AsNoTracking().AnyAsync(stored => stored.CaseId == packet.CaseId && stored.Id == packet.Id, token)).ConfigureAwait(false);
            return true;
        }
        catch (ConditionNotMetException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<PacketItem?> GetAsync(string caseId, string packetId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<PacketRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == packetId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PacketItem>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<PacketRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToDomain).Order(PacketItems.NewestFirst)];
    }

    private static PacketRow ToRow(PacketItem packet, string firmId) => new()
    {
        CaseId = packet.CaseId,
        Id = packet.Id,
        FirmId = firmId,
        JobId = packet.JobId,
        FileName = packet.FileName,
        ContentType = packet.ContentType,
        ByteSize = packet.ByteSize,
        Sha256 = packet.Sha256,
        StorageRef = packet.StorageRef,
        FormRevisions = StoredJson.FromNode(new JsonObject([.. packet.FormRevisions.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value))])),
        ConstantsSetId = packet.ConstantsSetId,
        CreditorCount = packet.CreditorCount,
        CreatedBy = packet.CreatedBy,
        CreatedAt = Inserts.Utc(packet.CreatedAt),
        Options = StoredJson.FromNullableNode(packet.Options),
        Parts = StoredJson.FromNode(packet.Parts),
    };

    private static PacketItem ToDomain(PacketRow row)
    {
        var revisions = StoredJson.ToObject(row.FormRevisions)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, Json.AsString(pair.Value) ?? throw new ValidationException("stored packet item is malformed: formRevisions")))
            .ToValueDictionary();
        var parts = JsonNode.Parse(row.Parts.RootElement.GetRawText()) as JsonArray ?? [];
        return new PacketItem(
            row.Id,
            row.CaseId,
            row.JobId,
            row.FileName,
            row.ContentType,
            row.ByteSize,
            row.Sha256,
            row.StorageRef,
            revisions,
            row.ConstantsSetId,
            row.CreditorCount,
            row.CreatedBy,
            row.CreatedAt,
            StoredJson.ToNullableObject(row.Options),
            parts);
    }
}
