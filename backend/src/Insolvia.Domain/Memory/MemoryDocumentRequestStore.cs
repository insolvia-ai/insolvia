using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IDocumentRequestStore"/>. Keyed by (case, id) as the table is, so
/// "an id only resolves inside its own case" is a property of the map.
/// </summary>
public sealed class MemoryDocumentRequestStore : IDocumentRequestStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), DocumentRequest> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            // An existing (case, id) means the id minting is broken; replacing would hide it.
            if (!_rows.TryAdd((request.CaseId, request.Id), request))
            {
                throw new InvalidOperationException($"request {(request.CaseId, request.Id)} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DocumentRequest?> GetAsync(string caseId, string requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, requestId)));
        }
    }

    /// <inheritdoc />
    public Task<DocumentRequest?> UpdateAsync(DocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_rows.ContainsKey((request.CaseId, request.Id)))
            {
                return Task.FromResult<DocumentRequest?>(null);
            }

            _rows[(request.CaseId, request.Id)] = request;
            return Task.FromResult<DocumentRequest?>(request);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentRequest>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DocumentRequest> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(DocumentRequestRules.ListOrder)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string caseId, string requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Remove((caseId, requestId)));
        }
    }
}
