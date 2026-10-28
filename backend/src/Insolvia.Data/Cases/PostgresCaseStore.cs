using Insolvia.Core;
using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Insolvia.Data.Cases;

/// <summary>
/// The Postgres <see cref="ICaseStore"/> (Python's <c>DynamoDbCaseStore</c>),
/// held to <see cref="Core.Memory.MemoryCaseStore"/> by the conformance suite.
/// <list type="bullet">
/// <item><b>Create</b> — the case, its creator's assignment and its debtors
/// in ONE transaction, each linked client locked <c>FOR SHARE</c> and checked
/// linkable inside it (Python's <c>client_linkable_check</c> ConditionCheck):
/// a merge claim cannot land between the check and the link.</item>
/// <item><b>Update</b> — <c>attribute_exists(PK) AND firmId = :firm AND
/// #status = :expected</c> is the <c>WHERE</c> of one <c>UPDATE</c>, plus the
/// stored status equalling the one written (a plain write never moves it).
/// Zero rows is told apart by one re-read, as Python's
/// <c>_refused_update</c> did: gone or another firm's is null, otherwise a 409.</item>
/// <item><b>Record</b> — the lifecycle write (ADR 0027): the case's
/// <c>UPDATE</c> conditioned on the status, chapter and latest event the
/// writer read (<see cref="CaseRow.LifecycleRecordedAt"/>), and the event's
/// <c>INSERT</c>, whose key (case, recorded_at) is the same-instant rule —
/// one transaction.</item>
/// <item><b>Listings</b> — keyset pages over (created_at DESC, id DESC),
/// cursors bound by <see cref="CursorCodec"/> to the index AND the view.</item>
/// </list>
/// </summary>
public sealed class PostgresCaseStore : ICaseStore
{
    private readonly InsolviaDbContext _db;
    private readonly CursorCodec _cursors;

