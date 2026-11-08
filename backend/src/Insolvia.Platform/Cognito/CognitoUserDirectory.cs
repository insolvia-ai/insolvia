using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Insolvia.Core;
using Insolvia.Core.Ports;

namespace Insolvia.Aws.Cognito;

/// <summary>
/// <see cref="IUserDirectory"/> backed by the environment's firm Cognito pool
/// (insolvia_core adapters/aws/user_directory.py). Both methods are
/// <c>AdminCreateUser</c> — the one IAM action the API and admin roles hold on
/// the pool — so a compromised caller can create or re-invite accounts it
/// still cannot authenticate as.
/// </summary>
/// <remarks>
/// <para><b>Nothing here ever sees a password.</b> Cognito mails the temporary
/// password to the address; none is generated, returned or set here, and the
/// roles hold no <c>AdminSetUserPassword</c> — which is what keeps creating an
/// account from being a way to become one.</para>
/// <para>The address arrives lower-cased (the firm parser normalises it); the
/// pool is case-insensitive as well (<c>username_configuration</c>), so the
/// duplicate refusal is right for any casing without a second normaliser here.</para>
/// </remarks>
public sealed class CognitoUserDirectory : IUserDirectory
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly string _userPoolId;

    /// <summary>A directory over <paramref name="userPoolId"/>.</summary>
    /// <param name="cognito">The Cognito client (the default credential chain in a host; a fake in tests).</param>
    /// <param name="userPoolId">The firm pool's id.</param>
    public CognitoUserDirectory(IAmazonCognitoIdentityProvider cognito, string userPoolId)
    {
        _cognito = cognito ?? throw new ArgumentNullException(nameof(cognito));
        ArgumentException.ThrowIfNullOrWhiteSpace(userPoolId);
        _userPoolId = userPoolId;
    }

    /// <inheritdoc />
    public async Task<string> CreateUserAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        AdminCreateUserResponse response;
        try
        {
            response = await _cognito.AdminCreateUserAsync(
                new AdminCreateUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email,
                    UserAttributes =
                    [
                        new AttributeType { Name = "email", Value = email },

                        // auto_verified_attributes governs SELF-service
                        // verification; an admin-created user starts
                        // UNVERIFIED unless this says otherwise, and an
                        // unverified address cannot drive account recovery.
                        new AttributeType { Name = "email_verified", Value = "true" },
                    ],
                    DesiredDeliveryMediums = ["EMAIL"],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (UsernameExistsException error)
        {
            // A 409 naming the cause: a small enumeration surface for an
            // authenticated firm admin, and the right trade — "already here"
            // and "something broke" have different fixes.
            throw new ConflictException("that email address already has an Insolvia account", error);
        }

        // The subject Cognito assigned, read back rather than minted: a
        // made-up value would be a firm-user row nobody can sign in as.
        return response.User?.Attributes?.FirstOrDefault(attribute => attribute.Name == "sub")?.Value
            ?? throw new InvalidOperationException("Cognito returned a created user with no subject");
    }

    /// <inheritdoc />
    public async Task ResendInviteAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        try
        {
            await _cognito.AdminCreateUserAsync(
                new AdminCreateUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email,
                    MessageAction = MessageActionType.RESEND,
                    DesiredDeliveryMediums = ["EMAIL"],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (UserNotFoundException error)
        {
            throw new NotFoundException("no account exists for that address", error);
        }
        catch (UnsupportedUserStateException error)
        {
            // Already CONFIRMED: the invite did its job, and Cognito refuses
            // RESEND for exactly this state.
            throw new ConflictException("that user has already completed first sign-in; the forgot-password flow is the way back in", error);
        }
    }
}
