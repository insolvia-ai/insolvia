using Insolvia.Domain.FilingCredentials;

namespace Insolvia.Api.Filing;

/// <summary>
/// A CM/ECF credential's status — the only shape of a credential a client
/// ever receives: no password, no authenticator key, no sealed envelope.
/// </summary>
/// <param name="Id">The credential's id.</param>
/// <param name="Login">The PACER username.</param>
/// <param name="Courts">The court codes the attorney e-files in with it.</param>
/// <param name="Status">Always <c>active</c> while it exists: revoking destroys it.</param>
/// <param name="CreatedAt">When it was enrolled, ISO 8601 UTC with microseconds.</param>
/// <param name="UpdatedAt">When it was last written, ISO 8601 UTC with microseconds.</param>
public sealed record FilingCredentialView(
    string Id,
    string Login,
    IReadOnlyList<string> Courts,
    CredentialStatus Status,
    string CreatedAt,
    string UpdatedAt)
{
    /// <summary>The view of <paramref name="credential"/>.</summary>
    /// <param name="credential">The stored credential.</param>
    /// <returns>The view.</returns>
    public static FilingCredentialView Of(FilingCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new(
            credential.CredentialId,
            credential.Login,
            [.. credential.Courts],
            credential.Status,
            WireFormat.Micros(credential.CreatedAt),
            WireFormat.Micros(credential.UpdatedAt));
    }
}

/// <summary>The caller's own CM/ECF credentials.</summary>
/// <param name="Credentials">Each credential's status.</param>
public sealed record FilingCredentialList(IReadOnlyList<FilingCredentialView> Credentials);
