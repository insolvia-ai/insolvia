using System.Text.Json.Serialization;

namespace Insolvia.Core.Auth;

/// <summary>
/// Why verification failed, at a granularity that is safe to log: a category
/// only, never the token, a claim, or anything derived from them (GLBA).
/// </summary>
public enum AuthFailureReason
{
    /// <summary>No credentials were sent.</summary>
    [JsonStringEnumMemberName("missing_credentials")]
    MissingCredentials,

    /// <summary>Something was sent that is not a bearer credential.</summary>
    [JsonStringEnumMemberName("malformed_header")]
    MalformedHeader,

    /// <summary>The token's header could not be read.</summary>
    [JsonStringEnumMemberName("malformed_token")]
    MalformedToken,

    /// <summary>No signing key for the token's <c>kid</c>.</summary>
    [JsonStringEnumMemberName("unknown_key")]
    UnknownKey,

    /// <summary>Bad signature, a refused algorithm, or anything unrecognised.</summary>
    [JsonStringEnumMemberName("invalid_signature")]
    InvalidSignature,

    /// <summary>Past <c>exp</c> by more than the leeway.</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>Not our issuer.</summary>
    [JsonStringEnumMemberName("invalid_issuer")]
    InvalidIssuer,

    /// <summary>Not our app client (or, for Google, audience).</summary>
    [JsonStringEnumMemberName("invalid_client")]
    InvalidClient,

    /// <summary>An ID token where an access token is required.</summary>
    [JsonStringEnumMemberName("wrong_token_use")]
    WrongTokenUse,

    /// <summary>A required claim is missing or wrong.</summary>
    [JsonStringEnumMemberName("invalid_claims")]
    InvalidClaims,

    /// <summary>The deployment has no auth configuration — fail closed.</summary>
    [JsonStringEnumMemberName("not_configured")]
    NotConfigured,
}

/// <summary>
/// The request carried no usable proof of identity (Python's
/// <c>AuthenticationError</c>). Deliberately NOT an <see cref="ApiException"/>:
/// it is a 401, answered with a generic body — the caller learns
/// "unauthorized", never which check failed.
/// </summary>
public sealed class AuthenticationException : Exception
{
    /// <summary>Creates the exception for <paramref name="reason"/>.</summary>
    public AuthenticationException(AuthFailureReason reason)
        : base("authentication failed: " + reason.ToWire())
    {
        Reason = reason;
    }

    /// <summary>Creates the exception for <paramref name="reason"/>, keeping the cause.</summary>
    public AuthenticationException(AuthFailureReason reason, Exception innerException)
        : base("authentication failed: " + reason.ToWire(), innerException)
    {
        Reason = reason;
    }

    /// <summary>The coarse, loggable category.</summary>
    public AuthFailureReason Reason { get; }
}

/// <summary>
/// The caller is signed in, but not RECENTLY enough for this act (ADR 0024).
/// A 403, not a 401: a refresh keeps <c>auth_time</c> and cannot help, so the
/// remedy is a fresh sign-in, never the client's refresh-and-retry loop.
/// </summary>
public sealed class ReauthenticationRequiredException : ForbiddenException
{
    /// <summary>Creates the exception with no message.</summary>
    public ReauthenticationRequiredException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ReauthenticationRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ReauthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The two values that decide whether a Cognito access token is ours. Both required.</summary>
public sealed record AuthSettings(string IssuerUrl, string ClientId);

/// <summary>The issuer plus the client ALLOWLIST (the MCP profile, issue #261).</summary>
public sealed record MultiClientAuthSettings(string IssuerUrl, ValueCollection<string> ClientIds);

/// <summary>The two values that decide whether a Google Workspace ID token is ours.</summary>
public sealed record GoogleAuthSettings(string ClientId, string WorkspaceDomain);

/// <summary>
/// The authenticated caller, derived purely from verified claims.
/// <see cref="Subject"/> (<c>sub</c>) is the only thing that keys user-owned
/// data; <see cref="Username"/> is a Cognito-generated UUID, never an email.
/// </summary>
/// <param name="Subject">The pool's stable user identifier.</param>
/// <param name="Username">Cognito's <c>username</c>, when present.</param>
/// <param name="ClientId">The app client the token was minted for.</param>
/// <param name="Scopes">The space-delimited <c>scope</c> claim, split.</param>
/// <param name="ExpiresAt"><c>exp</c>, epoch seconds, when it is a whole number.</param>
/// <param name="AuthenticatedAt">
/// <c>auth_time</c>, epoch seconds: when the person last actually signed in.
/// Null when the claim is not a positive whole number — treated as stale.
/// </param>
public sealed record Principal(
    string Subject,
    string? Username,
    string ClientId,
    ValueCollection<string> Scopes,
    long? ExpiresAt,
    long? AuthenticatedAt = null);

/// <summary>
/// The authenticated STAFF caller (ADR 0011), from a verified Google ID token.
/// <see cref="Subject"/> is Google's <c>sub</c> and what audit rows key on;
/// <see cref="Email"/> is display and audit only.
/// </summary>
public sealed record StaffPrincipal(string Subject, string Email, long? ExpiresAt);
