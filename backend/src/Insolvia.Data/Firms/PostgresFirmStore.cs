using Insolvia.Core;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Ports;
using Insolvia.Core.Questionnaires;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Firms;

/// <summary>
/// The Postgres <see cref="IFirmStore"/> (Python's <c>DynamoDbFirmStore</c>),
/// held to <see cref="Core.Memory.MemoryFirmStore"/> by the conformance suite.
/// Every DynamoDB condition is the <c>WHERE</c> of one statement, and every
/// <c>TransactWriteItems</c> is one transaction:
/// <list type="bullet">
/// <item><c>attribute_not_exists</c> puts are inserts whose primary-key
/// violation is the memory store's <see cref="InvalidOperationException"/>.</item>
/// <item><c>attribute_exists(SK) AND firmId = :firm</c> is a primary-key
/// match on <c>(firm_id, …)</c> — tenancy is part of the key, so a record
/// carrying another firm's id matches no row and the method answers null.</item>
/// <item>The client merge's <c>#into</c>/<c>#from</c> conditions are
/// predicates on the <c>merging_into</c>/<c>merging_from</c> columns, both
/// rows locked in id order first so two opposite claims queue instead of
/// deadlocking.</item>
/// </list>
/// Listings are whole (the port never truncates them) and are ordered in
/// code with the memory store's comparers, so both stores agree to the byte —
/// a SQL collation would not match <c>StringComparer.Ordinal</c> or
/// <c>PyText.Casefold</c>.
/// </summary>
public sealed class PostgresFirmStore : IFirmStore
{
    private readonly InsolviaDbContext _db;
    private readonly TimeProvider? _clock;

    /// <summary>A store over <paramref name="db"/> (request-scoped); <paramref name="clock"/> stamps the funnel and retained writes.</summary>
    public PostgresFirmStore(InsolviaDbContext db, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock;
    }

    private IQueryable<FirmRow> Firms => _db.Set<FirmRow>().AsNoTracking();

    private IQueryable<FirmUserRow> Users => _db.Set<FirmUserRow>().AsNoTracking();

    private IQueryable<LibraryCreditorRow> Creditors => _db.Set<LibraryCreditorRow>().AsNoTracking();

    private IQueryable<FirmClientRow> Clients => _db.Set<FirmClientRow>().AsNoTracking();

