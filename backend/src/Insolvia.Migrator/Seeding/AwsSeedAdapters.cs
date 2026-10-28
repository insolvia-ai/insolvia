using System.Net;
using System.Security.Cryptography;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.S3;
using Amazon.S3.Model;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// <see cref="ISeedAccounts"/> against a real pool (seed.py's <c>CognitoAccounts</c>).
/// SUBJECTS ARE RESOLVED FRESH, NEVER PINNED: a sub stored as a secret goes
/// stale in silence when a pool is replaced, and seeding then writes a firm
/// for somebody who cannot sign in. One scoped grant beats that failure.
/// </summary>
/// <param name="cognito">The Cognito client.</param>
/// <param name="userPoolId">The firm pool the fixture's people live in.</param>
public sealed class CognitoSeedAccounts(IAmazonCognitoIdentityProvider cognito, string userPoolId) : ISeedAccounts
{
    /// <inheritdoc />
    public async Task<string> SubjectOfAsync(string email, CancellationToken cancellationToken = default) =>
        await FindAsync(email, cancellationToken).ConfigureAwait(false)
            ?? throw new SeedRefusedException($"no pool user for {email}, and the fixture gives no password to create one with");

    /// <inheritdoc />
    public async Task<string> EnsureAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(email, cancellationToken).ConfigureAwait(false) is null)
        {
            // SUPPRESS is not optional: these are .test addresses that can
            // never receive a temporary password, and SES would only bounce.
            // email_verified spares a verification step nothing can complete.
            await cognito.AdminCreateUserAsync(
                new AdminCreateUserRequest
                {
                    UserPoolId = userPoolId,
                    Username = email,
                    UserAttributes =
                    [
                        new AttributeType { Name = "email", Value = email },
                        new AttributeType { Name = "email_verified", Value = "true" },
                    ],
                    MessageAction = MessageActionType.SUPPRESS,
                },
                cancellationToken).ConfigureAwait(false);
        }

        // ALWAYS, even for an account that already existed: AdminCreateUser
        // leaves FORCE_CHANGE_PASSWORD, whose "set a new password" screen a
        // browser test cannot complete, and a rotated secret must converge.
        await cognito.AdminSetUserPasswordAsync(
            new AdminSetUserPasswordRequest { UserPoolId = userPoolId, Username = email, Password = password, Permanent = true },
            cancellationToken).ConfigureAwait(false);
        return await SubjectOfAsync(email, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindAsync(string email, CancellationToken cancellationToken)
    {
        AdminGetUserResponse user;
        try
        {
            user = await cognito.AdminGetUserAsync(new AdminGetUserRequest { UserPoolId = userPoolId, Username = email }, cancellationToken).ConfigureAwait(false);
        }
        catch (UserNotFoundException)
        {
            return null;
        }

        return user.UserAttributes?.FirstOrDefault(attribute => attribute.Name == "sub")?.Value
            ?? throw new SeedRefusedException("pool user has no sub attribute");
    }
}

/// <summary>
/// <see cref="IFixtureObjects"/> against the shared fixture bucket
/// (insolvia-services' <c>dev_fixtures</c>; seed.py's <c>S3FixtureObjects</c>):
/// a load is a server-side CopyObject per document into the target's own
/// case-documents bucket, so the loader never handles the bytes.
/// </summary>
/// <param name="s3">The S3 client.</param>
/// <param name="fixtureBucket">The shared fixture bucket.</param>
/// <param name="targetBucket">The target's case-documents bucket; null for publish.</param>
public sealed class S3FixtureObjects(IAmazonS3 s3, string fixtureBucket, string? targetBucket) : IFixtureObjects
{
    /// <inheritdoc />
    /// <remarks>
    /// NO ENCRYPTION HEADERS, deliberately: SSE-KMS without a key id means the
    /// AWS-managed key, which the target bucket's DenyForeignEncryptionKey
    /// refuses (the first staging run hit exactly that). No header lands on
    /// the bucket default, the case key. REPLACE so the content type is the
    /// document's, not the fixture upload's.
    /// </remarks>
    public Task CopyAsync(string sourceKey, string targetKey, string contentType, CancellationToken cancellationToken = default) =>
        s3.CopyObjectAsync(
            new CopyObjectRequest
            {
                SourceBucket = fixtureBucket,
                SourceKey = sourceKey,
                DestinationBucket = targetBucket ?? throw new SeedRefusedException("no document bucket to copy into"),
                DestinationKey = targetKey,
                ContentType = contentType,
                MetadataDirective = S3MetadataDirective.REPLACE,
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        using var body = new MemoryStream(content, writable: false);
        await s3.PutObjectAsync(
            new PutObjectRequest { BucketName = fixtureBucket, Key = key, InputStream = body, ContentType = contentType, AutoCloseStream = false },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> DigestAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = fixtureBucket, Key = key }, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound || error.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }
}
