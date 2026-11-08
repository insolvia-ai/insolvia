using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Insolvia.Aws.Cognito;
using Insolvia.Aws.Tests.Support;
using Insolvia.Core;

namespace Insolvia.Aws.Tests.Cognito;

// The adapter behind POST /v1/firm/users (API) and POST /v1/firms (admin):
// exactly the AdminCreateUser calls insolvia_core's CognitoUserDirectory
// makes, and its error mapping. No network — the SDK client is a fake.
public sealed class CognitoUserDirectoryTests
{
    private const string Pool = "us-east-1_EXAMPLE00";
    private const string Sub = "00000000-0000-4000-8000-00000000a11c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_invites_by_email_with_a_verified_address_and_returns_the_pools_subject()
    {
        var (client, state) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => new AdminCreateUserResponse
        {
            User = new UserType { Attributes = [new AttributeType { Name = "email", Value = "a@example.test" }, new AttributeType { Name = "sub", Value = Sub }] },
        });

        var subject = await new CognitoUserDirectory(client, Pool).CreateUserAsync("a@example.test", Ct);

        Assert.Equal(Sub, subject);
        var (method, raw) = Assert.Single(state.Calls);
        Assert.Equal(nameof(IAmazonCognitoIdentityProvider.AdminCreateUserAsync), method);
        var request = (AdminCreateUserRequest)raw;
        Assert.Equal(Pool, request.UserPoolId);
        Assert.Equal("a@example.test", request.Username);
        Assert.Equal(["email=a@example.test", "email_verified=true"], request.UserAttributes.Select(attribute => $"{attribute.Name}={attribute.Value}"));
        Assert.Equal(["EMAIL"], request.DesiredDeliveryMediums);

        // Cognito mails the temporary password: none is set or suppressed here.
        Assert.Null(request.TemporaryPassword);
        Assert.Null(request.MessageAction);
    }

    [Fact]
    public async Task An_existing_address_is_a_conflict()
    {
        var (client, _) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => throw new UsernameExistsException("exists"));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => new CognitoUserDirectory(client, Pool).CreateUserAsync("a@example.test", Ct));
        Assert.Equal("that email address already has an Insolvia account", refusal.Message);
    }

    [Fact]
    public async Task A_created_user_with_no_subject_is_never_guessed()
    {
        var (client, _) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => new AdminCreateUserResponse { User = new UserType { Attributes = [] } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CognitoUserDirectory(client, Pool).CreateUserAsync("a@example.test", Ct));
    }

    [Fact]
    public async Task Resend_is_admin_create_user_with_resend()
    {
        var (client, state) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => new AdminCreateUserResponse());

        await new CognitoUserDirectory(client, Pool).ResendInviteAsync("a@example.test", Ct);

        var request = (AdminCreateUserRequest)Assert.Single(state.Calls).Request;
        Assert.Equal(MessageActionType.RESEND, request.MessageAction);
        Assert.Equal("a@example.test", request.Username);
        Assert.Equal(["EMAIL"], request.DesiredDeliveryMediums);
    }

    [Fact]
    public async Task Resend_maps_an_unknown_account_and_a_confirmed_one()
    {
        var (unknown, _) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => throw new UserNotFoundException("no"));
        var (confirmed, _) = FakeAws.Create<IAmazonCognitoIdentityProvider>((_, _) => throw new UnsupportedUserStateException("confirmed"));

        await Assert.ThrowsAsync<NotFoundException>(() => new CognitoUserDirectory(unknown, Pool).ResendInviteAsync("a@example.test", Ct));
        var conflict = await Assert.ThrowsAsync<ConflictException>(() => new CognitoUserDirectory(confirmed, Pool).ResendInviteAsync("a@example.test", Ct));
        Assert.Contains("forgot-password", conflict.Message, StringComparison.Ordinal);
    }
}
