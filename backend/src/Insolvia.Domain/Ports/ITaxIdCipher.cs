using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Ports;

/// <summary>
/// Seals and opens one tax identifier under the case key (Python's
/// <c>TaxIdCipher</c>; <see cref="TaxIdRules"/> owns the design). Both methods
/// take the SAME context the value was sealed with; an implementation MUST
/// throw — never return garbage or null — when opening under another: that
/// refusal is the replay protection. The KMS envelope adapter is 18.12's.
/// </summary>
public interface ITaxIdCipher
{
    /// <summary>Seal <paramref name="plaintext"/> bound to <paramref name="context"/>.</summary>
    Task<Envelope> SealAsync(string plaintext, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default);

    /// <summary>Open <paramref name="envelope"/>; throws under any context but the sealing one.</summary>
    Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default);
}
