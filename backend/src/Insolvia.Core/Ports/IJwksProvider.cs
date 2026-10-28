using Microsoft.IdentityModel.Tokens;

namespace Insolvia.Core.Ports;

/// <summary>
/// Supplies the public key a JWT's <c>kid</c> names (issue #79). This port is
/// what keeps <see cref="Auth.TokenVerification"/> pure: verification needs a
/// key, fetching one needs the network, and Core may not have it. The real
/// adapter reads the pool's <c>&lt;issuer&gt;/.well-known/jwks.json</c> (or
/// Google's certs URL) and caches by <c>kid</c>; the static one in
/// <see cref="Memory.StaticJwksProvider"/> is handed keys directly.
/// </summary>
public interface IJwksProvider
{
    /// <summary>
    /// The key for <paramref name="kid"/>. MUST throw
    /// <see cref="Auth.AuthenticationException"/> with
    /// <see cref="Auth.AuthFailureReason.UnknownKey"/> for a <c>kid</c> it
    /// cannot resolve — including after a refresh — never return a placeholder.
    /// </summary>
    ValueTask<SecurityKey> GetSigningKeyAsync(string kid, CancellationToken cancellationToken = default);
}
