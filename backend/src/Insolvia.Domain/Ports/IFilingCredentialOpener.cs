using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Ports;

/// <summary>
/// OPENS a sealed credential — the filing worker's half. MUST throw under any
/// context but the one it was sealed with.
/// </summary>
public interface IFilingCredentialOpener
{
    /// <summary>Open <paramref name="envelope"/>; throws under any context but the sealing one.</summary>
    Task<string> OpenAsync(Envelope envelope, IReadOnlyDictionary<string, string> context, CancellationToken cancellationToken = default);
}
