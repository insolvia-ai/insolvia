using Insolvia.Core;
using Insolvia.Core.Clients;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Portal;

/// <summary>
/// The Postgres <see cref="IClientBindingStore"/> (Python's
/// <c>DynamoDbClientBindingStore</c>), held to
/// <see cref="Core.Memory.MemoryClientBindingStore"/> by the conformance
/// suite. The cross-table <c>TransactWriteItems</c> is one transaction over
/// three tables; each of its conditions is the <c>WHERE</c> of an upsert or an
/// update, and zero rows affected is the <see cref="ConflictException"/> the
/// cancelled transaction was — everything already written rolls back.
/// </summary>
public sealed class PostgresClientBindingStore : IClientBindingStore
{
    private const string Revoked = "revoked";
    private const string Invited = "invited";
    private const string Active = "active";

    private readonly InsolviaDbContext _db;

    /// <summary>A store over <paramref name="db"/> (request-scoped).</summary>
    public PostgresClientBindingStore(InsolviaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public Task BindAsync(ClientBinding binding, IReadOnlyList<ClientBinding> narrowed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(narrowed);
        string[] holders = [binding.Subject, .. narrowed.Select(other => other.Subject)];

        return _db.InTransactionAsync(
            async token =>
            {
                // One live binding per subject per firm: a revoked row may be
                // re-bound (a refiled case), a live one may not.
                // (Python: attribute_not_exists(SK) OR #status = :revoked.)
                await ConditionalAsync(
                    UpsertBindingUnlessLive(binding),
                    "that person already has portal access to a case; revoke it before inviting them again",
                    token).ConfigureAwait(false);
                await ConditionalAsync(
                    UpsertMirror(binding, onlyOverRevoked: true),
                    "that person already has portal access to this case; revoke it before inviting them again",
                    token).ConfigureAwait(false);

                // The per-role rule, made structural: a claim is taken only if
                // nobody holds it, or its holder is the new subject or one this
                // same transaction narrows.
                // (Python: attribute_not_exists(SK) OR #subject IN (:h0, …).)
                foreach (var role in binding.Roles)
                {
                    await ConditionalAsync(
                        $"""
                        INSERT INTO insolvia.case_client_role_claims AS claim (case_id, role, firm_id, subject)
                        VALUES ({binding.CaseId}, {role.ToWire()}, {binding.FirmId}, {binding.Subject})
                        ON CONFLICT (case_id, role) DO UPDATE
                        SET firm_id = excluded.firm_id, subject = excluded.subject
                        WHERE claim.subject = ANY({holders})
                        """,
                        "another client already answers for that debtor",
                        token).ConfigureAwait(false);
                }

                // Each narrowed binding still live, or the narrowing is stale.
                // (Python: #status IN (:invited, :active).)
                foreach (var other in narrowed)
                {
                    await ConditionalAsync(
                        $"""
                        UPDATE insolvia.client_bindings
                        SET case_id = {other.CaseId}, email = {other.Email}, display_name = {other.DisplayName},
                            roles = {RolesOf(other)}, status = {other.Status.ToWire()}, invited_by = {other.InvitedBy},
                            created_at = {Inserts.Utc(other.CreatedAt)}, updated_at = {Inserts.Utc(other.UpdatedAt)}
                        WHERE firm_id = {other.FirmId} AND subject = {other.Subject} AND status IN ({Invited}, {Active})
                        """,
                        "a binding changed while this one was written",
                        token).ConfigureAwait(false);
                    await _db.Database.ExecuteSqlInterpolatedAsync(UpsertMirror(other, onlyOverRevoked: false), token).ConfigureAwait(false);
                }
            },
            cancellationToken,
            // A lost commit acknowledgement: the replay finds the binding live
            // — this bind's — and would refuse it as "already has portal
            // access". The binding row carrying this bind's case, status and
            // timestamp is this commit (the unit writes it first).
            landed: token => BindingLandedAsync(binding, token));
    }

    private Task<bool> BindingLandedAsync(ClientBinding binding, CancellationToken cancellationToken)
    {
        var status = binding.Status;
        var updatedAt = Inserts.Utc(binding.UpdatedAt);
        return _db.Set<ClientBindingRow>().AsNoTracking().AnyAsync(
            row => row.FirmId == binding.FirmId && row.Subject == binding.Subject
                && row.CaseId == binding.CaseId && row.Status == status && row.UpdatedAt == updatedAt,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ClientBinding?> UpdateAsync(ClientBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return _db.InTransactionAsync<ClientBinding?>(
            async token =>
            {
                // Python: Put … attribute_exists(SK) — the first item, whose
                // failure alone is "no such binding" (null), not a conflict.
                var updated = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE insolvia.client_bindings
                    SET case_id = {binding.CaseId}, email = {binding.Email}, display_name = {binding.DisplayName},
                        roles = {RolesOf(binding)}, status = {binding.Status.ToWire()}, invited_by = {binding.InvitedBy},
                        created_at = {Inserts.Utc(binding.CreatedAt)}, updated_at = {Inserts.Utc(binding.UpdatedAt)}
                    WHERE firm_id = {binding.FirmId} AND subject = {binding.Subject}
                    """,
                    token).ConfigureAwait(false);
                if (updated == 0)
                {
                    return null;
                }

                await _db.Database.ExecuteSqlInterpolatedAsync(UpsertMirror(binding, onlyOverRevoked: false), token).ConfigureAwait(false);
                if (binding.Status == BindingStatus.Revoked)
                {
                    // Python: Delete … attribute_not_exists(SK) OR #subject = :subject,
                    // per role. A claim another subject holds cancels the whole
                    // transaction — checked under a row lock, then released.
                    var heldByOthers = await _db.Database.SqlQuery<int>(
                        $"""
                        SELECT 1 AS "Value" FROM insolvia.case_client_role_claims
                        WHERE case_id = {binding.CaseId} AND role = ANY({RolesOf(binding)}) AND subject <> {binding.Subject}
                        FOR UPDATE
                        """).ToListAsync(token).ConfigureAwait(false);
                    if (heldByOthers.Count > 0)
                    {
                        throw new ConflictException(ClientBindings.AccessChanged);
                    }

                    await _db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        DELETE FROM insolvia.case_client_role_claims
                        WHERE case_id = {binding.CaseId} AND role = ANY({RolesOf(binding)}) AND subject = {binding.Subject}
                        """,
                        token).ConfigureAwait(false);
                }

                return binding;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ClientBinding?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<ClientBindingRow>().AsNoTracking()
            .SingleOrDefaultAsync(binding => binding.FirmId == firmId && binding.Subject == subject, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<ClientBinding?> FindAsync(string subject, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<ClientBindingRow>().AsNoTracking()
            .Where(binding => binding.Subject == subject)
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count > 1)
        {
            throw new InvalidOperationException("a client resolves to more than one firm; refusing to guess");
        }

        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    /// <inheritdoc />
    public async Task<ClientBinding?> FindByEmailAsync(string firmId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var lowered = TextRules.Lower(email);
        var row = await _db.Set<ClientBindingRow>().AsNoTracking()
            .Where(binding => binding.FirmId == firmId && binding.Email == lowered)
            .OrderBy(binding => binding.Subject)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientBinding>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<CaseClientBindingRow>().AsNoTracking()
            .Where(binding => binding.CaseId == caseId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .Select(ToDomain)
            .OrderBy(binding => binding.CreatedAt)
            .ThenBy(binding => binding.Subject, StringComparer.Ordinal)];
    }

    private static string[] RolesOf(ClientBinding binding) => [.. binding.Roles.Select(role => role.ToWire())];

    private static ClientBinding ToDomain(ClientBindingRow row) => new(
        row.FirmId,
        row.CaseId,
        row.Subject,
        row.Email,
        row.DisplayName,
        RolesFrom(row.Roles),
        row.Status,
        row.InvitedBy,
        row.CreatedAt,
        row.UpdatedAt);

    private static ClientBinding ToDomain(CaseClientBindingRow row) => new(
        row.FirmId,
        row.CaseId,
        row.Subject,
        row.Email,
        row.DisplayName,
        RolesFrom(row.Roles),
        row.Status,
        row.InvitedBy,
        row.CreatedAt,
        row.UpdatedAt);

    private static ValueCollection<ClientRole> RolesFrom(string[] stored) =>
        stored.Select(WireEnums.Parse<ClientRole>).ToValueCollection();

    private static FormattableString UpsertBindingUnlessLive(ClientBinding binding) =>
        $"""
        INSERT INTO insolvia.client_bindings AS binding
            (firm_id, subject, case_id, email, display_name, roles, status, invited_by, created_at, updated_at)
        VALUES ({binding.FirmId}, {binding.Subject}, {binding.CaseId}, {binding.Email}, {binding.DisplayName},
            {RolesOf(binding)}, {binding.Status.ToWire()}, {binding.InvitedBy}, {Inserts.Utc(binding.CreatedAt)}, {Inserts.Utc(binding.UpdatedAt)})
        ON CONFLICT (firm_id, subject) DO UPDATE
        SET case_id = excluded.case_id, email = excluded.email, display_name = excluded.display_name, roles = excluded.roles,
            status = excluded.status, invited_by = excluded.invited_by, created_at = excluded.created_at, updated_at = excluded.updated_at
        WHERE binding.status = {Revoked}
        """;

    // The mirror's upsert. With onlyOverRevoked, a live mirror refuses it
    // (the bind's not-live condition on the case side); without, it is the
    // unconditional Put the update and the narrowing write.
    private static FormattableString UpsertMirror(ClientBinding binding, bool onlyOverRevoked) =>
        $"""
        INSERT INTO insolvia.case_client_bindings AS mirror
            (case_id, subject, firm_id, email, display_name, roles, status, invited_by, created_at, updated_at)
        VALUES ({binding.CaseId}, {binding.Subject}, {binding.FirmId}, {binding.Email}, {binding.DisplayName},
            {RolesOf(binding)}, {binding.Status.ToWire()}, {binding.InvitedBy}, {Inserts.Utc(binding.CreatedAt)}, {Inserts.Utc(binding.UpdatedAt)})
        ON CONFLICT (case_id, subject) DO UPDATE
        SET firm_id = excluded.firm_id, email = excluded.email, display_name = excluded.display_name, roles = excluded.roles,
            status = excluded.status, invited_by = excluded.invited_by, created_at = excluded.created_at, updated_at = excluded.updated_at
        WHERE {!onlyOverRevoked} OR mirror.status = {Revoked}
        """;

    private Task<int> ConditionalAsync(FormattableString sql, string refusal, CancellationToken cancellationToken) =>
        _db.Database.ExecuteConditionalSqlAsync(sql, () => new ConflictException(refusal), cancellationToken);
}