    /// <summary>A store over <paramref name="db"/> (request-scoped), minting cursors with <paramref name="cursors"/>.</summary>
    public PostgresCaseStore(InsolviaDbContext db, CursorCodec cursors)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
    }

    private IQueryable<CaseRow> Rows => _db.Set<CaseRow>().AsNoTracking();

    private IQueryable<CaseAssignmentRow> Assignments => _db.Set<CaseAssignmentRow>().AsNoTracking();

    /// <inheritdoc />
    public Task CreateAsync(Case created, CaseAssignment assignment, IReadOnlyList<Debtor>? debtors = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(assignment);
        debtors ??= [];
        if (debtors.Any(debtor => debtor.CaseId != created.Id))
        {
            throw new InvalidOperationException("a debtor written with a case must belong to it");
        }

        return _db.InTransactionAsync(
            async token =>
            {
                await _db.InsertOnceAsync(CaseRowMapping.ToRow(created), CaseRow.PrimaryKey, () => new InvalidOperationException($"case {created.Id} already exists"), token).ConfigureAwait(false);
                await _db.InsertOnceAsync(CaseRowMapping.ToRow(assignment, created.FirmId), "pk_case_assignments", () => new InvalidOperationException("assignment already exists"), token).ConfigureAwait(false);

                // Python: one ConditionCheck per distinct client, and the
                // cancellation reason names the client that refused.
                foreach (var clientId in debtors.Select(debtor => debtor.ClientId).OfType<string>().Distinct(StringComparer.Ordinal))
                {
                    if (!await FirmClientLinks.LockLinkableAsync(_db, created.FirmId, clientId, token).ConfigureAwait(false))
                    {
                        throw new ClientUnavailableException(clientId);
                    }
                }

                foreach (var debtor in debtors)
                {
                    await _db.InsertOnceAsync(CaseRowMapping.ToRow(debtor, created.FirmId), DebtorRow.PrimaryKey, () => new InvalidOperationException($"debtor {debtor.FilingRole.ToWire()} already exists"), token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientCase>> ListForClientAsync(string clientId, Accessor accessor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var subject = accessor.Subject;

        // The by-client index: debtors naming the client, each with its case
        // and whether the caller is linked to it — everything MaySeeCase needs.
        var entries = await (
                from debtor in _db.Set<DebtorRow>().AsNoTracking()
                where debtor.ClientId == clientId && debtor.CaseCreatedAt != null
                join row in Rows on debtor.CaseId equals row.Id
                select new
                {
                    debtor.FilingRole,
                    CaseCreatedAt = debtor.CaseCreatedAt!.Value,
                    Case = row,
                    Assigned = Assignments.Any(link => link.CaseId == row.Id && link.Subject == subject),
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. entries
            .OrderByDescending(entry => CaseRules.ListingSortKey(entry.CaseCreatedAt, entry.Case.Id), StringComparer.Ordinal)
            .Select(entry => (Entry: entry, Case: CaseRowMapping.ToDomain(entry.Case)))
            .Where(pair => CaseAccess.MaySeeCase(accessor, pair.Case, pair.Entry.Assigned))
            .Select(pair => new ClientCase(pair.Case, pair.Entry.FilingRole))];
    }

    /// <inheritdoc />
    public async Task<Case?> GetAsync(string caseId, Accessor accessor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var subject = accessor.Subject;

        // Both halves of the access question in one round trip.
        var found = await Rows
            .Where(row => row.Id == caseId)
            .Select(row => new { Case = row, Assigned = Assignments.Any(link => link.CaseId == row.Id && link.Subject == subject) })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var stored = CaseRowMapping.ToDomain(found.Case);
        return CaseAccess.MaySeeCase(accessor, stored, found.Assigned) ? stored : null;
    }

    /// <inheritdoc />
    public async Task<CasePage> ListForAccessorAsync(Accessor accessor, int limit, string? cursor, bool archived = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var firmId = accessor.FirmId;
        var subject = accessor.Subject;
        var index = accessor.SeesEveryCase ? CaseRules.IndexByFirm : CaseRules.IndexByAssignee;

        // The view is a filter in the query, so a page is never short because
        // the rows it skipped were archived or deleted.
        var rows = Rows.Where(row => row.FirmId == firmId && row.DeletedAt == null && (archived ? row.ArchivedAt != null : row.ArchivedAt == null));
        if (!accessor.SeesEveryCase)
        {
            rows = rows.Where(row => Assignments.Any(link => link.CaseId == row.Id && link.Subject == subject));
        }

        Page<CaseRow> page;
        try
        {
            page = await rows.ToKeysetPageAsync(Listing(CaseRules.ListingCursorTag(index, archived)), _cursors, cursor, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidCursorException error)
        {
            throw new ValidationException(InvalidCursorException.Text, error);
        }

        return new CasePage(page.Items.Select(CaseRowMapping.ToDomain).ToValueCollection(), page.NextCursor);
    }

    /// <inheritdoc />
    public async Task<Case?> UpdateAsync(Case updated, CaseStatus? expectedStatus = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var row = CaseRowMapping.ToRow(updated);

        // Python: Put … attribute_exists(PK) AND firmId = :firm AND #status = :expected.
        // The stored status must also be the one written: an edit never moves it.
        var rows = StoredRow(row).Where(stored => stored.Status == row.Status);
        if (expectedStatus is { } expected)
        {
            rows = rows.Where(stored => stored.Status == expected);
        }

        var written = await rows.ExecuteUpdateAsync(set => SetEditable(set, row), cancellationToken).ConfigureAwait(false);
        if (written > 0)
        {
            return updated;
        }

        var stored = await Rows.SingleOrDefaultAsync(found => found.Id == row.Id, cancellationToken).ConfigureAwait(false);
        return stored is null || stored.FirmId != row.FirmId ? null : throw new ConflictException(CaseRules.StatusMoved);
    }

    /// <inheritdoc />
    public async Task<Case?> RecordAsync(LifecycleWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var row = CaseRowMapping.ToRow(write.Case);
        var readAt = write.ReadAt;
        var latest = readAt.LatestRecordedAt;
        var recordedAt = write.Event.RecordedAt;
        try
        {
            await _db.InTransactionAsync(
                async token =>
                {
                    await StoredRow(row)
                        .Where(stored => stored.Status == readAt.Status && stored.Chapter == readAt.Chapter && stored.LifecycleRecordedAt == latest)
                        .ExecuteConditionalUpdateAsync(
                            set => SetEditable(set, row).SetProperty(stored => stored.LifecycleRecordedAt, recordedAt),
                            () => new ConditionNotMetException(),
                            token)
                        .ConfigureAwait(false);
                    await _db.InsertOnceAsync(CaseRowMapping.ToRow(write.Event, row.FirmId), LifecycleEventRow.PrimaryKey, () => new ConditionNotMetException(), token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return write.Case;
        }
        catch (ConditionNotMetException)
        {
            // Why, told apart by one re-read (Python's _refused_update).
            var stored = await Rows.SingleOrDefaultAsync(found => found.Id == row.Id, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.FirmId != row.FirmId)
            {
                return null;
            }

            throw new ConflictException(stored.Status != readAt.Status || stored.Chapter != readAt.Chapter ? CaseRules.StatusMoved : CaseLifecycle.HistoryMoved);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LifecycleEvent>> LifecycleAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<LifecycleEventRow>().AsNoTracking()
            .Where(recorded => recorded.CaseId == caseId)
            .OrderBy(recorded => recorded.RecordedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(CaseRowMapping.ToDomain)];
    }

    /// <inheritdoc />
    public async Task AssignAsync(CaseAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // Unconditional, because the port says idempotent: a replay rewrites
        // only who linked them and when. The firm is the case's own.
        var written = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO insolvia.case_assignments (case_id, subject, firm_id, case_created_at, assigned_at, assigned_by)
            SELECT id, {assignment.Subject}, firm_id, {Inserts.Utc(assignment.CaseCreatedAt)}, {Inserts.Utc(assignment.AssignedAt)}, {assignment.AssignedBy}
            FROM insolvia.cases WHERE id = {assignment.CaseId}
            ON CONFLICT (case_id, subject) DO UPDATE
            SET case_created_at = excluded.case_created_at, assigned_at = excluded.assigned_at, assigned_by = excluded.assigned_by
            """,
            cancellationToken).ConfigureAwait(false);
        if (written == 0)
        {
            throw new InvalidOperationException($"case {assignment.CaseId} does not exist");
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnassignAsync(string caseId, string subject, CancellationToken cancellationToken = default) =>
        await _db.Set<CaseAssignmentRow>()
            .Where(link => link.CaseId == caseId && link.Subject == subject)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseAssignment>> AssigneesAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await Assignments.Where(link => link.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .Select(CaseRowMapping.ToDomain)
            .OrderBy(link => link.AssignedAt)
            .ThenBy(link => link.Subject, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<Case?> ReadForWorkerAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var row = await Rows.SingleOrDefaultAsync(found => found.Id == caseId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<CasePublicStatus?> PublicStatusAsync(ClientAccessor client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var row = await Rows.SingleOrDefaultAsync(found => found.Id == client.CaseId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CasePublicStatuses.From(CaseRowMapping.ToDomain(row), client.FirmId);
    }

    /// <summary>
    /// The listing's keyset: newest first, the id (compared bytewise, as the
    /// memory store's ordinal sort key does) breaking ties. The name carries
    /// the index AND the view, so a cursor from one is refused by the other.
    /// </summary>
#pragma warning disable CA1309 // Translated to SQL, not run: id COLLATE "C" < @id, a bytewise (ordinal) comparison.
    private static Keyset<CaseRow, CaseCursor> Listing(string tag) => new(
        $"cases.{tag}",
        rows => rows.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => EF.Functions.Collate(row.Id, "C")),
        row => new CaseCursor(row.CreatedAt, row.Id),
        (rows, key) => rows.Where(row => row.CreatedAt < key.CreatedAt
            || (row.CreatedAt == key.CreatedAt && string.Compare(EF.Functions.Collate(row.Id, "C"), key.Id) < 0)));
#pragma warning restore CA1309

    // Every column a whole-record write replaces — all but the key, the
    // firm (a condition) and the lifecycle version (only an event moves it).
    private static UpdateSettersBuilder<CaseRow> SetEditable(UpdateSettersBuilder<CaseRow> set, CaseRow row) => set
        .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
        .SetProperty(stored => stored.Chapter, row.Chapter)
        .SetProperty(stored => stored.District, row.District)
        .SetProperty(stored => stored.Status, row.Status)
        .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
        .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
        .SetProperty(stored => stored.Court, row.Court)
        .SetProperty(stored => stored.Division, row.Division)
        .SetProperty(stored => stored.FormRevisions, row.FormRevisions)
        .SetProperty(stored => stored.ConstantsSetId, row.ConstantsSetId)
        .SetProperty(stored => stored.ExemptionSet, row.ExemptionSet)
        .SetProperty(stored => stored.FiledAt, row.FiledAt)
        .SetProperty(stored => stored.Meeting341At, row.Meeting341At)
        .SetProperty(stored => stored.CaseNumber, row.CaseNumber)
        .SetProperty(stored => stored.CaseNumberKey, row.CaseNumberKey)
        .SetProperty(stored => stored.Judge, row.Judge)
        .SetProperty(stored => stored.Trustee, row.Trustee)
        .SetProperty(stored => stored.OfficeFileNumber, row.OfficeFileNumber)
        .SetProperty(stored => stored.ArchivedAt, row.ArchivedAt)
        .SetProperty(stored => stored.ArchivedBy, row.ArchivedBy)
        .SetProperty(stored => stored.DeletedAt, row.DeletedAt)
        .SetProperty(stored => stored.DeletedBy, row.DeletedBy);

    private IQueryable<CaseRow> StoredRow(CaseRow row) =>
        _db.Set<CaseRow>().Where(stored => stored.Id == row.Id && stored.FirmId == row.FirmId);

    /// <summary>A listing cursor's keyset values.</summary>
    private sealed record CaseCursor(DateTimeOffset CreatedAt, string Id);
}