    // ── Firms ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CreateFirmAsync(Firm firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        return _db.InsertOnceAsync(
            FirmRowMapping.ToRow(firm),
            FirmRow.PrimaryKey,
            () => new InvalidOperationException($"firm {firm.Id} already exists"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Firm?> GetFirmAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var row = await Firms.SingleOrDefaultAsync(firm => firm.Id == firmId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Firm>> ListFirmsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await Firms.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .Select(FirmRowMapping.ToDomain)
            .OrderBy(firm => firm.Name, StringComparer.Ordinal)
            .ThenBy(firm => firm.Id, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<Firm?> UpdateFirmAsync(Firm firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        var row = FirmRowMapping.ToRow(firm);

        // Python: put_item … ConditionExpression attribute_exists(PK).
        var updated = await _db.Set<FirmRow>()
            .Where(stored => stored.Id == row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Name, row.Name)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
                    .SetProperty(stored => stored.CreatedByEmail, row.CreatedByEmail)
                    .SetProperty(stored => stored.DefaultCourt, row.DefaultCourt)
                    .SetProperty(stored => stored.DefaultDivision, row.DefaultDivision)
                    .SetProperty(stored => stored.DefaultChapter, row.DefaultChapter)
                    .SetProperty(stored => stored.Letterhead, row.Letterhead),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 0 ? null : firm;
    }

    // ── Firm users ──────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AddUserAsync(FirmUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _db.InsertOnceAsync(
            FirmRowMapping.ToRow(user),
            FirmUserRow.PrimaryKey,
            () => new InvalidOperationException("firm user already exists"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FirmUser?> GetUserAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        var row = await Users
            .SingleOrDefaultAsync(user => user.FirmId == firmId && user.Subject == subject, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<FirmUser?> FindUserAsync(string subject, CancellationToken cancellationToken = default)
    {
        // LIMIT 2, as the GSI query's Limit=2: a second row is the invariant
        // violation the port requires a throw on.
        var rows = await Users.Where(user => user.Subject == subject).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > 1)
        {
            throw new InvalidOperationException("a firm user resolves to more than one firm; refusing to guess");
        }

        return rows.Count == 0 ? null : FirmRowMapping.ToDomain(rows[0]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FirmUser>> ListUsersAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var rows = await Users.Where(user => user.FirmId == firmId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .Select(FirmRowMapping.ToDomain)
            .OrderBy(user => user.LastName, StringComparer.Ordinal)
            .ThenBy(user => user.FirstName, StringComparer.Ordinal)
            .ThenBy(user => user.Subject, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<FirmUser?> UpdateUserAsync(FirmUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var row = FirmRowMapping.ToRow(user);

        // Python: attribute_exists(SK) AND firmId = :firm — here the key itself.
        var updated = await _db.Set<FirmUserRow>()
            .Where(stored => stored.FirmId == row.FirmId && stored.Subject == row.Subject)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Email, row.Email)
                    .SetProperty(stored => stored.FirstName, row.FirstName)
                    .SetProperty(stored => stored.LastName, row.LastName)
                    .SetProperty(stored => stored.Role, row.Role)
                    .SetProperty(stored => stored.IsAdmin, row.IsAdmin)
                    .SetProperty(stored => stored.AccessAllCases, row.AccessAllCases)
                    .SetProperty(stored => stored.Permissions, row.Permissions)
                    .SetProperty(stored => stored.Status, row.Status)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                    .SetProperty(stored => stored.SignatureBlock, row.SignatureBlock),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 0 ? null : user;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUserAsync(string firmId, string subject, CancellationToken cancellationToken = default) =>
        await _db.Set<FirmUserRow>()
            .Where(user => user.FirmId == firmId && user.Subject == subject)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    // ── Library creditors ───────────────────────────────────────────

    /// <inheritdoc />
    public Task CreateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        return _db.InsertOnceAsync(
            FirmRowMapping.ToRow(creditor),
            LibraryCreditorRow.PrimaryKey,
            () => new InvalidOperationException($"library creditor {creditor.Id} already exists"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LibraryCreditor?> GetLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default)
    {
        var row = await Creditors
            .SingleOrDefaultAsync(creditor => creditor.FirmId == firmId && creditor.Id == creditorId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryCreditor>> ListLibraryCreditorsAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var rows = await Creditors.Where(creditor => creditor.FirmId == firmId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows
            .Select(FirmRowMapping.ToDomain)
            .OrderBy(creditor => creditor.Name, StringComparer.Ordinal)
            .ThenBy(creditor => creditor.Id, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<LibraryCreditor?> UpdateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        var row = FirmRowMapping.ToRow(creditor);
        var updated = await _db.Set<LibraryCreditorRow>()
            .Where(stored => stored.FirmId == row.FirmId && stored.Id == row.Id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Name, row.Name)
                    .SetProperty(stored => stored.Address, row.Address)
                    .SetProperty(stored => stored.AdditionalNoticeParties, row.AdditionalNoticeParties)
                    .SetProperty(stored => stored.Preferred, row.Preferred)
                    .SetProperty(stored => stored.Notes, row.Notes)
                    .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                    .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt),
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 0 ? null : creditor;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default) =>
        await _db.Set<LibraryCreditorRow>()
            .Where(creditor => creditor.FirmId == firmId && creditor.Id == creditorId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    // ── Firm clients ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CreateClientAsync(FirmClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return _db.InsertOnceAsync(
            FirmRowMapping.ToRow(client),
            FirmClientRow.PrimaryKey,
            () => new InvalidOperationException($"client {client.Id} already exists"),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FirmClient?> GetClientAsync(string firmId, string clientId, CancellationToken cancellationToken = default) =>
        ReadClientAsync(firmId, clientId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FirmClient>> ListClientsAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var rows = await Clients.Where(client => client.FirmId == firmId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return FirmClientRules.Sorted(rows.Select(FirmRowMapping.ToDomain));
    }

    /// <inheritdoc />
    public Task<FirmClient?> UpdateClientAsync(FirmClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var row = FirmRowMapping.ToRow(client);

        // Python: SET <every attribute but the server-owned ones> …
        // attribute_exists(SK) AND firmId = :firm AND attribute_not_exists(#merged).
        // merged_into, merging_into, merging_from and prospect_stage are not
        // in the SET list, so they stay exactly as stored.
        return UpdateThenReadClientAsync(
            row.FirmId,
            row.Id,
            rows => rows
                .Where(stored => stored.MergedInto == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(stored => stored.Status, row.Status)
                        .SetProperty(stored => stored.CreatedAt, row.CreatedAt)
                        .SetProperty(stored => stored.UpdatedAt, row.UpdatedAt)
                        .SetProperty(stored => stored.CreatedBy, row.CreatedBy)
                        .SetProperty(stored => stored.Body, row.Body)
                        .SetProperty(stored => stored.FirstRetainedAt, row.FirstRetainedAt)
                        .SetProperty(stored => stored.TaxIdRef, row.TaxIdRef)
                        .SetProperty(stored => stored.TaxIdLastFour, row.TaxIdLastFour),
                    cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FirmClient?> SetClientProspectStageAsync(string firmId, string clientId, ProspectStage? stage, CancellationToken cancellationToken = default)
    {
        var now = Clock.Now(_clock);

        // Python: … AND attribute_not_exists(#merged) AND attribute_not_exists(#body.#retained).
        return UpdateThenReadClientAsync(
            firmId,
            clientId,
            rows => rows
                .Where(stored => stored.MergedInto == null && stored.FirstRetainedAt == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(stored => stored.ProspectStage, stage)
                        .SetProperty(stored => stored.UpdatedAt, now),
                    cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FirmClient?> MarkClientRetainedAsync(string firmId, string clientId, DateOnly retainedOn, CancellationToken cancellationToken = default)
    {
        var now = Clock.Now(_clock);

        // Python: SET #body.#retained = if_not_exists(#body.#retained, :date),
        // #updated = :now REMOVE #stage … attribute_not_exists(#merged).
        return UpdateThenReadClientAsync(
            firmId,
            clientId,
            rows => rows
                .Where(stored => stored.MergedInto == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(stored => stored.FirstRetainedAt, stored => stored.FirstRetainedAt ?? retainedOn)
                        .SetProperty(stored => stored.ProspectStage, (ProspectStage?)null)
                        .SetProperty(stored => stored.UpdatedAt, now),
                    cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ClaimClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default)
    {
        if (mergedId == survivorId)
        {
            return false;
        }

        try
        {
            await _db.InTransactionAsync(
                async token =>
                {
                    await LockBothClientsAsync(firmId, mergedId, survivorId, token).ConfigureAwait(false);

                    // The merged row: active, never merged, not receiving a
                    // merge, and not claimed by another survivor.
                    await ClientRow(firmId, mergedId)
                        .Where(stored => stored.Status == FirmClientStatus.Active
                            && stored.MergedInto == null
                            && stored.MergingFrom == null
                            && (stored.MergingInto == null || stored.MergingInto == survivorId))
                        .ExecuteConditionalUpdateAsync(set => set.SetProperty(stored => stored.MergingInto, survivorId), NotMet, token)
                        .ConfigureAwait(false);

                    // The survivor: active, never merged, not being merged
                    // away, and not receiving another client's merge.
                    await ClientRow(firmId, survivorId)
                        .Where(stored => stored.Status == FirmClientStatus.Active
                            && stored.MergedInto == null
                            && stored.MergingInto == null
                            && (stored.MergingFrom == null || stored.MergingFrom == mergedId))
                        .ExecuteConditionalUpdateAsync(set => set.SetProperty(stored => stored.MergingFrom, mergedId), NotMet, token)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConditionNotMetException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<FirmClient?> FinishClientMergeAsync(string firmId, string mergedId, string survivorId, DateTimeOffset mergedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.InTransactionAsync(
                async token =>
                {
                    await LockBothClientsAsync(firmId, mergedId, survivorId, token).ConfigureAwait(false);

                    // Python: SET status = archived, mergedInto = :survivor,
                    // updatedAt = :now REMOVE #into … #into = :survivor.
                    await ClientRow(firmId, mergedId)
                        .Where(stored => stored.MergingInto == survivorId)
                        .ExecuteConditionalUpdateAsync(
                            set => set
                                .SetProperty(stored => stored.Status, FirmClientStatus.Archived)
                                .SetProperty(stored => stored.MergedInto, survivorId)
                                .SetProperty(stored => stored.MergingInto, (string?)null)
                                .SetProperty(stored => stored.UpdatedAt, mergedAt),
                            NotMet,
                            token)
                        .ConfigureAwait(false);

                    // Python: REMOVE #from … #from = :merged.
                    await ClientRow(firmId, survivorId)
                        .Where(stored => stored.MergingFrom == mergedId)
                        .ExecuteConditionalUpdateAsync(set => set.SetProperty(stored => stored.MergingFrom, (string?)null), NotMet, token)
                        .ConfigureAwait(false);

                    return await ReadClientAsync(firmId, mergedId, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionNotMetException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task ReleaseClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default) =>
        _db.InTransactionAsync(
            async token =>
            {
                // Each half only while it is still THIS pair's (Python:
                // REMOVE #claim … #claim = :other, a refusal ignored).
                await ClientRow(firmId, mergedId)
                    .Where(stored => stored.MergingInto == survivorId)
                    .ExecuteUpdateAsync(set => set.SetProperty(stored => stored.MergingInto, (string?)null), token)
                    .ConfigureAwait(false);
                await ClientRow(firmId, survivorId)
                    .Where(stored => stored.MergingFrom == mergedId)
                    .ExecuteUpdateAsync(set => set.SetProperty(stored => stored.MergingFrom, (string?)null), token)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    // ── The questionnaire config and the document checklist ────────

    /// <inheritdoc />
    public async Task<QuestionnaireConfig?> GetQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<FirmQuestionnaireRow>().AsNoTracking()
            .SingleOrDefaultAsync(config => config.FirmId == firmId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task PutQuestionnaireAsync(QuestionnaireConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var sections = StoredJson.ToDocument(config.Sections.ToDictionary(StringComparer.Ordinal));

        // A put with no condition: last writer wins.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO insolvia.firm_questionnaires (firm_id, sections, updated_at, updated_by)
            VALUES ({config.FirmId}, {sections.RootElement.GetRawText()}::jsonb, {Inserts.Utc(config.UpdatedAt)}, {config.UpdatedBy})
            ON CONFLICT (firm_id) DO UPDATE
            SET sections = excluded.sections, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default) =>
        await _db.Set<FirmQuestionnaireRow>()
            .Where(config => config.FirmId == firmId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public async Task<DocumentChecklist?> GetDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<FirmDocumentChecklistRow>().AsNoTracking()
            .SingleOrDefaultAsync(checklist => checklist.FirmId == firmId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    /// <inheritdoc />
    public async Task PutDocumentChecklistAsync(DocumentChecklist checklist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        using var items = StoredJson.ToDocument(checklist.Items);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO insolvia.firm_document_checklists (firm_id, items, updated_at, updated_by)
            VALUES ({checklist.FirmId}, {items.RootElement.GetRawText()}::jsonb, {Inserts.Utc(checklist.UpdatedAt)}, {checklist.UpdatedBy})
            ON CONFLICT (firm_id) DO UPDATE
            SET items = excluded.items, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default) =>
        await _db.Set<FirmDocumentChecklistRow>()
            .Where(checklist => checklist.FirmId == firmId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    // ── Helpers ─────────────────────────────────────────────────────

    private static ConditionNotMetException NotMet() => new();

    private IQueryable<FirmClientRow> ClientRow(string firmId, string clientId) =>
        _db.Set<FirmClientRow>().Where(stored => stored.FirmId == firmId && stored.Id == clientId);

    private async Task<FirmClient?> ReadClientAsync(string firmId, string clientId, CancellationToken cancellationToken)
    {
        var row = await Clients
            .SingleOrDefaultAsync(client => client.FirmId == firmId && client.Id == clientId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FirmRowMapping.ToDomain(row);
    }

    // DynamoDB's UpdateItem … ReturnValues=ALL_NEW: the conditional UPDATE
    // and the read of what it wrote, in one transaction — the UPDATE's row
    // lock is held until commit, so the read sees exactly this write.
    private Task<FirmClient?> UpdateThenReadClientAsync(
        string firmId,
        string clientId,
        Func<IQueryable<FirmClientRow>, Task<int>> update,
        CancellationToken cancellationToken) =>
        _db.InTransactionAsync(
            async token => await update(ClientRow(firmId, clientId)).ConfigureAwait(false) == 0
                ? null
                : await ReadClientAsync(firmId, clientId, token).ConfigureAwait(false),
            cancellationToken);

    // Both rows of a merge, locked in id order before either is written:
    // claim(A into B) and claim(B into A) then queue on the same first lock
    // instead of each holding one row and deadlocking on the other. Both
    // must exist in the firm — checked here, under the lock, because the
    // first pointer written would otherwise trip the foreign key onto a
    // missing second row instead of answering "refused".
    private async Task LockBothClientsAsync(string firmId, string first, string second, CancellationToken cancellationToken)
    {
        var locked = await _db.Database
            .SqlQuery<string>($"SELECT id AS \"Value\" FROM insolvia.firm_clients WHERE firm_id = {firmId} AND id IN ({first}, {second}) ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (locked.Count < 2)
        {
            throw NotMet();
        }
    }
}
