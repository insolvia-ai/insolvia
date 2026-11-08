using Insolvia.Core.CaseTasks;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ICaseTaskStore"/>. Keyed by (case, id) as the table is, so
/// "an id only resolves inside its own case" is a property of the map.
/// </summary>
public sealed class MemoryCaseTaskStore : ICaseTaskStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, string Id), CaseTask> _rows = [];

    /// <inheritdoc />
    public Task CreateAsync(CaseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            // An existing (case, id) means the id minting is broken; replacing would hide it.
            if (!_rows.TryAdd((task.CaseId, task.Id), task))
            {
                throw new InvalidOperationException($"task {(task.CaseId, task.Id)} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CaseTask?> GetAsync(string caseId, string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault((caseId, taskId)));
        }
    }

    /// <inheritdoc />
    public Task<CaseTask?> UpdateAsync(CaseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (!_rows.ContainsKey((task.CaseId, task.Id)))
            {
                return Task.FromResult<CaseTask?>(null);
            }

            _rows[(task.CaseId, task.Id)] = task;
            return Task.FromResult<CaseTask?>(task);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CaseTask>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CaseTask> listed = [.. _rows.Where(pair => pair.Key.CaseId == caseId).Select(pair => pair.Value).Order(CaseTaskRules.ListOrder)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CaseTask>> ListForCasesAsync(IReadOnlyCollection<string> caseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        var wanted = caseIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            IReadOnlyList<CaseTask> listed = [.. _rows.Where(pair => wanted.Contains(pair.Key.CaseId)).Select(pair => pair.Value)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string caseId, string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Remove((caseId, taskId)));
        }
    }
}
