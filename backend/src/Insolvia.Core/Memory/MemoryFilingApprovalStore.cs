using Insolvia.Core.Filings;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IFilingApprovalStore"/>, with the database's
/// conditions — a test that could consume twice, or create past a moved
/// pointer, would be testing a weaker store than production runs.
/// </summary>
public sealed class MemoryFilingApprovalStore : IFilingApprovalStore
{
    private const string Raced = "Another approval was made or used first — review the filing set again.";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string ApprovalId), FilingApproval> _approvals = [];
    private readonly Dictionary<string, string> _pointers = new(StringComparer.Ordinal);

    /// <summary>A snapshot of every approval — a test affordance.</summary>
    public IReadOnlyList<FilingApproval> Approvals
    {
        get
        {
            lock (_gate)
            {
                return [.. _approvals.Values];
            }
        }
    }

    /// <summary>Overwrite a stored approval in place — a test affordance (an expiry moved, say).</summary>
    public void Replace(FilingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_gate)
        {
            _approvals[(approval.CaseId, approval.ApprovalId)] = approval;
        }
    }

    /// <inheritdoc />
    public Task<FilingApproval?> CurrentAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_pointers.TryGetValue(caseId, out var id) ? _approvals.GetValueOrDefault((caseId, id)) : null);
        }
    }

    /// <inheritdoc />
    public Task<FilingApproval?> GetAsync(string caseId, string approvalId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_approvals.GetValueOrDefault((caseId, approvalId)));
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(FilingApproval approval, FilingApproval? replacing, DateTimeOffset voidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_gate)
        {
            var key = (approval.CaseId, approval.ApprovalId);
            if (_approvals.ContainsKey(key))
            {
                throw new InvalidOperationException("approval id already exists in this case");
            }

            if (_pointers.GetValueOrDefault(approval.CaseId) != replacing?.ApprovalId)
            {
                throw new ConflictException(Raced);
            }

            // Every condition first, then every write: the transaction's all-or-nothing.
            if (replacing is { Status: ApprovalStatus.Pending })
            {
                var oldKey = (approval.CaseId, replacing.ApprovalId);
                if (!_approvals.TryGetValue(oldKey, out var stored) || stored.Status != ApprovalStatus.Pending)
                {
                    throw new ConflictException(Raced);
                }

                _approvals[oldKey] = FilingApprovalRecords.Voided(stored, VoidReasons.Superseded, voidedAt);
            }

            _approvals[key] = approval;
            _pointers[approval.CaseId] = approval.ApprovalId;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ConsumeAsync(string caseId, string approvalId, string digest, long nowSeconds, DateTimeOffset consumedAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_approvals.TryGetValue((caseId, approvalId), out var stored)
                || stored.Status != ApprovalStatus.Pending
                || stored.Digest != digest
                || stored.ExpiresAt <= nowSeconds)
            {
                return Task.FromResult(false);
            }

            _approvals[(caseId, approvalId)] = FilingApprovalRecords.Consumed(stored, consumedAt);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> VoidAsync(string caseId, string approvalId, string reason, DateTimeOffset voidedAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_approvals.TryGetValue((caseId, approvalId), out var stored) || stored.Status != ApprovalStatus.Pending)
            {
                return Task.FromResult(false);
            }

            _approvals[(caseId, approvalId)] = FilingApprovalRecords.Voided(stored, reason, voidedAt);
            return Task.FromResult(true);
        }
    }
}
