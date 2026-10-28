using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IFilingAuthorizationStore"/>: the current item per
/// attorney, and the history keyed by signature id.
/// </summary>
public sealed class MemoryFilingAuthorizationStore : IFilingAuthorizationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FirmId, string AttorneyId), FilingAuthorization> _current = [];
    private readonly Dictionary<string, FilingAuthorization> _history = new(StringComparer.Ordinal);

    /// <summary>A snapshot of the history, by signature id — a test affordance.</summary>
    public IReadOnlyDictionary<string, FilingAuthorization> History
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, FilingAuthorization>(_history, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Make <paramref name="authorization"/> the stored current one, unconditionally — a test affordance (a stale signature).</summary>
    public void SetCurrent(FilingAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            _current[(authorization.FirmId, authorization.AttorneyId)] = authorization;
            _history[authorization.AuthorizationId] = authorization;
        }
    }

    /// <inheritdoc />
    public Task<FilingAuthorization?> GetCurrentAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_current.GetValueOrDefault((firmId, attorneyId)));
        }
    }

    /// <inheritdoc />
    public Task PutCurrentAsync(FilingAuthorization authorization, FilingAuthorization? replacing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            var key = (authorization.FirmId, authorization.AttorneyId);
            var heldId = _current.GetValueOrDefault(key)?.AuthorizationId;
            if (heldId != replacing?.AuthorizationId)
            {
                throw new ConflictException("the authorization changed while signing");
            }

            _current[key] = authorization;
            _history[authorization.AuthorizationId] = authorization;
            if (replacing is not null)
            {
                _history[replacing.AuthorizationId] = replacing with { Status = AuthorizationStatus.Superseded };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> WithdrawAsync(FilingAuthorization authorization, DateTimeOffset withdrawnAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            var key = (authorization.FirmId, authorization.AttorneyId);
            if (!_current.TryGetValue(key, out var held) || held.AuthorizationId != authorization.AuthorizationId)
            {
                return Task.FromResult(false);
            }

            _current.Remove(key);
            _history[authorization.AuthorizationId] = authorization with { Status = AuthorizationStatus.Withdrawn, WithdrawnAt = withdrawnAt };
            return Task.FromResult(true);
        }
    }
}
