using Insolvia.Core;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;
using Insolvia.Data.Concurrency;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Cases;

/// <summary>
/// The Postgres <see cref="IFilingApprovalStore"/> (Python's
/// <c>DynamoDbFilingApprovalStore</c>), held to
/// <see cref="Core.Memory.MemoryFilingApprovalStore"/> by the conformance
/// suite. The conditions are the guardrail, each in a statement's WHERE:
/// <list type="bullet">
/// <item><c>create</c> — ONE transaction: the approval's INSERT (its key
/// refuses a reused id), the pointer's INSERT when the caller read none (its
/// key refuses a second) or <c>UPDATE … WHERE approval_id = :read</c> when it
/// replaces one, and — when the replaced one is pending — its
/// <c>UPDATE … WHERE status = 'pending'</c> to <c>superseded</c>. Any miss
/// rolls the unit back: <see cref="ConflictException"/>.</item>
/// <item><c>consume</c> — <c>UPDATE … WHERE status = 'pending' AND digest = :d
/// AND expires_at &gt; :now</c>: of two consumers exactly one row is affected.</item>
/// <item><c>void</c> — <c>UPDATE … WHERE status = 'pending'</c>.</item>
/// </list>
/// </summary>
public sealed class PostgresFilingApprovalStore(InsolviaDbContext db) : IFilingApprovalStore
{
    private const string ApprovalKey = "ak_filing_approval_row_case_id_approval_id";
    private const string PointerKey = "pk_current_filing_approvals";
    private const string Pending = "pending";
    private const string Raced = "Another approval was made or used first — review the filing set again.";

    private readonly InsolviaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task<FilingApproval?> CurrentAsync(string caseId, CancellationToken cancellationToken = default)
    {
        var pointer = await _db.Set<CurrentFilingApprovalRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.CaseId == caseId, cancellationToken).ConfigureAwait(false);
        return pointer is null ? null : await GetAsync(caseId, pointer.ApprovalId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FilingApproval?> GetAsync(string caseId, string approvalId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<FilingApprovalRow>().AsNoTracking()
            .SingleOrDefaultAsync(found => found.CaseId == caseId && found.ApprovalId == approvalId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task CreateAsync(FilingApproval approval, FilingApproval? replacing, DateTimeOffset voidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        try
        {
            await _db.InTransactionAsync(
                async token =>
                {
                    await _db.InsertOnceAsync(ToRow(approval), ApprovalKey, () => new ConditionNotMetException(), token).ConfigureAwait(false);
                    if (replacing is null)
                    {
                        await _db.InsertOnceAsync(
                            new CurrentFilingApprovalRow { CaseId = approval.CaseId, FirmId = approval.FirmId, ApprovalId = approval.ApprovalId },
                            PointerKey,
                            () => new ConditionNotMetException(),
                            token).ConfigureAwait(false);
                    }
                    else
                    {
                        var read = replacing.ApprovalId;
                        await _db.Set<CurrentFilingApprovalRow>()
                            .Where(row => row.CaseId == approval.CaseId && row.ApprovalId == read)
                            .ExecuteConditionalUpdateAsync(set => set.SetProperty(row => row.ApprovalId, approval.ApprovalId), () => new ConditionNotMetException(), token)
                            .ConfigureAwait(false);
                        if (replacing.Status == ApprovalStatus.Pending)
                        {
                            var at = Inserts.Utc(voidedAt);
                            await _db.Set<FilingApprovalRow>()
                                .Where(row => row.CaseId == approval.CaseId && row.ApprovalId == read && row.Status == Pending)
                                .ExecuteConditionalUpdateAsync(
                                    set => set
                                        .SetProperty(row => row.Status, "voided")
                                        .SetProperty(row => row.VoidedAt, at)
                                        .SetProperty(row => row.VoidReason, VoidReasons.Superseded),
                                    () => new ConditionNotMetException(),
                                    token)
                                .ConfigureAwait(false);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionNotMetException error)
        {
            throw new ConflictException(Raced, error);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConsumeAsync(string caseId, string approvalId, string digest, long nowSeconds, DateTimeOffset consumedAt, CancellationToken cancellationToken = default)
    {
        var at = Inserts.Utc(consumedAt);
        var rows = await _db.Set<FilingApprovalRow>()
            .Where(row => row.CaseId == caseId && row.ApprovalId == approvalId && row.Status == Pending && row.Digest == digest && row.ExpiresAt > nowSeconds)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "consumed").SetProperty(row => row.ConsumedAt, at), cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> VoidAsync(string caseId, string approvalId, string reason, DateTimeOffset voidedAt, CancellationToken cancellationToken = default)
    {
        var at = Inserts.Utc(voidedAt);
        var rows = await _db.Set<FilingApprovalRow>()
            .Where(row => row.CaseId == caseId && row.ApprovalId == approvalId && row.Status == Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "voided").SetProperty(row => row.VoidedAt, at).SetProperty(row => row.VoidReason, reason), cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    private static FilingApprovalRow ToRow(FilingApproval approval) => new()
    {
        CaseId = approval.CaseId,
        ApprovalId = approval.ApprovalId,
        FirmId = approval.FirmId,
        FilingId = approval.FilingId,
        AttorneyId = approval.AttorneyId,
        CredentialId = approval.CredentialId,
        AuthorizationId = approval.AuthorizationId,
        Digest = approval.Digest,
        PacketId = approval.PacketId,
        Court = approval.Court,
        Division = approval.Division,
        RegistryRelease = approval.RegistryRelease,
        ApprovedAt = Inserts.Utc(approval.ApprovedAt),
        AuthenticatedAt = DateTimeOffset.FromUnixTimeSeconds(approval.AuthenticatedAt),
        ExpiresAt = approval.ExpiresAt,
        Status = approval.Status.ToWire(),
        ConsumedAt = approval.ConsumedAt is { } consumed ? Inserts.Utc(consumed) : null,
        VoidedAt = approval.VoidedAt is { } voided ? Inserts.Utc(voided) : null,
        VoidReason = approval.VoidReason,
    };

    private static FilingApproval ToDomain(FilingApprovalRow row) => new(
        row.ApprovalId,
        row.FilingId,
        row.CaseId,
        row.FirmId,
        row.AttorneyId,
        row.CredentialId,
        row.AuthorizationId,
        row.Digest,
        row.PacketId,
        row.Court,
        row.Division,
        row.RegistryRelease,
        row.ApprovedAt,
        row.AuthenticatedAt.ToUnixTimeSeconds(),
        row.ExpiresAt)
    {
        Status = row.Status switch
        {
            "pending" => ApprovalStatus.Pending,
            "consumed" => ApprovalStatus.Consumed,
            "voided" => ApprovalStatus.Voided,
            _ => throw new ValidationException($"stored filing approval has status '{row.Status}'"),
        },
        ConsumedAt = row.ConsumedAt,
        VoidedAt = row.VoidedAt,
        VoidReason = row.VoidReason,
    };
}
