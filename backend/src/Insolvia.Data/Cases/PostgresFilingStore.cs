using Insolvia.Core.Cases;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Cases;

/// <summary>
/// The Postgres <see cref="IFilingStore"/> (Python's <c>DynamoDbFilingStore</c>),
/// held to <see cref="Core.Memory.MemoryFilingStore"/> by the conformance
/// suite. Every write is conditional, and the conditions are the idempotency:
/// <list type="bullet">
/// <item><c>claim</c> — <c>attribute_not_exists(PK)</c>: the primary key;
/// of two racing consumers exactly one inserts.</item>
/// <item><c>transition</c> — <c>#state = :expected AND attemptId = :attempt</c>:
/// the <c>WHERE</c> of the filing's <c>UPDATE</c>.</item>
/// <item><c>resolve</c> — the same AND <c>attribute_not_exists(resolution)</c>:
/// <c>resolution IS NULL</c>.</item>
/// <item>With a filed case — ONE transaction of the filing's <c>UPDATE</c>,
/// the case's <c>UPDATE</c> of exactly the lifecycle columns (status, petition
/// date, case number and its key, <c>updated_at</c>, the lifecycle version)
/// conditioned on it existing undeleted in its firm at the version the writer
/// read, and the lifecycle event's <c>INSERT</c> (its key refuses an event at
/// the same instant). Any zero rows rolls the whole unit back: false.</item>
/// </list>
/// </summary>
public sealed class PostgresFilingStore(InsolviaDbContext db) : IFilingStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<Filing?> GetAsync(string caseId, string filingId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<FilingRow>().AsNoTracking()
            .SingleOrDefaultAsync(found => found.CaseId == caseId && found.FilingId == filingId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<bool> ClaimAsync(Filing filing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filing);
        try
        {
            await _db.InsertOnceAsync(CaseRowMapping.ToRow(filing), FilingRow.PrimaryKey, () => new ConditionNotMetException(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConditionNotMetException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TransitionAsync(Filing filing, FilingState expectedState, LifecycleWrite? filedCase = null, CancellationToken cancellationToken = default) =>
        WriteAsync(filing, expectedState, filedCase, unresolved: false, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ResolveAsync(Filing filing, FilingState expectedState, LifecycleWrite? filedCase = null, CancellationToken cancellationToken = default) =>
        WriteAsync(filing, expectedState, filedCase, unresolved: true, cancellationToken);

    private async Task<bool> WriteAsync(Filing filing, FilingState expectedState, LifecycleWrite? filedCase, bool unresolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filing);
        if (filedCase is not null && (!filedCase.FilesTheCase || filedCase.Case.Id != filing.CaseId))
        {
            throw new ArgumentException("a filing's case half moves its own case into filed", nameof(filedCase));
        }

        var row = CaseRowMapping.ToRow(filing);
        try
        {
            await _db.InTransactionAsync(
                async token =>
                {
                    var rows = _db.Set<FilingRow>().Where(stored =>
                        stored.CaseId == row.CaseId && stored.FilingId == row.FilingId && stored.State == expectedState && stored.AttemptId == row.AttemptId);
                    if (unresolved)
                    {
                        rows = rows.Where(stored => stored.Resolution == null);
                    }

                    await rows.ExecuteConditionalUpdateAsync(
                            set => set
                                .SetProperty(stored => stored.FirmId, row.FirmId)
                                .SetProperty(stored => stored.ApprovalId, row.ApprovalId)
                                .SetProperty(stored => stored.AttorneyId, row.AttorneyId)
                                .SetProperty(stored => stored.CredentialId, row.CredentialId)
                                .SetProperty(stored => stored.Court, row.Court)
                                .SetProperty(stored => stored.Division, row.Division)
                                .SetProperty(stored => stored.State, row.State)
                                .SetProperty(stored => stored.ClaimedAt, row.ClaimedAt)
                                .SetProperty(stored => stored.LeaseExpiresAt, row.LeaseExpiresAt)
                                .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                                .SetProperty(stored => stored.History, row.History)
                                .SetProperty(stored => stored.Driver, row.Driver)
                                .SetProperty(stored => stored.HandBack, row.HandBack)
                                .SetProperty(stored => stored.UnknownReason, row.UnknownReason)
                                .SetProperty(stored => stored.Confirmation, row.Confirmation)
                                .SetProperty(stored => stored.ReceiptDocumentId, row.ReceiptDocumentId)
                                .SetProperty(stored => stored.FollowUps, row.FollowUps)
                                .SetProperty(stored => stored.Resolution, row.Resolution),
                            () => new ConditionNotMetException(),
                            token)
                        .ConfigureAwait(false);

                    if (filedCase is not null)
                    {
                        await FileTheCaseAsync(filedCase, token).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConditionNotMetException)
        {
            return false;
        }
    }

    // The case half: exactly the attributes an electronic filing changes, so
    // a concurrent edit of anything else on the case is never written back
    // over (Python's _FILED_ATTRIBUTES UpdateItem).
    private async Task FileTheCaseAsync(LifecycleWrite write, CancellationToken cancellationToken)
    {
        var filed = CaseRowMapping.ToRow(write.Case);
        var readAt = write.ReadAt;
        var latest = readAt.LatestRecordedAt;
        var recordedAt = write.Event.RecordedAt;
        await _db.Set<CaseRow>()
            .Where(stored => stored.Id == filed.Id
                && stored.FirmId == filed.FirmId
                && stored.DeletedAt == null
                && stored.Status == readAt.Status
                && stored.Chapter == readAt.Chapter
                && stored.LifecycleRecordedAt == latest)
            .ExecuteConditionalUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Status, filed.Status)
                    .SetProperty(stored => stored.FiledAt, filed.FiledAt)
                    .SetProperty(stored => stored.CaseNumber, filed.CaseNumber)
                    .SetProperty(stored => stored.CaseNumberKey, filed.CaseNumberKey)
                    .SetProperty(stored => stored.UpdatedAt, filed.UpdatedAt)
                    .SetProperty(stored => stored.LifecycleRecordedAt, recordedAt),
                () => new ConditionNotMetException(),
                cancellationToken)
            .ConfigureAwait(false);
        await _db.InsertOnceAsync(CaseRowMapping.ToRow(write.Event, filed.FirmId), LifecycleEventRow.PrimaryKey, () => new ConditionNotMetException(), cancellationToken).ConfigureAwait(false);
    }
}
