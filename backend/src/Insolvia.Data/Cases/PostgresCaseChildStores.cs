using Insolvia.Core;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.CaseTasks;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Ports;
using Insolvia.Core.TaxIds;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Cases;

// The case-scoped stores whose DynamoDB originals were one conditional item
// write each, held to their memory twins by the conformance suite:
//   attribute_not_exists(SK) Put  → an INSERT whose primary-key violation is
//                                   the twin's InvalidOperationException;
//   attribute_exists(SK) Put      → an UPDATE … WHERE <key>; zero rows is
//                                   null / false (an edit racing a delete);
//   Delete ReturnValues=ALL_OLD   → a DELETE; one row is "this call removed it";
//   #status = :expected           → the candidate's compare-and-swap, in the WHERE.
// Listings read the whole case and order in code with the domain's own
// comparer, so both stores agree to the byte.

/// <summary>The Postgres <see cref="ICaseEntityStore"/> (Python's <c>DynamoDbCaseEntityStore</c>): every generic collection, notes included, in one table keyed (case, kind, id).</summary>
public sealed class PostgresCaseEntityStore(InsolviaDbContext db) : ICaseEntityStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(ICaseEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var firmId = await CaseFirms.OfAsync(_db, entity.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(entity, firmId), CaseEntityRow.PrimaryKey, () => new InvalidOperationException("entity id already exists in this case"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ICaseEntity?> GetAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var prefix = kind.SkPrefix;
        var row = await _db.Set<CaseEntityRow>().AsNoTracking()
            .SingleOrDefaultAsync(found => found.CaseId == caseId && found.Kind == prefix && found.Id == entityId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row, kind);
    }

    /// <inheritdoc />
    public async Task<bool> PutAsync(ICaseEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Python: Put … attribute_exists(SK). The firm is not rewritten — the
        // row keeps its case's.
        var row = CaseRowMapping.ToRow(entity, firmId: "");
        return await Row(row.CaseId, row.Kind, row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.Body, row.Body)
                    .SetProperty(stored => stored.Provenance, row.Provenance)
                    .SetProperty(stored => stored.Amended, row.Amended),
                cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string caseId, IEntityKind kind, string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return await Row(caseId, kind.SkPrefix, entityId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ICaseEntity>> ListForCaseAsync(string caseId, IEntityKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var prefix = kind.SkPrefix;
        var rows = await _db.Set<CaseEntityRow>().AsNoTracking()
            .Where(row => row.CaseId == caseId && row.Kind == prefix)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => CaseRowMapping.ToDomain(row, kind)).Order(CaseEntityRules.ListOrderComparer)];
    }

    private IQueryable<CaseEntityRow> Row(string caseId, string prefix, string entityId) =>
        _db.Set<CaseEntityRow>().Where(stored => stored.CaseId == caseId && stored.Kind == prefix && stored.Id == entityId);
}

/// <summary>The Postgres <see cref="IDocumentStore"/> (Python's <c>DynamoDbDocumentStore</c>) — metadata only.</summary>
public sealed class PostgresDocumentStore(InsolviaDbContext db) : IDocumentStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var firmId = await CaseFirms.OfAsync(_db, document.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(document, firmId), DocumentRow.PrimaryKey, () => new InvalidOperationException($"document {(document.CaseId, document.Id)} already exists"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Document?> GetAsync(string caseId, string documentId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<DocumentRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == documentId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<Document?> UpdateAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var row = CaseRowMapping.ToRow(document, firmId: "");

        // Python: Put … attribute_exists(SK) — a deleted document is not resurrected.
        var written = await Row(row.CaseId, row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Kind, row.Kind)
                    .SetProperty(stored => stored.FileName, row.FileName)
                    .SetProperty(stored => stored.ContentType, row.ContentType)
                    .SetProperty(stored => stored.ByteSize, row.ByteSize)
                    .SetProperty(stored => stored.StorageRef, row.StorageRef)
                    .SetProperty(stored => stored.UploadedBy, row.UploadedBy)
                    .SetProperty(stored => stored.UploadedAt, row.UploadedAt)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.Etag, row.Etag)
                    .SetProperty(stored => stored.Channel, row.Channel)
                    .SetProperty(stored => stored.RequestId, row.RequestId),
                cancellationToken)
            .ConfigureAwait(false);
        return written > 0 ? document : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<DocumentRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(CaseRowMapping.ToDomain).Order(DocumentRules.ListOrder)];
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string caseId, string documentId, CancellationToken cancellationToken = default) =>
        await Row(caseId, documentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

    private IQueryable<DocumentRow> Row(string caseId, string documentId) =>
        _db.Set<DocumentRow>().Where(stored => stored.CaseId == caseId && stored.Id == documentId);
}

