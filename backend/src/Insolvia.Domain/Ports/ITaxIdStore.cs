using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists the sealed tax-id items (Python's <c>TaxIdStore</c>).
/// <c>caseId</c> is WHERE THE ITEM LIVES TODAY, not what identifies it: the
/// ref is the identity. Ownership is not a parameter — the encryption context
/// is what refuses another firm's read.
/// </summary>
public interface ITaxIdStore
{
    /// <summary>Write the item whole, replacing any under the same ref — a correction reaches every matter pointing at it.</summary>
    Task PutAsync(string caseId, SealedTaxId sealedId, CancellationToken cancellationToken = default);

    /// <summary>The item under <paramref name="reference"/>, or null.</summary>
    Task<SealedTaxId?> GetAsync(string caseId, string reference, CancellationToken cancellationToken = default);
}
