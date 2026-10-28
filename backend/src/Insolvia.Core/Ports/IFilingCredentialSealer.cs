using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Ports;

/// <summary>
/// SEALS an attorney's CM/ECF credential under the vault key — the API's half,
/// and the whole of what the API may do with that key (ADR 0024, guardrail 3).
/// A separate port from <see cref="IFilingCredentialOpener"/> on purpose: the
/// API's composition holds nothing that could open a credential.
/// </summary>
public interface IFilingCredentialSealer
{
    /// <summary>Seal <paramref name="plaintext"/> bound to <paramref name="context"/>.</summary>
    Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default);
}