/// <summary>The Postgres <see cref="ICandidateStore"/> (Python's <c>DynamoDbCandidateStore</c>): the review is a compare-and-swap on status.</summary>
public sealed class PostgresCandidateStore(InsolviaDbContext db) : ICandidateStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(Candidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var firmId = await CaseFirms.OfAsync(_db, candidate.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(candidate, firmId), CandidateRow.PrimaryKey, () => new InvalidOperationException("candidate id already exists in this case"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Candidate?> GetAsync(string caseId, string candidateId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<CandidateRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == candidateId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candidate>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<CandidateRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(CaseRowMapping.ToDomain).Order(CandidateRules.ListOrder)];
    }

    /// <inheritdoc />
    public async Task<Candidate?> UpdateAsync(Candidate candidate, CandidateStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var row = CaseRowMapping.ToRow(candidate, firmId: "");

        // Python: Put … attribute_exists(SK) AND #status = :expected. Of two
        // racing reviews exactly one matches; the other reads null.
        var written = await _db.Set<CandidateRow>()
            .Where(stored => stored.CaseId == row.CaseId && stored.Id == row.Id && stored.Status == expectedStatus)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.EntityType, row.EntityType)
                    .SetProperty(stored => stored.Payload, row.Payload)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.OriginChannel, row.OriginChannel)
                    .SetProperty(stored => stored.OriginClientId, row.OriginClientId)
                    .SetProperty(stored => stored.OriginSubject, row.OriginSubject)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.DocumentId, row.DocumentId)
                    .SetProperty(stored => stored.ExternalRef, row.ExternalRef)
                    .SetProperty(stored => stored.Note, row.Note)
                    .SetProperty(stored => stored.Confidence, row.Confidence)
                    .SetProperty(stored => stored.Locator, row.Locator)
                    .SetProperty(stored => stored.ConfirmedBy, row.ConfirmedBy)
                    .SetProperty(stored => stored.ConfirmedAt, row.ConfirmedAt)
                    .SetProperty(stored => stored.CorrectedPayload, row.CorrectedPayload)
                    .SetProperty(stored => stored.ResultingRecordId, row.ResultingRecordId),
                cancellationToken)
            .ConfigureAwait(false);
        return written > 0 ? candidate : null;
    }
}

