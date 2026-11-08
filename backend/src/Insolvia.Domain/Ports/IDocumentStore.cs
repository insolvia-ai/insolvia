using Insolvia.Core.Documents;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists document METADATA rows — never the bytes (issue 8.6; Python's
/// <c>DocumentStore</c>). Case-scoped, not owner-scoped: the route resolves the
/// case through <see cref="ICaseStore"/> first, and the case is half the key,
/// so a leaked document id is useless without its case.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Store a new record; MUST refuse to overwrite an existing (case, document).</summary>
    Task CreateAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>One record, or null.</summary>
    Task<Document?> GetAsync(string caseId, string documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write <paramref name="document"/> back, only over a row that still
    /// exists — null otherwise, so a deleted document is not resurrected. The
    /// one caller is confirmation.
    /// </summary>
    Task<Document?> UpdateAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>Every document of one case, newest first (<see cref="DocumentRules.ListOrder"/>). All of them.</summary>
    Task<IReadOnlyList<Document>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>Remove the record. True if this call removed it — two racing deletes cannot both succeed.</summary>
    Task<bool> DeleteAsync(string caseId, string documentId, CancellationToken cancellationToken = default);
}
