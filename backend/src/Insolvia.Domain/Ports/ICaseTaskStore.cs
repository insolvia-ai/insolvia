using Insolvia.Core.CaseTasks;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists case tasks (issue #356; Python's <c>TaskStore</c>). Case-scoped.
/// NO CROSS-CASE QUERY: "assigned to me" walks the caller's reachable cases
/// rather than growing an index that would have to re-derive the access rule.
/// </summary>
public interface ICaseTaskStore
{
    /// <summary>Store a new record; MUST throw rather than replace an existing (case, id).</summary>
    Task CreateAsync(CaseTask task, CancellationToken cancellationToken = default);

    /// <summary>One task, or null.</summary>
    Task<CaseTask?> GetAsync(string caseId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Write <paramref name="task"/> back over a row that still exists, or null.</summary>
    Task<CaseTask?> UpdateAsync(CaseTask task, CancellationToken cancellationToken = default);

    /// <summary>Every task of one case, in creation order. All of them.</summary>
    Task<IReadOnlyList<CaseTask>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every task of every case in <paramref name="caseIds"/>, in one read —
    /// a cross-case listing (GET /v1/tasks), not one round trip per case. No
    /// order promised; the caller orders. The caller resolved access first.
    /// </summary>
    Task<IReadOnlyList<CaseTask>> ListForCasesAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default);

    /// <summary>Remove the record. True if this call removed it.</summary>
    Task<bool> DeleteAsync(string caseId, string taskId, CancellationToken cancellationToken = default);
}