/// <summary>The Postgres <see cref="ICaseTaskStore"/> (Python's <c>DynamoDbTaskStore</c>).</summary>
public sealed class PostgresCaseTaskStore(InsolviaDbContext db) : ICaseTaskStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(CaseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var firmId = await CaseFirms.OfAsync(_db, task.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(task, firmId), CaseTaskRow.PrimaryKey, () => new InvalidOperationException("task id already exists in this case"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CaseTask?> GetAsync(string caseId, string taskId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<CaseTaskRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == taskId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<CaseTask?> UpdateAsync(CaseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var row = CaseRowMapping.ToRow(task, firmId: "");
        var written = await Row(row.CaseId, row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Subject, row.Subject)
                    .SetProperty(stored => stored.Description, row.Description)
                    .SetProperty(stored => stored.DueDate, row.DueDate)
                    .SetProperty(stored => stored.AssigneeSubject, row.AssigneeSubject)
                    .SetProperty(stored => stored.FormSeries, row.FormSeries)
                    .SetProperty(stored => stored.Done, row.Done)
                    .SetProperty(stored => stored.CompletedAt, row.CompletedAt)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.CreatedBy, row.CreatedBy),
                cancellationToken)
            .ConfigureAwait(false);
        return written > 0 ? task : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseTask>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<CaseTaskRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(CaseRowMapping.ToDomain).Order(CaseTaskRules.ListOrder)];
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string caseId, string taskId, CancellationToken cancellationToken = default) =>
        await Row(caseId, taskId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

    private IQueryable<CaseTaskRow> Row(string caseId, string taskId) =>
        _db.Set<CaseTaskRow>().Where(stored => stored.CaseId == caseId && stored.Id == taskId);
}

/// <summary>The Postgres <see cref="IDocumentRequestStore"/> (Python's <c>DynamoDbDocumentRequestStore</c>). Last writer wins.</summary>
public sealed class PostgresDocumentRequestStore(InsolviaDbContext db) : IDocumentRequestStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task CreateAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var firmId = await CaseFirms.OfAsync(_db, request.CaseId, cancellationToken).ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(request, firmId), DocumentRequestRow.PrimaryKey, () => new InvalidOperationException("document request id already exists in this case"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentRequest?> GetAsync(string caseId, string requestId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<DocumentRequestRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Id == requestId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<DocumentRequest?> UpdateAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = CaseRowMapping.ToRow(request, firmId: "");
        var written = await Row(row.CaseId, row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Title, row.Title)
                    .SetProperty(stored => stored.Kind, row.Kind)
                    .SetProperty(stored => stored.Description, row.Description)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.DocumentIds, row.DocumentIds)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.ReceivedAt, row.ReceivedAt),
                cancellationToken)
            .ConfigureAwait(false);
        return written > 0 ? request : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentRequest>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<DocumentRequestRow>().AsNoTracking().Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(CaseRowMapping.ToDomain).Order(DocumentRequestRules.ListOrder)];
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string caseId, string requestId, CancellationToken cancellationToken = default) =>
        await Row(caseId, requestId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;

    private IQueryable<DocumentRequestRow> Row(string caseId, string requestId) =>
        _db.Set<DocumentRequestRow>().Where(stored => stored.CaseId == caseId && stored.Id == requestId);
}

/// <summary>
/// The Postgres <see cref="ITaxIdStore"/> (Python's <c>DynamoDbTaxIdStore</c>):
/// an upsert by (case, ref) — a correction reaches every matter pointing at
/// it. Stores the envelope's bytes, never opens them (that is 18.12's cipher).
/// </summary>
public sealed class PostgresTaxIdStore(InsolviaDbContext db) : ITaxIdStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task PutAsync(string caseId, SealedTaxId sealedId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sealedId);
        var row = CaseRowMapping.ToRow(caseId, sealedId);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO insolvia.tax_ids AS sealed
                (case_id, ref, firm_id, kind, last_four, scheme, ciphertext, nonce, wrapped_key, created_at, updated_at)
            VALUES ({row.CaseId}, {row.Ref}, {row.FirmId}, {row.Kind.ToWire()}, {row.LastFour}, {row.Scheme},
                {row.Ciphertext}, {row.Nonce}, {row.WrappedKey}, {Inserts.Utc(row.CreatedAt)}, {Inserts.Utc(row.UpdatedAt)})
            ON CONFLICT (case_id, ref) DO UPDATE
            SET firm_id = excluded.firm_id, kind = excluded.kind, last_four = excluded.last_four, scheme = excluded.scheme,
                ciphertext = excluded.ciphertext, nonce = excluded.nonce, wrapped_key = excluded.wrapped_key,
                created_at = excluded.created_at, updated_at = excluded.updated_at
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SealedTaxId?> GetAsync(string caseId, string reference, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<TaxIdRow>().AsNoTracking().SingleOrDefaultAsync(found => found.CaseId == caseId && found.Ref == reference, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }
}
