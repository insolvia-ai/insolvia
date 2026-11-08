using Insolvia.Core.Filings;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists the attorney's per-filing approvals (Python's
/// <c>FilingApprovalStore</c>; ADR 0024, guardrail 1) beside ONE pointer per
/// case naming its current approval. Every write is conditional; the
/// conditions ARE the guardrail: one live approval per case, used once,
/// never after expiry or a change.
/// </summary>
public interface IFilingApprovalStore
{
    /// <summary>The approval the case's pointer names, strongly consistent, or null.</summary>
    Task<FilingApproval?> CurrentAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>One approval by id, strongly consistent — the consume reads this.</summary>
    Task<FilingApproval?> GetAsync(string caseId, string approvalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store <paramref name="approval"/> and point the case at it, atomically.
    /// <paramref name="replacing"/> is the current approval the caller read
    /// (null: there was none). The write is conditional on the pointer still
    /// naming it and, when it is pending, voids it (<c>superseded</c>,
    /// <paramref name="voidedAt"/>) in the same transaction — conditional on
    /// it still being pending. A race lost throws <see cref="ConflictException"/>
    /// and writes nothing.
    /// </summary>
    Task CreateAsync(FilingApproval approval, FilingApproval? replacing, DateTimeOffset voidedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>pending</c> → <c>consumed</c>, conditional on status pending, the
    /// stored digest equalling <paramref name="digest"/>, and
    /// <c>expires_at &gt; <paramref name="nowSeconds"/></c>. True when THIS call
    /// made the write — of two callers, exactly one gets true.
    /// </summary>
    Task<bool> ConsumeAsync(string caseId, string approvalId, string digest, long nowSeconds, DateTimeOffset consumedAt, CancellationToken cancellationToken = default);

    /// <summary><c>pending</c> → <c>voided</c> with <paramref name="reason"/>, conditional on pending. True when this call made the write.</summary>
    Task<bool> VoidAsync(string caseId, string approvalId, string reason, DateTimeOffset voidedAt, CancellationToken cancellationToken = default);
}
