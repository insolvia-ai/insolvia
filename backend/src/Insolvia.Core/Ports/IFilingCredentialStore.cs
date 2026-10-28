using Insolvia.Core.FilingCredentials;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists the vault's credentials (Python's <c>FilingCredentialStore</c>).
/// Every method is keyed by the attorney: a credential id that belongs to
/// somebody else is simply not found — ownership and lookup are the same key.
/// </summary>
public interface IFilingCredentialStore
{
    /// <summary>Write a NEW item; throws if the id is taken. Never a replace — rotation is revoke-then-enrol.</summary>
    Task CreateAsync(FilingCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Strongly consistent: the worker's re-check must see a revoke from a second ago.</summary>
    Task<FilingCredential?> GetAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default);

    /// <summary>The attorney's credentials, oldest first.</summary>
    Task<IReadOnlyList<FilingCredential>> ListForAttorneyAsync(string firmId, string attorneyId, CancellationToken cancellationToken = default);

    /// <summary>Destroy the item. True when one existed.</summary>
    Task<bool> DeleteAsync(string firmId, string attorneyId, string credentialId, CancellationToken cancellationToken = default);
}
