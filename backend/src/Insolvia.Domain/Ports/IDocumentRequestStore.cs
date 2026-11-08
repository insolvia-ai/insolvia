using Insolvia.Core.DocumentRequests;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists a case's document requests (ADR 0023 PR 5; Python's
/// <c>DocumentRequestStore</c>). Case-scoped; no accessor.
/// </summary>
public interface IDocumentRequestStore
{
    /// <summary>Store a new record; MUST throw on an existing (case, id).</summary>
    Task CreateAsync(DocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>One request, or null.</summary>
    Task<DocumentRequest?> GetAsync(string caseId, string requestId, CancellationToken cancellationToken = default);

    /// <summary>Write <paramref name="request"/> back over a row that still exists, or null. Last writer wins.</summary>
    Task<DocumentRequest?> UpdateAsync(DocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every request of one case, in <see cref="DocumentRequestRules.ListOrder"/>. All of them.</summary>
    Task<IReadOnlyList<DocumentRequest>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>Remove the record. True if this call removed it.</summary>
    Task<bool> DeleteAsync(string caseId, string requestId, CancellationToken cancellationToken = default);
}
