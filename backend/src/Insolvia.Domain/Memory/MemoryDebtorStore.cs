using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IDebtorStore"/> (Python's <c>MemoryDebtorStore</c>),
/// keyed by (case, filing role) as the table is, so "one record per role per
/// case" is a property of the map. <paramref name="firms"/> is the store whose
/// client rows <see cref="LinkAsync"/> (and <see cref="MemoryCaseStore"/>'s
/// create) condition on; without one, a link refuses to run, as the real one
/// does. Every method runs under one lock — one atomic step each.
/// </summary>
/// <param name="firms">The firm store the client conditions read.</param>
public sealed class MemoryDebtorStore(IFirmStore? firms = null) : IDebtorStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string CaseId, FilingRole Role), Debtor> _debtors = [];

    /// <summary>A snapshot of every debtor — a test affordance.</summary>
    public IReadOnlyList<Debtor> Debtors
    {
        get
        {
            lock (_gate)
            {
                return [.. _debtors.Values];
            }
        }
    }

    /// <summary>
    /// The client condition, read from the composed firm store: the row exists
    /// and <see cref="FirmClients.FirmClient.RefusalForNewCase"/> has nothing to
    /// say. True when no firm store is composed (only the case store asks then,
    /// for the seed loader's reason).
    /// </summary>
    public async Task<bool> ClientLinkableAsync(string firmId, string clientId, CancellationToken cancellationToken = default)
    {
        if (firms is null)
        {
            return true;
        }

        var client = await firms.GetClientAsync(firmId, clientId, cancellationToken).ConfigureAwait(false);
        return client is not null && client.RefusalForNewCase() is null;
    }

    /// <inheritdoc />
    public Task<bool> CreateAsync(Debtor debtor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        return Locked(() => _debtors.TryAdd((debtor.CaseId, debtor.FilingRole), debtor), cancellationToken);
    }

    /// <inheritdoc />
    public Task PutAsync(Debtor debtor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        return Locked(() => _debtors[(debtor.CaseId, debtor.FilingRole)] = debtor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LinkOutcome> LinkAsync(Debtor debtor, bool create, string firmId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        var clientId = debtor.ClientId ?? throw new ArgumentException("link needs a debtor that names a client", nameof(debtor));
        if (firms is null)
        {
            throw new InvalidOperationException("link needs the firm store: a link must be conditional on the client row");
        }

        // The conditions, in the order the real store reads its cancellation
        // reasons. The client row is read outside the lock (the firm store is
        // async), so the role conditions are checked again under it before
        // the write — a race can only turn a write into a refusal.
        if (Taken(debtor, create) is { } early)
        {
            return early;
        }

        if (!await ClientLinkableAsync(firmId, clientId, cancellationToken).ConfigureAwait(false))
        {
            return LinkOutcome.ClientUnavailable;
        }

        lock (_gate)
        {
            if (Taken(debtor, create) is { } late)
            {
                return late;
            }

            _debtors[(debtor.CaseId, debtor.FilingRole)] = debtor;
            return LinkOutcome.Written;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(string CaseId, FilingRole FilingRole)>> RolesForClientAsync(string clientId, CancellationToken cancellationToken = default) =>
        Locked<IReadOnlyList<(string, FilingRole)>>(
            () => [.. _debtors
                .Where(pair => pair.Value.ClientId is not null && pair.Value.ClientId == clientId)
                .Select(pair => pair.Key)
                .OrderBy(key => key.CaseId, StringComparer.Ordinal)
                .ThenBy(key => key.Role.ToWire(), StringComparer.Ordinal)],
            cancellationToken);

    /// <inheritdoc />
    public Task<RepointOutcome> RepointClientAsync(string caseId, FilingRole filingRole, string fromClientId, string toClientId, CancellationToken cancellationToken = default) =>
        Locked(
            () =>
            {
                // Only the link moves — copied fields and provenance stay.
                if (!_debtors.TryGetValue((caseId, filingRole), out var stored) || stored.ClientId != fromClientId)
                {
                    return RepointOutcome.Absent;
                }

                if (_debtors.Any(pair => pair.Key.CaseId == caseId && pair.Key.Role != filingRole && pair.Value.ClientId == toClientId))
                {
                    return RepointOutcome.ClientTaken;
                }

                var caseCreatedAt = stored.CaseCreatedAt ?? throw new InvalidOperationException("a debtor naming a client has no case_created_at");
                _debtors[(caseId, filingRole)] = DebtorRules.LinkClient(stored, toClientId, caseCreatedAt);
                return RepointOutcome.Written;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebtorClientLink>> ClientLinksForCaseAsync(string caseId, CancellationToken cancellationToken = default) =>
        Locked<IReadOnlyList<DebtorClientLink>>(
            () => [.. Ordered(caseId).Select(debtor => new DebtorClientLink(caseId, debtor.FilingRole, debtor.ClientId))],
            cancellationToken);

    /// <inheritdoc />
    public Task<Debtor?> GetAsync(string caseId, FilingRole filingRole, CancellationToken cancellationToken = default) =>
        Locked(() => _debtors.GetValueOrDefault((caseId, filingRole)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Debtor>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default) =>
        Locked<IReadOnlyList<Debtor>>(() => [.. Ordered(caseId)], cancellationToken);

    private IEnumerable<Debtor> Ordered(string caseId) =>
        _debtors.Where(pair => pair.Key.CaseId == caseId)
            .Select(pair => pair.Value)
            .OrderBy(debtor => DebtorRules.RoleOrder(debtor.FilingRole))
            .ThenBy(debtor => debtor.FilingRole.ToWire(), StringComparer.Ordinal);

    private LinkOutcome? Taken(Debtor debtor, bool create)
    {
        lock (_gate)
        {
            if (create && _debtors.ContainsKey((debtor.CaseId, debtor.FilingRole)))
            {
                return LinkOutcome.RoleTaken;
            }

            return _debtors.Any(pair => pair.Key.CaseId == debtor.CaseId && pair.Key.Role != debtor.FilingRole && pair.Value.ClientId == debtor.ClientId)
                ? LinkOutcome.ClientTaken
                : null;
        }
    }

    private Task<T> Locked<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(action());
        }
    }
}
