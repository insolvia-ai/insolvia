using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Auth;
using Insolvia.Core.Ports;

namespace Insolvia.Core.FilingAuthorizations;

// An attorney's signed authorization for Insolvia to file on their behalf
// (ADR 0024, guardrail 2; from filing_authorization.py). The text ships in
// the artifact, versioned; a signature records the version and the digest
// of the exact text, and "current" means signed, this firm and attorney, and
// over the text as it ships today.

/// <summary>
/// One signature over one version of the text. <see cref="AttorneyId"/> is the
/// signer (the token's subject); <see cref="AuthenticatedAt"/> the
/// <c>auth_time</c> it was made under, so the record shows the sign-in was fresh.
/// </summary>
/// <param name="AuthorizationId">The server-minted uuid.</param>
/// <param name="FirmId">The attorney's firm.</param>
/// <param name="AttorneyId">The signer.</param>
/// <param name="TextVersion">The version signed.</param>
/// <param name="TextDigest">SHA-256 of that version's text.</param>
/// <param name="SignedAt">When.</param>
/// <param name="AuthenticatedAt">The sign-in's <c>auth_time</c>, epoch seconds.</param>
/// <param name="Status">Signed, superseded or withdrawn.</param>
/// <param name="WithdrawnAt">When it was withdrawn.</param>
public sealed record FilingAuthorization(
    string AuthorizationId,
    string FirmId,
    string AttorneyId,
    string TextVersion,
    string TextDigest,
    DateTimeOffset SignedAt,
    long AuthenticatedAt,
    AuthorizationStatus Status = AuthorizationStatus.Signed,
    DateTimeOffset? WithdrawnAt = null);

/// <summary>The authorization rules.</summary>
public static class FilingAuthorizationRules
{
    /// <summary>The text version a signature must be over to be current.</summary>
    public const string TextVersion = "2026-10-07-draft";

    /// <summary>A signature needs a sign-in at most this old.</summary>
    public const int SignatureMaxAgeSeconds = 300;

    private const string VersionAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789-";

    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);

    /// <summary>
    /// The exact text of <paramref name="version"/>, as shipped. Throws for a
    /// version that is not in the assembly (or a name that could escape its folder).
    /// </summary>
    public static string AuthorizationText(string version = TextVersion)
    {
        if (string.IsNullOrEmpty(version) || version.Any(ch => !VersionAlphabet.Contains(ch, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"not an authorization text version: '{version}'", nameof(version));
        }

        return Texts.GetOrAdd(version, static name =>
        {
            var assembly = typeof(FilingAuthorizationRules).Assembly;
            using var stream = assembly.GetManifestResourceStream($"Insolvia.Core.Agreements/filing-authorization/{name}.txt")
                ?? throw new FileNotFoundException($"no authorization text '{name}'");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    /// <summary>SHA-256 of the text's UTF-8 bytes, lower-case hex.</summary>
    public static string TextDigest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>THE definition of "a current authorization".</summary>
    public static bool IsCurrent(FilingAuthorization? authorization, string firmId, string attorneyId) =>
        authorization is not null
        && authorization.Status == AuthorizationStatus.Signed
        && authorization.FirmId == firmId
        && authorization.AttorneyId == attorneyId
        && authorization.TextVersion == TextVersion
        && authorization.TextDigest == TextDigest(AuthorizationText());

    /// <summary>
    /// The attorney's authorization in force, or null — returned even over an
    /// older version, so the status view can say "the text has changed".
    /// </summary>
    public static Task<FilingAuthorization?> CurrentAsync(string firmId, string attorneyId, IFilingAuthorizationStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetCurrentAsync(firmId, attorneyId, cancellationToken);
    }

    /// <summary>
    /// Record the caller's signature over the current text. Refuses, in order:
    /// a sign-in older than <see cref="SignatureMaxAgeSeconds"/> (403); a body
    /// naming another version or digest (409 — the attorney has not read this
    /// text); a second signature while one is current (409). Signing over an
    /// older version's signature supersedes it.
    /// </summary>
    public static async Task<FilingAuthorization> SignAsync(
        JsonNode? payload,
        string firmId,
        string attorneyId,
        long? authenticatedAt,
        DateTimeOffset now,
        IFilingAuthorizationStore store,
        IAccessLog accessLog,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessLog);
        var fresh = TokenVerification.RequireRecentAuthentication(authenticatedAt, now, SignatureMaxAgeSeconds);
        var (version, digest) = ParseSignature(payload);
        var expected = TextDigest(AuthorizationText());
        if (version != TextVersion || digest != expected)
        {
            throw new ConflictException("The authorization text has changed since it was shown — read the current version and sign again.");
        }

        var existing = await store.GetCurrentAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false);
        if (IsCurrent(existing, firmId, attorneyId))
        {
            throw new ConflictException("You have already signed the current authorization.");
        }

        var authorization = new FilingAuthorization(Guid.NewGuid().ToString(), firmId, attorneyId, TextVersion, expected, Clock.Now(clock), fresh);
        await store.PutCurrentAsync(authorization, existing, cancellationToken).ConfigureAwait(false);
        await accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.AuthorizationSign, authorizationId: authorization.AuthorizationId, clock: clock),
            cancellationToken).ConfigureAwait(false);
        return authorization;
    }

    private static (string Version, string Digest) ParseSignature(JsonNode? payload)
    {
        if (payload is not JsonObject body)
        {
            throw new ValidationException("request body must be a JSON object");
        }

        var version = Json.AsString(body["text_version"]);
        var digest = Json.AsString(body["text_digest"]);
        return version is null || digest is null
            ? throw new ValidationException("text_version and text_digest are required")
            : (version, digest);
    }
}

/// <summary>A filing authorization's state (Python's <c>AUTHORIZATION_STATUSES</c>).</summary>
public enum AuthorizationStatus
{
    /// <summary>In force (if over the current text).</summary>
    [JsonStringEnumMemberName("signed")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The wire value is \"signed\"; the member names the state, not a numeric type.")]
    Signed,

    /// <summary>Replaced by a later signature.</summary>
    [JsonStringEnumMemberName("superseded")]
    Superseded,

    /// <summary>Withdrawn by the attorney — which revoked every credential.</summary>
    [JsonStringEnumMemberName("withdrawn")]
    Withdrawn,
}
