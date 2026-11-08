using Insolvia.Core.Auth;
using Insolvia.Core.Ports;
using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Core.Memory;

/// <summary>
/// A fixed <c>{kid: key}</c> map — the tests' and local development's
/// <see cref="IJwksProvider"/>, never composed in a deployed environment.
/// Throws <see cref="AuthFailureReason.UnknownKey"/> for anything else, exactly
/// as the port requires, so the unknown-kid path behaves as production's does.
/// </summary>
public sealed class StaticJwksProvider : IJwksProvider
{
    private readonly Dictionary<string, SecurityKey> _keys;

    /// <summary>Serves <paramref name="keys"/>.</summary>
    public StaticJwksProvider(IReadOnlyDictionary<string, SecurityKey>? keys = null)
    {
        _keys = keys is null ? new(StringComparer.Ordinal) : new(keys, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<SecurityKey> GetSigningKeyAsync(string kid, CancellationToken cancellationToken = default) =>
        _keys.TryGetValue(kid, out var key)
            ? ValueTask.FromResult(key)
            : throw new AuthenticationException(AuthFailureReason.UnknownKey);
}
