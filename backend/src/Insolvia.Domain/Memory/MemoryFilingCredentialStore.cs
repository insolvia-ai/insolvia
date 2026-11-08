using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>The in-memory <see cref="IFilingCredentialStore"/>, keyed by (firm, attorney, credential).</summary>
public sealed class MemoryFilingCredentialStore : IFilingCredentialStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string FirmId, string AttorneyId, string CredentialId), FilingCredential> _items = [];

    /// <summary>A snapshot of every credential — a test affordance (tests search them for secrets).</summary>
    public IReadOnlyList<FilingCredential> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items.Values];
            }
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(FilingCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_gate)
        {
            if (!_items.TryAdd((credential.FirmId, credential.AttorneyId, credential.CredentialId), credential))
            {
                throw new InvalidOperationException("credential id already exists");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FilingCredential?> GetAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.GetValueOrDefault((firmId, attorneyId, credentialId)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FilingCredential>> ListForAttorneyAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FilingCredential> listed = [.. _items
                .Where(pair => pair.Key.FirmId == firmId && pair.Key.AttorneyId == attorneyId)
                .Select(pair => pair.Value)
                .OrderBy(credential => credential.CreatedAt)
                .ThenBy(credential => credential.CredentialId, StringComparer.Ordinal)];
            return Task.FromResult(listed);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Remove((firmId, attorneyId, credentialId)));
        }
    }
}
