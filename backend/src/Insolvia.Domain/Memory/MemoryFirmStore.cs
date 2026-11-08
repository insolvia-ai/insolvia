using Insolvia.Core.DocumentRequests;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Ports;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IFirmStore"/> — for tests and a local server, and
/// the behavioural spec the Postgres store is held to (18.10). It enforces
/// the conditions the real store does — no overwrite on create, firm-scoped
/// reads and writes, a throw on a subject in two firms, the merge claim —
/// because a suite passing against a looser store proves nothing.
/// </summary>
/// <remarks>
/// Rows are keyed exactly as the tables are: by (firm, key). A map keyed by
/// subject alone would make the firm-scoped reads impossible to get wrong,
/// which is the problem — the real store CAN get them wrong. Every method
/// runs under one lock, so each one is the single atomic step the real
/// store's conditional write or transaction is.
/// </remarks>
public sealed class MemoryFirmStore : IFirmStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Firm> _firms = new(StringComparer.Ordinal);
    private readonly Dictionary<(string FirmId, string Subject), FirmUser> _users = [];
    private readonly Dictionary<(string FirmId, string Id), LibraryCreditor> _libraryCreditors = [];
    private readonly Dictionary<(string FirmId, string Id), FirmClient> _clients = [];
    private readonly Dictionary<string, QuestionnaireConfig> _questionnaires = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DocumentChecklist> _checklists = new(StringComparer.Ordinal);
    private readonly TimeProvider? _clock;

    /// <summary>Creates an empty store; <paramref name="clock"/> stamps the store-side writes (funnel, retained).</summary>
    public MemoryFirmStore(TimeProvider? clock = null)
    {
        _clock = clock;
    }

    /// <summary>A snapshot of every firm — a test affordance.</summary>
    public IReadOnlyDictionary<string, Firm> Firms
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, Firm>(_firms, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>A snapshot of every firm user by (firm, subject) — a test affordance.</summary>
    public IReadOnlyDictionary<(string FirmId, string Subject), FirmUser> Users
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<(string, string), FirmUser>(_users);
            }
        }
    }

    /// <inheritdoc />
    public Task CreateFirmAsync(Firm firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        lock (_gate)
        {
            // Not a validation error: a uuid collision or a replayed write is
            // nothing the caller did.
            if (!_firms.TryAdd(firm.Id, firm))
            {
                throw new InvalidOperationException($"firm {firm.Id} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Firm?> GetFirmAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_firms.GetValueOrDefault(firmId));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Firm>> ListFirmsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Firm> listed = [.. _firms.Values
                .OrderBy(firm => firm.Name, StringComparer.Ordinal)
                .ThenBy(firm => firm.Id, StringComparer.Ordinal)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<Firm?> UpdateFirmAsync(Firm firm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firm);
        lock (_gate)
        {
            if (!_firms.ContainsKey(firm.Id))
            {
                return Task.FromResult<Firm?>(null);
            }

            _firms[firm.Id] = firm;
            return Task.FromResult<Firm?>(firm);
        }
    }

    /// <inheritdoc />
    public Task AddUserAsync(FirmUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (!_users.TryAdd((user.FirmId, user.Subject), user))
            {
                throw new InvalidOperationException("firm user already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FirmUser?> GetUserAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.GetValueOrDefault((firmId, subject)));
        }
    }

    /// <inheritdoc />
    public Task<FirmUser?> FindUserAsync(string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var matches = _users.Values.Where(user => user.Subject == subject).ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException("a firm user resolves to more than one firm; refusing to guess");
            }

            return Task.FromResult(matches.SingleOrDefault());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FirmUser>> ListUsersAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // BY SURNAME, and identical to the Postgres store's ORDER BY.
            IReadOnlyList<FirmUser> listed = [.. _users.Values
                .Where(user => user.FirmId == firmId)
                .OrderBy(user => user.LastName, StringComparer.Ordinal)
                .ThenBy(user => user.FirstName, StringComparer.Ordinal)
                .ThenBy(user => user.Subject, StringComparer.Ordinal)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<FirmUser?> UpdateUserAsync(FirmUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var key = (user.FirmId, user.Subject);
            if (!_users.ContainsKey(key))
            {
                return Task.FromResult<FirmUser?>(null);
            }

            _users[key] = user;
            return Task.FromResult<FirmUser?>(user);
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveUserAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Remove((firmId, subject)));
        }
    }

    /// <inheritdoc />
    public Task CreateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        lock (_gate)
        {
            if (!_libraryCreditors.TryAdd((creditor.FirmId, creditor.Id), creditor))
            {
                throw new InvalidOperationException($"library creditor {creditor.Id} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LibraryCreditor?> GetLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_libraryCreditors.GetValueOrDefault((firmId, creditorId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LibraryCreditor>> ListLibraryCreditorsAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<LibraryCreditor> listed = [.. _libraryCreditors.Values
                .Where(creditor => creditor.FirmId == firmId)
                .OrderBy(creditor => creditor.Name, StringComparer.Ordinal)
                .ThenBy(creditor => creditor.Id, StringComparer.Ordinal)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<LibraryCreditor?> UpdateLibraryCreditorAsync(LibraryCreditor creditor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creditor);
        lock (_gate)
        {
            var key = (creditor.FirmId, creditor.Id);
            if (!_libraryCreditors.ContainsKey(key))
            {
                return Task.FromResult<LibraryCreditor?>(null);
            }

            _libraryCreditors[key] = creditor;
            return Task.FromResult<LibraryCreditor?>(creditor);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteLibraryCreditorAsync(string firmId, string creditorId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_libraryCreditors.Remove((firmId, creditorId)));
        }
    }

    /// <inheritdoc />
    public Task CreateClientAsync(FirmClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (!_clients.TryAdd((client.FirmId, client.Id), client))
            {
                throw new InvalidOperationException($"client {client.Id} already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FirmClient?> GetClientAsync(string firmId, string clientId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_clients.GetValueOrDefault((firmId, clientId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FirmClient>> ListClientsAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FirmClient> listed = FirmClientRules.Sorted(_clients.Values.Where(client => client.FirmId == firmId));
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<FirmClient?> UpdateClientAsync(FirmClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            var key = (client.FirmId, client.Id);
            if (!_clients.TryGetValue(key, out var stored) || stored.MergedInto is not null)
            {
                return Task.FromResult<FirmClient?>(null);
            }

            // The record's own fields only: the server-owned attributes stay
            // as stored, as the real store's UPDATE leaves them.
            var written = client with
            {
                MergedInto = stored.MergedInto,
                MergingInto = stored.MergingInto,
                MergingFrom = stored.MergingFrom,
                ProspectStage = stored.ProspectStage,
            };
            _clients[key] = written;
            return Task.FromResult<FirmClient?>(written);
        }
    }

    /// <inheritdoc />
    public Task<FirmClient?> SetClientProspectStageAsync(string firmId, string clientId, ProspectStage? stage, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = (firmId, clientId);
            if (!_clients.TryGetValue(key, out var stored) || stored.MergedInto is not null || !stored.Prospect)
            {
                return Task.FromResult<FirmClient?>(null);
            }

            var written = stored with { ProspectStage = stage, UpdatedAt = Clock.Now(_clock) };
            _clients[key] = written;
            return Task.FromResult<FirmClient?>(written);
        }
    }

    /// <inheritdoc />
    public Task<FirmClient?> MarkClientRetainedAsync(string firmId, string clientId, DateOnly retainedOn, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = (firmId, clientId);
            if (!_clients.TryGetValue(key, out var stored) || stored.MergedInto is not null)
            {
                return Task.FromResult<FirmClient?>(null);
            }

            var written = stored with
            {
                FirstRetainedAt = stored.FirstRetainedAt ?? retainedOn,
                ProspectStage = null,
                UpdatedAt = Clock.Now(_clock),
            };
            _clients[key] = written;
            return Task.FromResult<FirmClient?>(written);
        }
    }

    /// <inheritdoc />
    public Task<bool> ClaimClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue((firmId, mergedId), out var merged)
                || !_clients.TryGetValue((firmId, survivorId), out var survivor)
                || mergedId == survivorId)
            {
                return Task.FromResult(false);
            }

            if (!Claimable(merged, survivorId, asSurvivor: false) || !Claimable(survivor, mergedId, asSurvivor: true))
            {
                return Task.FromResult(false);
            }

            _clients[(firmId, mergedId)] = merged with { MergingInto = survivorId };
            _clients[(firmId, survivorId)] = survivor with { MergingFrom = mergedId };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<FirmClient?> FinishClientMergeAsync(string firmId, string mergedId, string survivorId, DateTimeOffset mergedAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue((firmId, mergedId), out var merged)
                || !_clients.TryGetValue((firmId, survivorId), out var survivor)
                || merged.MergingInto != survivorId
                || survivor.MergingFrom != mergedId)
            {
                return Task.FromResult<FirmClient?>(null);
            }

            var archived = merged with
            {
                Status = FirmClientStatus.Archived,
                MergedInto = survivorId,
                MergingInto = null,
                UpdatedAt = mergedAt,
            };
            _clients[(firmId, mergedId)] = archived;
            _clients[(firmId, survivorId)] = survivor with { MergingFrom = null };
            return Task.FromResult<FirmClient?>(archived);
        }
    }

    /// <inheritdoc />
    public Task ReleaseClientMergeAsync(string firmId, string mergedId, string survivorId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue((firmId, mergedId), out var merged) && merged.MergingInto == survivorId)
            {
                _clients[(firmId, mergedId)] = merged with { MergingInto = null };
            }

            if (_clients.TryGetValue((firmId, survivorId), out var survivor) && survivor.MergingFrom == mergedId)
            {
                _clients[(firmId, survivorId)] = survivor with { MergingFrom = null };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<QuestionnaireConfig?> GetQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_questionnaires.GetValueOrDefault(firmId));
        }
    }

    /// <inheritdoc />
    public Task PutQuestionnaireAsync(QuestionnaireConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _questionnaires[config.FirmId] = config;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteQuestionnaireAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_questionnaires.Remove(firmId));
        }
    }

    /// <inheritdoc />
    public Task<DocumentChecklist?> GetDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_checklists.GetValueOrDefault(firmId));
        }
    }

    /// <inheritdoc />
    public Task PutDocumentChecklistAsync(DocumentChecklist checklist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        lock (_gate)
        {
            _checklists[checklist.FirmId] = checklist;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteDocumentChecklistAsync(string firmId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_checklists.Remove(firmId));
        }
    }

    // The claim's condition on one row: active, never merged, and not already
    // in a merge except this same one.
    private static bool Claimable(FirmClient client, string otherId, bool asSurvivor)
    {
        if (client.Status != FirmClientStatus.Active || client.MergedInto is not null)
        {
            return false;
        }

        return asSurvivor
            ? client.MergingInto is null && (client.MergingFrom is null || client.MergingFrom == otherId)
            : client.MergingFrom is null && (client.MergingInto is null || client.MergingInto == otherId);
    }
}
