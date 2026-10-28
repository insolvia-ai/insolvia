using Insolvia.Core.Clients;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IClientBindingStore"/>. Enforces the two
/// conditions the real transaction does — one live binding per subject per
/// firm, one holder per role per case — and checks ALL of them before
/// applying ANY write: a fake that half-applied a refused transaction would
/// let a suite pass on code the real store rejects.
/// </summary>
public sealed class MemoryClientBindingStore : IClientBindingStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FirmId, string Subject), ClientBinding> _bindings = [];
    private readonly Dictionary<(string CaseId, string Subject), ClientBinding> _mirrors = [];
    private readonly Dictionary<(string CaseId, ClientRole Role), string> _claims = [];

    /// <summary>Who holds which role on which case — a test affordance.</summary>
    public IReadOnlyDictionary<(string CaseId, ClientRole Role), string> Claims
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<(string, ClientRole), string>(_claims);
            }
        }
    }

    /// <inheritdoc />
    public Task BindAsync(ClientBinding binding, IReadOnlyList<ClientBinding> narrowed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(narrowed);
        lock (_gate)
        {
            if (_bindings.TryGetValue((binding.FirmId, binding.Subject), out var previous) && previous.Status != BindingStatus.Revoked)
            {
                throw new ConflictException("that person already has portal access to a case; revoke it before inviting them again");
            }

            var allowed = narrowed.Select(other => other.Subject).Append(binding.Subject).ToHashSet(StringComparer.Ordinal);
            foreach (var role in binding.Roles)
            {
                if (_claims.TryGetValue((binding.CaseId, role), out var holder) && !allowed.Contains(holder))
                {
                    throw new ConflictException("another client already answers for that debtor");
                }
            }

            foreach (var other in narrowed)
            {
                if (!_bindings.TryGetValue((other.FirmId, other.Subject), out var current) || !current.Live)
                {
                    throw new ConflictException("a binding changed while this one was written");
                }
            }

            _bindings[(binding.FirmId, binding.Subject)] = binding;
            _mirrors[(binding.CaseId, binding.Subject)] = binding;
            foreach (var other in narrowed)
            {
                _bindings[(other.FirmId, other.Subject)] = other;
                _mirrors[(other.CaseId, other.Subject)] = other;
            }

            foreach (var role in binding.Roles)
            {
                _claims[(binding.CaseId, role)] = binding.Subject;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ClientBinding?> UpdateAsync(ClientBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            var key = (binding.FirmId, binding.Subject);
            if (!_bindings.ContainsKey(key))
            {
                return Task.FromResult<ClientBinding?>(null);
            }

            // A revocation releases the claims it names, each only if this
            // subject holds it or nobody does. One another subject now holds
            // (a revoke from a stale read) cancels the WHOLE write — the
            // DynamoDB store's transaction was cancelled, nothing written.
            var revoking = binding.Status == BindingStatus.Revoked;
            if (revoking && binding.Roles.Any(role => _claims.TryGetValue((binding.CaseId, role), out var holder) && holder != binding.Subject))
            {
                throw new ConflictException(ClientBindings.AccessChanged);
            }

            _bindings[key] = binding;
            _mirrors[(binding.CaseId, binding.Subject)] = binding;
            if (revoking)
            {
                foreach (var role in binding.Roles)
                {
                    _claims.Remove((binding.CaseId, role));
                }
            }

            return Task.FromResult<ClientBinding?>(binding);
        }
    }

    /// <inheritdoc />
    public Task<ClientBinding?> GetAsync(string firmId, string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_bindings.GetValueOrDefault((firmId, subject)));
        }
    }

    /// <inheritdoc />
    public Task<ClientBinding?> FindAsync(string subject, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var found = _bindings.Where(pair => pair.Key.Subject == subject).Select(pair => pair.Value).ToList();
            if (found.Count > 1)
            {
                throw new InvalidOperationException("a client resolves to more than one firm; refusing to guess");
            }

            return Task.FromResult(found.SingleOrDefault());
        }
    }

    /// <inheritdoc />
    public Task<ClientBinding?> FindByEmailAsync(string firmId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        lock (_gate)
        {
            var lowered = PyText.Lower(email);
            var found = _bindings
                .Where(pair => pair.Key.FirmId == firmId && pair.Value.Email == lowered)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ClientBinding>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ClientBinding> found = [.. _mirrors
                .Where(pair => pair.Key.CaseId == caseId)
                .Select(pair => pair.Value)
                .OrderBy(binding => binding.CreatedAt)
                .ThenBy(binding => binding.Subject, StringComparer.Ordinal)];
            return Task.FromResult(found);
        }
    }
}
