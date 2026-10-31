using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insolvia.Data.Cases;

/// <summary>
/// The Postgres <see cref="IDebtorStore"/> (Python's <c>DynamoDbDebtorStore</c>),
/// held to <see cref="Core.Memory.MemoryDebtorStore"/> by the conformance suite.
/// <list type="bullet">
/// <item><c>create</c>'s <c>attribute_not_exists(SK)</c> is the primary key
/// (case, role); <c>put</c> is an upsert.</item>
/// <item><c>link</c> and <c>repoint_client</c> were one transaction of a write
/// plus a <c>ConditionCheck</c> on each OTHER role (and, for a link, on the
/// client's row). Here: the case row is locked (<c>FOR NO KEY UPDATE</c>, so
/// links to one case queue), the other roles are read under that lock, the
/// client row is locked <c>FOR SHARE</c> (<see cref="FirmClientLinks"/>), and
/// the outcome is answered in the contract's order — role_taken, client_taken,
/// client_unavailable — before anything is written. The partial unique index
/// on (case, client) makes "one client, one role" structural besides.</item>
/// </list>
/// </summary>
public sealed class PostgresDebtorStore : IDebtorStore
{
    private readonly InsolviaDbContext _db;

    /// <summary>A store over <paramref name="db"/> (request-scoped).</summary>
    public PostgresDebtorStore(InsolviaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    private IQueryable<DebtorRow> Rows => _db.Set<DebtorRow>().AsNoTracking();

    /// <inheritdoc />
    public async Task<bool> CreateAsync(Debtor debtor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var firmId = await CaseFirms.OfAsync(_db, debtor.CaseId, cancellationToken).ConfigureAwait(false);
        try
        {
            // The key is (case, role), which callers compete for — a conflict
            // is the answer "role taken" — so a retry recognises its own row
            // by the debtor's generated id.
            var id = debtor.Id;
            await _db.InsertOnceAsync(CaseRowMapping.ToRow(debtor, firmId), DebtorRow.PrimaryKey, () => new ConditionNotMetException(), cancellationToken, alsoMatching: row => row.Id == id).ConfigureAwait(false);
            return true;
        }
        catch (ConditionNotMetException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(Debtor debtor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var firmId = await CaseFirms.OfAsync(_db, debtor.CaseId, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(CaseRowMapping.ToRow(debtor, firmId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LinkOutcome> LinkAsync(Debtor debtor, bool create, string firmId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var clientId = debtor.ClientId ?? throw new ArgumentException("link needs a debtor that names a client", nameof(debtor));
        return await _db.InTransactionAsync(
            async token =>
            {
                var caseFirm = await LockCaseAsync(debtor.CaseId, token).ConfigureAwait(false);
                var others = await Rows.Where(row => row.CaseId == debtor.CaseId).ToListAsync(token).ConfigureAwait(false);
                if (create && others.Any(row => row.FilingRole == debtor.FilingRole))
                {
                    return LinkOutcome.RoleTaken;
                }

                if (others.Any(row => row.FilingRole != debtor.FilingRole && row.ClientId == clientId))
                {
                    return LinkOutcome.ClientTaken;
                }

                if (caseFirm != firmId || !await FirmClientLinks.LockLinkableAsync(_db, firmId, clientId, token).ConfigureAwait(false))
                {
                    return LinkOutcome.ClientUnavailable;
                }

                await UpsertAsync(CaseRowMapping.ToRow(debtor, caseFirm), token).ConfigureAwait(false);
                return LinkOutcome.Written;
            },
            cancellationToken,
            // A lost commit acknowledgement: with create, the replay finds the
            // role filled — by this write — and would answer RoleTaken. The
            // row carrying this debtor's id, client and timestamp is this write.
            landed: async token => await Rows.AnyAsync(
                    row => row.CaseId == debtor.CaseId && row.FilingRole == debtor.FilingRole
                        && row.Id == debtor.Id && row.ClientId == clientId && row.UpdatedAt == debtor.UpdatedAt,
                    token).ConfigureAwait(false)
                ? new ExecutionResult<LinkOutcome>(true, LinkOutcome.Written)
                : new ExecutionResult<LinkOutcome>(false, default)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var rows = await Rows
            .Where(row => row.ClientId == clientId)
            .Select(row => new { row.CaseId, row.FilingRole })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .Select(row => (row.CaseId, row.FilingRole))
            .OrderBy(key => key.CaseId, StringComparer.Ordinal)
            .ThenBy(key => key.FilingRole.ToWire(), StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default) =>
        _db.InTransactionAsync(
            async token =>
            {
                await LockCaseAsync(caseId, token).ConfigureAwait(false);
                var rows = await Rows.Where(row => row.CaseId == caseId).ToListAsync(token).ConfigureAwait(false);

                // Python: Update … clientId = :from, then a ConditionCheck on
                // each other role that it does not name the survivor.
                var own = rows.SingleOrDefault(row => row.FilingRole == filingRole);
                if (own is null || own.ClientId != fromClientId)
                {
                    return RepointOutcome.Absent;
                }

                if (rows.Any(row => row.FilingRole != filingRole && row.ClientId == toClientId))
                {
                    return RepointOutcome.ClientTaken;
                }

                // Only the link moves — body and provenance are not in the SET.
                var caseCreatedAt = own.CaseCreatedAt ?? throw new InvalidOperationException("a debtor naming a client has no case_created_at");
                var moved = DebtorRules.LinkClient(CaseRowMapping.ToDomain(own), toClientId, caseCreatedAt);
                await _db.Set<DebtorRow>()
                    .Where(row => row.CaseId == caseId && row.FilingRole == filingRole && row.ClientId == fromClientId)
                    .ExecuteConditionalUpdateAsync(
                        set => set
                            .SetProperty(row => row.ClientId, moved.ClientId)
                            .SetProperty(row => row.CaseCreatedAt, moved.CaseCreatedAt)
                            .SetProperty(row => row.UpdatedAt, moved.UpdatedAt),
                        () => new ConflictException("a case's debtors changed while the clients were being merged; run the merge again to finish it"),
                        token)
                    .ConfigureAwait(false);
                return RepointOutcome.Written;
            },
            cancellationToken,
            // A lost commit acknowledgement: the replay finds the role naming
            // the survivor and would answer Absent. The role naming it is the
            // outcome this call asked for, whoever wrote it.
            landed: async token => await Rows.AnyAsync(
                    row => row.CaseId == caseId && row.FilingRole == filingRole && row.ClientId == toClientId,
                    token).ConfigureAwait(false)
                ? new ExecutionResult<RepointOutcome>(true, RepointOutcome.Written)
                : new ExecutionResult<RepointOutcome>(false, default));

    /// <inheritdoc />
    public async Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var debtors = await ListForCaseAsync(caseId, cancellationToken).ConfigureAwait(false);
        return [.. debtors.Select(debtor => new DebtorClientLink(caseId, debtor.FilingRole, debtor.ClientId))];
    }

    /// <inheritdoc />
    public async Task<Debtor?> GetAsync(string caseId, FilingRole filingRole, CancellationToken cancellationToken = default)
    {
        var row = await Rows.SingleOrDefaultAsync(found => found.CaseId == caseId && found.FilingRole == filingRole, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CaseRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Debtor>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await Rows.Where(row => row.CaseId == caseId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .Select(CaseRowMapping.ToDomain)
            .OrderBy(debtor => DebtorRules.RoleOrder(debtor.FilingRole))
            .ThenBy(debtor => debtor.FilingRole.ToWire(), StringComparer.Ordinal)];
    }

    // The case row, locked against every other link of the same case until
    // this transaction ends; its firm, or a refusal when it does not exist.
    private async Task<string> LockCaseAsync(string caseId, CancellationToken cancellationToken)
    {
        var firms = await _db.Database
            .SqlQuery<string>($"SELECT firm_id AS \"Value\" FROM insolvia.cases WHERE id = {caseId} FOR NO KEY UPDATE")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return firms.Count == 1 ? firms[0] : throw new InvalidOperationException($"case {caseId} does not exist");
    }

    private Task<int> UpsertAsync(DebtorRow row, CancellationToken cancellationToken) =>
        _db.ExecuteIdempotentSqlAsync(
            $"""
            INSERT INTO insolvia.debtors AS debtor
                (case_id, filing_role, firm_id, id, created_at, updated_at, body, provenance,
                 tax_id_kind, tax_id_last_four, tax_id_ref, client_id, case_created_at)
            VALUES ({row.CaseId}, {row.FilingRole.ToWire()}, {row.FirmId}, {row.Id}, {Inserts.Utc(row.CreatedAt)}, {Inserts.Utc(row.UpdatedAt)},
                {row.Body}, {row.Provenance}, {row.TaxIdKind?.ToWire()}, {row.TaxIdLastFour}, {row.TaxIdRef}, {row.ClientId},
                {(row.CaseCreatedAt is { } at ? Inserts.Utc(at) : (DateTimeOffset?)null)})
            ON CONFLICT (case_id, filing_role) DO UPDATE
            SET firm_id = excluded.firm_id, id = excluded.id, created_at = excluded.created_at, updated_at = excluded.updated_at,
                body = excluded.body, provenance = excluded.provenance, tax_id_kind = excluded.tax_id_kind,
                tax_id_last_four = excluded.tax_id_last_four, tax_id_ref = excluded.tax_id_ref,
                client_id = excluded.client_id, case_created_at = excluded.case_created_at
            """,
            cancellationToken);
}

/// <summary>The firm a case belongs to — the firm_id a child row's composite foreign key needs.</summary>
internal static class CaseFirms
{
    /// <summary>The case's firm; a child of a case that does not exist is refused.</summary>
    public static async Task<string> OfAsync(InsolviaDbContext db, string caseId, CancellationToken cancellationToken) =>
        await db.Set<CaseRow>().AsNoTracking()
            .Where(row => row.Id == caseId)
            .Select(row => row.FirmId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException($"case {caseId} does not exist");
}
