namespace Insolvia.Core.Ports;

/// <summary>
/// Creates — and re-invites — the Cognito account behind a firm user. THE
/// ONLY PORT THAT WRITES TO THE IDENTITY PROVIDER, and nothing here sets a
/// password: both methods map to <c>AdminCreateUser</c> (resend is
/// <c>MessageAction=RESEND</c>), so a compromised caller can create or
/// re-invite accounts it still cannot authenticate as. Removing someone from
/// a firm is not here either — it deletes the membership row only.
/// </summary>
public interface IUserDirectory
{
    /// <summary>
    /// Create the account and return its subject — the one the provider
    /// assigned, never a minted one. MUST throw <see cref="ConflictException"/>
    /// when the address already has an account (case-insensitively).
    /// </summary>
    Task<string> CreateUserAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-send the invitation to an account that has never completed first
    /// sign-in (#212). MUST throw <see cref="NotFoundException"/> for an
    /// address with no account and <see cref="ConflictException"/> for one
    /// that has already signed in (that is forgot-password's job).
    /// </summary>
    Task ResendInviteAsync(string email, CancellationToken cancellationToken = default);
}
