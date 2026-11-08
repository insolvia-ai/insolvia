using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Insolvia.Core.Documents;
using Insolvia.Core.Ports;

namespace Insolvia.Aws.Documents;

/// <summary>
/// <see cref="IDocumentBlobStore"/> over the case-document bucket (Python's
/// <c>S3DocumentBlobStore</c>, adapters/aws/document_blobs.py). Every URL is a
/// SigV4 presigned capability for ONE key and ONE verb; an upload URL binds the
/// content type, the exact length and the <c>upload=unconfirmed</c> tag into
/// its signed headers, so a client that sends anything else is refused by S3.
/// </summary>
/// <remarks>
/// <para><b>Virtual-hosted addressing, SigV4</b>, as the Python configured
/// botocore: give the client a <see cref="AmazonS3Config"/> with
/// <c>ForcePathStyle = false</c> (the SDK's default) — see <see cref="ClientConfig"/>.</para>
/// <para><b>403 is absence.</b> The API's role has no <c>s3:ListBucket</c>,
/// so S3 answers a HEAD (or GET) of a missing key with 403, not 404: both
/// read as "not there", which the port requires — the route answers 409 and
/// the client re-uploads. Anything else (throttling, 5xx) propagates.</para>
/// </remarks>
/// <param name="s3">The S3 client.</param>
/// <param name="bucketName">The case-document bucket (<c>CASE_DOCUMENT_BUCKET</c>).</param>
/// <param name="clock">Dates a presigned URL's expiry; the system clock when null.</param>
public sealed class S3DocumentBlobStore(IAmazonS3 s3, string bucketName, TimeProvider? clock = null) : IDocumentBlobStore
{
    private const string TaggingHeader = "x-amz-tagging";

    private readonly IAmazonS3 _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The bucket.</summary>
    public string BucketName { get; } = string.IsNullOrEmpty(bucketName) ? throw new ArgumentException("a bucket name is required", nameof(bucketName)) : bucketName;

    /// <summary>The client settings the Python's botocore <c>Config</c> named: SigV4 (the v4 SDK signs nothing else) and virtual-hosted addressing.</summary>
    /// <returns>A config to build the <see cref="AmazonS3Client"/> from.</returns>
    public static AmazonS3Config ClientConfig() => new() { ForcePathStyle = false };

    /// <inheritdoc />
    public Task<string> UploadUrlAsync(string storageRef, string contentType, long byteSize, int expiresIn, CancellationToken cancellationToken = default)
    {
        var request = Presign(storageRef, HttpVerb.PUT, expiresIn);
        request.ContentType = contentType;
        request.Headers.ContentLength = byteSize;
        request.Headers[TaggingHeader] = DocumentRules.UploadTag;
        return _s3.GetPreSignedURLAsync(request);
    }

    /// <inheritdoc />
    public Task<string> DownloadUrlAsync(string storageRef, int expiresIn, CancellationToken cancellationToken = default) =>
        _s3.GetPreSignedURLAsync(Presign(storageRef, HttpVerb.GET, expiresIn));

    /// <inheritdoc />
    public async Task<StoredBlob?> StatAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        try
        {
            var head = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = BucketName, Key = storageRef }, cancellationToken).ConfigureAwait(false);
            return new StoredBlob(head.ContentLength, head.ETag.Trim('"'));
        }
        catch (AmazonS3Exception error) when (IsAbsent(error))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task ClearUploadTagAsync(string storageRef, CancellationToken cancellationToken = default) =>
        _s3.PutObjectTaggingAsync(
            new PutObjectTaggingRequest { BucketName = BucketName, Key = storageRef, Tagging = new Tagging { TagSet = [] } },
            cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken = default) =>
        _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = BucketName, Key = storageRef }, cancellationToken);

    /// <inheritdoc />
    public async Task PutBytesAsync(string storageRef, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        using var body = new MemoryStream(content.ToArray(), writable: false);
        await _s3.PutObjectAsync(
            new PutObjectRequest { BucketName = BucketName, Key = storageRef, InputStream = body, ContentType = contentType, AutoCloseStream = false },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBytesAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = BucketName, Key = storageRef }, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception error) when (IsAbsent(error))
        {
            return null;
        }
    }

    // botocore's codes "403" / "AccessDenied" / "404" / "NoSuchKey".
    private static bool IsAbsent(AmazonS3Exception error) =>
        error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
        || error.ErrorCode is "AccessDenied" or "NoSuchKey" or "NotFound";

    private GetPreSignedUrlRequest Presign(string storageRef, HttpVerb verb, int expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageRef);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expiresIn);
        return new GetPreSignedUrlRequest
        {
            BucketName = BucketName,
            Key = storageRef,
            Verb = verb,
            Protocol = Protocol.HTTPS,
            Expires = _clock.GetUtcNow().UtcDateTime.AddSeconds(expiresIn),
        };
    }
}
