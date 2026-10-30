using Insolvia.Core;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insolvia.Data.Vault;

/// <summary>
/// The Postgres <see cref="IFilingAuthorizationStore"/> (Python's
/// <c>DynamoDbFilingAuthorizationStore</c>): the current pointer
/// (<c>current_filing_authorizations</c>, the <c>AUTHORIZATION</c> item) and
/// one history row per signature (<c>filing_authorizations</c>,
/// <c>AUTHORIZATION#&lt;id&gt;</c>).
/// </summary>
/// <remarks>
/// Python's writes were not transactional, and their ORDER made that safe
/// (the pointer first, so a lost race failed before any history existed).
/// Here each write is one transaction, so the order no longer carries the
/// guarantee: a lost race rolls its history row back with it. The pointer's
/// precondition is still in the statement's <c>WHERE</c> (or its
/// <c>ON CONFLICT</c>), never a read-then-write.
/// </remarks>
public sealed class PostgresFilingAuthorizationStore(InsolviaDbContext db) : IFilingAuthorizationStore
{
    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    /// <remarks>Python: <c>GetItem(SK = AUTHORIZATION, ConsistentRead)</c> → the pointer joined to its signature.</remarks>
    public async Task<FilingAuthorization?> GetCurrentAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default)
    {
        var row = await (
                from current in _db.Set<CurrentFilingAuthorizationRow>().AsNoTracking()
                join signature in _db.Set<FilingAuthorizationRow>().AsNoTracking()
                    on current.AuthorizationId equals signature.AuthorizationId
                where current.FirmId == firmId && current.AttorneyId == attorneyId
                select signature)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Python: <c>PutItem(AUTHORIZATION) … attribute_not_exists(SK)</c> when
    /// replacing nothing → <c>INSERT … ON CONFLICT DO NOTHING</c>;
    /// <c>… authorizationId = :held</c> when replacing → <c>UPDATE … WHERE
    /// authorization_id = :held</c>. Zero rows is the <see cref="ConflictException"/>
    /// Python raised on <c>ConditionalCheckFailed</c>. Then the history: the
    /// new signature's row (<c>attribute_not_exists</c> → its primary key) and
    /// the replaced one marked superseded.
    /// </remarks>
    public Task PutCurrentAsync(FilingAuthorization authorization, FilingAuthorization? replacing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return _db.InTransactionAsync(
            async token =>
            {
                // The history row first: the pointer's foreign key needs it.
                // A lost race below rolls it back.
                await _db.InsertOnceAsync(
                    ToRow(authorization),
                    FilingAuthorizationRow.PrimaryKey,
                    () => new InvalidOperationException("authorization id already exists"),
                    token).ConfigureAwait(false);

                var pointer = replacing is null
                    ? (FormattableString)$"""
                        INSERT INTO insolvia.current_filing_authorizations (firm_id, attorney_id, authorization_id)
                        VALUES ({authorization.FirmId}, {authorization.AttorneyId}, {authorization.AuthorizationId})
                        ON CONFLICT (firm_id, attorney_id) DO NOTHING
                        """
                    : $"""
                        UPDATE insolvia.current_filing_authorizations
                        SET authorization_id = {authorization.AuthorizationId}
                        WHERE firm_id = {authorization.FirmId} AND attorney_id = {authorization.AttorneyId}
                          AND authorization_id = {replacing.AuthorizationId}
                        """;
                await _db.Database.ExecuteConditionalSqlAsync(
                    pointer,
                    () => new ConflictException("the authorization changed while signing"),
                    token).ConfigureAwait(false);

                if (replacing is not null)
                {
                    await _db.Set<FilingAuthorizationRow>()
                        .Where(row => row.AuthorizationId == replacing.AuthorizationId)
                        .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, AuthorizationStatus.Superseded), token)
                        .ConfigureAwait(false);
                }
            },
            cancellationToken,
            // A lost commit acknowledgement: the replay meets its own history
            // row and would throw "already exists". The pointer on this
            // signature's generated id is this commit.
            landed: token => _db.Set<CurrentFilingAuthorizationRow>().AsNoTracking().AnyAsync(
                row => row.FirmId == authorization.FirmId && row.AttorneyId == authorization.AttorneyId && row.AuthorizationId == authorization.AuthorizationId,
                token));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Python: <c>DeleteItem(AUTHORIZATION) … authorizationId = :held</c> →
    /// <c>DELETE … WHERE authorization_id = :held</c>; zero rows is false.
    /// The pointer goes first — from that statement's commit on, nothing the
    /// attorney holds is openable — and the history row is marked withdrawn
    /// in the same transaction.
    /// </remarks>
    public Task<bool> WithdrawAsync(FilingAuthorization authorization, DateTimeOffset withdrawnAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var at = Inserts.Utc(withdrawnAt);
        return _db.InTransactionAsync(
            async token =>
            {
                var removed = await _db.Set<CurrentFilingAuthorizationRow>()
                    .Where(row => row.FirmId == authorization.FirmId
                        && row.AttorneyId == authorization.AttorneyId
                        && row.AuthorizationId == authorization.AuthorizationId)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                if (removed == 0)
                {
                    return false;
                }

                await _db.Set<FilingAuthorizationRow>()
                    .Where(row => row.AuthorizationId == authorization.AuthorizationId)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(row => row.Status, AuthorizationStatus.Withdrawn)
                            .SetProperty(row => row.WithdrawnAt, at),
                        token)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            // A lost commit acknowledgement: the replay finds no pointer to
            // delete and would answer false. The signature marked withdrawn at
            // this call's instant is this commit.
            landed: async token => await _db.Set<FilingAuthorizationRow>().AsNoTracking().AnyAsync(
                    row => row.AuthorizationId == authorization.AuthorizationId && row.Status == AuthorizationStatus.Withdrawn && row.WithdrawnAt == at,
                    token).ConfigureAwait(false)
                ? new ExecutionResult<bool>(true, true)
                : new ExecutionResult<bool>(false, false));
    }

    internal static FilingAuthorizationRow ToRow(FilingAuthorization authorization) => new()
    {
        AuthorizationId = authorization.AuthorizationId,
        FirmId = authorization.FirmId,
        AttorneyId = authorization.AttorneyId,
        TextVersion = authorization.TextVersion,
        TextDigest = authorization.TextDigest,
        SignedAt = authorization.SignedAt,
        AuthenticatedAt = authorization.AuthenticatedAt,
        Status = authorization.Status,
        WithdrawnAt = authorization.WithdrawnAt,
    };

    internal static FilingAuthorization ToDomain(FilingAuthorizationRow row) => new(
        row.AuthorizationId,
        row.FirmId,
        row.AttorneyId,
        row.TextVersion,
        row.TextDigest,
        row.SignedAt,
        row.AuthenticatedAt,
        row.Status,
        row.WithdrawnAt);
}
