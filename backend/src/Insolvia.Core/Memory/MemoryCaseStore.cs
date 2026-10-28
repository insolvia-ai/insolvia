using Insolvia.Core.Access;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="ICaseStore"/> (Python's <c>MemoryCaseStore</c>). It
/// applies <see cref="CaseAccess.MaySeeCase(Accessor, Case, bool)"/> and
/// orders results exactly as the real store must, and mirrors the awkward
/// part: which index a listing reads depends on the caller, and a cursor
/// minted against one is refused by the other.
/// </summary>
/// <remarks>
/// A case, its assignments and its debtors share a partition in the real
/// store; here that is one shared <see cref="MemoryDebtorStore"/> — pass the
/// one the debtor routes use, or the debtors this store writes are invisible
/// to them. Every condition of <see cref="CreateAsync"/> is checked before
/// anything is written, which is what makes it the transaction.
/// </remarks>
/// <param name="debtors">The debtor store the case's debtors live in; a private one when none is given.</param>
public sealed class MemoryCaseStore(MemoryDebtorStore? debtors = null) : ICaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Case> _cases = new(StringComparer.Ordinal);
    private readonly Dictionary<(string CaseId, string Subject), CaseAssignment> _assignments = [];
    private readonly Dictionary<string, List<StatusChange>> _history = new(StringComparer.Ordinal);

    /// <summary>The debtor store this case store writes into.</summary>
    public MemoryDebtorStore Debtors { get; } = debtors ?? new MemoryDebtorStore();

    /// <summary>A snapshot of every case — a test affordance (and the filing store's transaction partner).</summary>
    public IReadOnlyDictionary<string, Case> Cases
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, Case>(_cases, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Place <paramref name="stored"/> directly, no conditions — a test affordance (a row as another writer left it).</summary>
    internal void Put(Case stored)
    {
        lock (_gate)
        {
            _cases[stored.Id] = stored;
        }
    }

    /// <inheritdoc />
    public async Task CreateAsync(Case created, CaseAssignment assignment, IReadOnlyList<Debtor>? debtors = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(assignment);
        debtors ??= [];
        lock (_gate)
        {
            if (_cases.ContainsKey(created.Id))
            {
                throw new InvalidOperationException($"case {created.Id} already exists");
            }
        }

        // Refused BEFORE anything is written.
        foreach (var debtor in debtors)
        {
            if (debtor.CaseId != created.Id)
            {
                throw new InvalidOperationException("a debtor written with a case must belong to it");
            }

            if (await Debtors.GetAsync(created.Id, debtor.FilingRole, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException($"debtor {debtor.FilingRole.ToWire()} already exists");
            }
        }

        foreach (var debtor in debtors)
        {
            if (debtor.ClientId is not null && !await Debtors.ClientLinkableAsync(created.FirmId, debtor.ClientId, cancellationToken).ConfigureAwait(false))
            {
                throw new ClientUnavailableException(debtor.ClientId);
            }
        }

        lock (_gate)
        {
            _cases[created.Id] = created;
            _assignments[(assignment.CaseId, assignment.Subject)] = assignment;
        }

        foreach (var debtor in debtors)
        {
            await Debtors.PutAsync(debtor, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientCase>> ListForClientAsync(string clientId, Accessor accessor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        // The index, as a filter over the debtors: sparse, one entry per
        // debtor, newest case first.
        var entries = Debtors.Debtors
            .Where(debtor => debtor.ClientId == clientId && debtor.CaseCreatedAt is not null)
            .OrderByDescending(debtor => CaseRules.ListingSortKey(debtor.CaseCreatedAt!.Value, debtor.CaseId), StringComparer.Ordinal)
            .ToList();
        var found = new List<ClientCase>();
        foreach (var debtor in entries)
        {
            if (await GetAsync(debtor.CaseId, accessor, cancellationToken).ConfigureAwait(false) is { } visible)
            {
                found.Add(new ClientCase(visible, debtor.FilingRole));
            }
        }

        return found;
    }

    /// <inheritdoc />
    public Task<Case?> GetAsync(string caseId, Accessor accessor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        lock (_gate)
        {
            if (!_cases.TryGetValue(caseId, out var stored))
            {
                return Task.FromResult<Case?>(null);
            }

            var assigned = _assignments.ContainsKey((caseId, accessor.Subject));
            return Task.FromResult(CaseAccess.MaySeeCase(accessor, stored, assigned) ? stored : null);
        }
    }

    /// <inheritdoc />
    public Task<Case?> ReadForWorkerAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_cases.GetValueOrDefault(caseId));
        }
    }

    /// <inheritdoc />
    public Task<CasePublicStatus?> PublicStatusAsync(ClientAccessor client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            return Task.FromResult(_cases.TryGetValue(client.CaseId, out var stored) ? CasePublicStatuses.From(stored, client.FirmId) : null);
        }
    }

    /// <inheritdoc />
    public Task<CasePage> ListForAccessorAsync(Accessor accessor, int limit, string? cursor, bool archived = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        lock (_gate)
        {
            string index;
            IEnumerable<Case> visible;
            if (accessor.SeesEveryCase)
            {
                index = CaseRules.IndexByFirm;
                visible = _cases.Values.Where(stored => stored.FirmId == accessor.FirmId);
            }
            else
            {
                index = CaseRules.IndexByAssignee;
                visible = _cases.Values.Where(stored => stored.FirmId == accessor.FirmId && _assignments.ContainsKey((stored.Id, accessor.Subject)));
            }

            // The view (#355): a filter over the index, never a deleted case.
            index = CaseRules.ListingCursorTag(index, archived);
            var ordered = visible
                .Where(stored => !stored.Deleted && stored.Archived == archived)
                .OrderByDescending(stored => CaseRules.ListingSortKey(stored.CreatedAt, stored.Id), StringComparer.Ordinal)
                .ToList();
            if (cursor is not null)
            {
                var after = CaseRules.DecodeCursor(cursor, index).GetValueOrDefault("SK", string.Empty);
                ordered = [.. ordered.Where(stored => string.CompareOrdinal(CaseRules.ListingSortKey(stored.CreatedAt, stored.Id), after) < 0)];
            }

            var page = ordered.Take(limit).ToList();
            string? next = null;
            if (ordered.Count > limit && page.Count > 0)
            {
                var last = page[^1];
                next = CaseRules.EncodeCursor(new Dictionary<string, string> { ["SK"] = CaseRules.ListingSortKey(last.CreatedAt, last.Id) }, index);
            }

            return Task.FromResult(new CasePage(page.ToValueCollection(), next));
        }
    }

    /// <inheritdoc />
    public Task<Case?> UpdateAsync(Case updated, CaseStatus? expectedStatus = null, StatusChange? statusChange = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate)
        {
            if (!_cases.TryGetValue(updated.Id, out var existing) || existing.FirmId != updated.FirmId)
            {
                return Task.FromResult<Case?>(null);
            }

            if (expectedStatus is not null && existing.Status != expectedStatus)
            {
                throw new ConflictException(CaseRules.StatusMoved);
            }

            // Both, together — the real store's transaction.
            _cases[updated.Id] = updated;
            if (statusChange is not null)
            {
                History(updated.Id).Add(statusChange);
            }

            return Task.FromResult<Case?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StatusChange>> StatusHistoryAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StatusChange> history = _history.TryGetValue(caseId, out var changes)
                ? [.. changes.OrderBy(change => change.ChangedAt)]
                : [];
            return Task.FromResult(history);
        }
    }

    /// <inheritdoc />
    public Task AssignAsync(CaseAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        lock (_gate)
        {
            // Unconditional: the port says idempotent.
            _assignments[(assignment.CaseId, assignment.Subject)] = assignment;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> UnassignAsync(string caseId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_assignments.Remove((caseId, subject)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CaseAssignment>> AssigneesAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CaseAssignment> assignees = [.. _assignments.Values
                .Where(assignment => assignment.CaseId == caseId)
                .OrderBy(assignment => assignment.AssignedAt)
                .ThenBy(assignment => assignment.Subject, StringComparer.Ordinal)];
            return Task.FromResult(assignees);
        }
    }

    /// <summary>
    /// The filing store's half of its transaction (Python's
    /// <c>MemoryFilingStore</c> reached into <c>cases</c> and <c>history</c>):
    /// move the stored case to filed with its history row, only if it still
    /// exists undeleted in its firm with <paramref name="expectedStatus"/> and no
    /// history row shares the change's instant. False, writing nothing, otherwise.
    /// </summary>
    internal bool TryFile(Case filed, CaseStatus expectedStatus, StatusChange change)
    {
        lock (_gate)
        {
            if (!_cases.TryGetValue(filed.Id, out var current)
                || current.Deleted
                || current.FirmId != filed.FirmId
                || current.Status != expectedStatus
                || History(current.Id).Any(row => row.ChangedAt == change.ChangedAt))
            {
                return false;
            }

            _cases[current.Id] = current with
            {
                Status = filed.Status,
                FiledAt = filed.FiledAt,
                CaseNumber = filed.CaseNumber,
                UpdatedAt = filed.UpdatedAt,
            };
            History(current.Id).Add(change);
            return true;
        }
    }

    private List<StatusChange> History(string caseId)
    {
        if (!_history.TryGetValue(caseId, out var changes))
        {
            changes = [];
            _history[caseId] = changes;
        }

        return changes;
    }
}
