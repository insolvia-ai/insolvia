using Insolvia.Core.Documents;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IDocumentStore"/>. Keyed by (case, id) as the table is, so
/// "an id only resolves inside its own case" is a property of the map — a store keyed by document id alone would hand one firm's document to another the moment an id leaked.
/// </summary>
public sealed class MemoryDocumentStore : IDocumentStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), Document> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            // An existing (case, id) means the id minting is broken; replacing would hide it.
            if (!_rows.TryAdd((document.CaseId, document.Id), document))
            {
                throw new InvalidOperationException($"document {(document.CaseId, document.Id)} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Document?> GetAsync(string caseId, string documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, documentId)));
        }
    }

    /// <inheritdoc />
    public Task<Document?> UpdateAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (!_rows.ContainsKey((document.CaseId, document.Id)))
            {
                return Task.FromResult<Document?>(null);
            }

            _rows[(document.CaseId, document.Id)] = document;
            return Task.FromResult<Document?>(document);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Document>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Document> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(DocumentRules.ListOrder)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string caseId, string documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Remove((caseId, documentId)));
        }
    }
}
