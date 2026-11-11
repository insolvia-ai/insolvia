using Insolvia.Domain.FilingAuthorizations;

namespace Insolvia.Api.Filing;

/// <summary>The authorization text an attorney is asked to sign, with what they must post back.</summary>
/// <param name="Version">The text's version; post it back as <c>text_version</c>.</param>
/// <param name="Digest">SHA-256 of the text; post it back as <c>text_digest</c>.</param>
/// <param name="Text">The text itself.</param>
public sealed record AuthorizationTextView(string Version, string Digest, string Text)
{
    /// <summary>The text in force.</summary>
    /// <returns>The view.</returns>
    public static AuthorizationTextView Current()
    {
        var text = FilingAuthorizationRules.AuthorizationText();
        return new(FilingAuthorizationRules.TextVersion, FilingAuthorizationRules.TextDigest(text), text);
    }
}

/// <summary>
/// The caller's written authorization to file: the signature on record (in
/// force or stale), and the text a new signature would be over.
/// </summary>
/// <param name="Text">The text in force.</param>
/// <param name="CurrentVersion">The version a signature must be over to be current.</param>
/// <param name="Current">Whether the caller's signature is over the current version.</param>
/// <param name="Signature">The signature on record; null when the caller never signed or withdrew.</param>
[SnakeCase]
public sealed record FilingAuthorizationView(
    AuthorizationTextView Text,
    string CurrentVersion,
    bool Current,
    AuthorizationSignatureView? Signature)
{
    /// <summary>The view of <paramref name="authorization"/> for the caller.</summary>
    /// <param name="authorization">The signature on record, if any.</param>
    /// <param name="firmId">The caller's firm.</param>
    /// <param name="attorneyId">The caller.</param>
    /// <returns>The view.</returns>
    public static FilingAuthorizationView Of(FilingAuthorization? authorization, string firmId, string attorneyId) => new(
        AuthorizationTextView.Current(),
        FilingAuthorizationRules.TextVersion,
        FilingAuthorizationRules.IsCurrent(authorization, firmId, attorneyId),
        authorization is null
            ? null
            : new(authorization.AuthorizationId, authorization.TextVersion, authorization.TextDigest, WireFormat.Micros(authorization.SignedAt)));
}

/// <summary>A signature on record.</summary>
/// <param name="Id">The signature's id.</param>
/// <param name="TextVersion">The version signed.</param>
/// <param name="TextDigest">SHA-256 of that version's text.</param>
/// <param name="SignedAt">When, ISO 8601 UTC with microseconds.</param>
[SnakeCase]
public sealed record AuthorizationSignatureView(string Id, string TextVersion, string TextDigest, string SignedAt);

/// <summary>
/// A signature (<c>POST /v1/me/filing-authorization</c>): the version and
/// digest of the text as shown, which must be the current one. Parsed by
/// <c>FilingAuthorizationRules.SignAsync</c>.
/// </summary>
/// <param name="TextVersion">The version shown.</param>
/// <param name="TextDigest">Its digest.</param>
[SnakeCase]
public sealed record AuthorizationSignRequest(string TextVersion, string TextDigest);

/// <summary>What withdrawing the authorization destroyed.</summary>
/// <param name="CredentialsRevoked">How many credentials were destroyed with it.</param>
[SnakeCase]
public sealed record AuthorizationWithdrawn(int CredentialsRevoked);
