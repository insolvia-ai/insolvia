using System.Globalization;
using System.Security.Cryptography;
using Insolvia.Core.Documents;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Local;

/// <summary>
/// A document store over a local directory — LOCAL ONLY. Two things write
/// into one directory: the migrator's local seed (the fixture documents) and a
/// local API (packets, previews). Given a <paramref name="baseUrl"/> it also
/// mints capabilities, pointing at a local server that serves the same
/// directory: <c>contract/scripts/aux-server.ts</c>, which accepts one PUT with
/// the bound content type and length and serves GETs — S3's presigned URLs,
/// minus the signature. Without a base URL it mints nothing (the migrator's
/// use: there is no server to sign for).
/// </summary>
/// <remarks>
/// <para>Layout, which the aux server shares: the object is
/// <c>&lt;root&gt;/&lt;storageRef&gt;</c>; its content type, when one was
/// declared, is <c>&lt;root&gt;/&lt;storageRef&gt;.content-type</c>. A
/// capability is <c>&lt;baseUrl&gt;/blobs/&lt;storageRef, escaped whole&gt;?method=…</c>
/// with the terms the S3 adapter binds into its signature in the query — no
/// looser than <see cref="Memory.MemoryDocumentBlobStore"/>.</para>
/// <para>Never composed inside a Lambda: the API reads its pair only for
/// <c>INSOLVIA_ENV=local</c> outside Lambda (ApiConfig.LocalBlobs).</para>
/// </remarks>
/// <param name="root">The directory; created on first write.</param>
/// <param name="baseUrl">Where the local blob server listens, or null to mint no URLs.</param>
public sealed class DirectoryDocumentBlobStore(string root, Uri? baseUrl = null) : IDocumentBlobStore
{
    /// <summary>The suffix of an object's content-type sidecar.</summary>
    public const string ContentTypeSuffix = ".content-type";

    /// <inheritdoc />
    public Task<string> UploadUrlAsync(string storageRef, string contentType, long byteSize, int expiresIn, CancellationToken cancellationToken = default) =>
        Task.FromResult(Url(storageRef, ("method", "PUT"), ("content_type", contentType), ("content_length", byteSize.ToString(CultureInfo.InvariantCulture)), ("expires_in", expiresIn.ToString(CultureInfo.InvariantCulture)), ("tagging", DocumentRules.UploadTag)));

    /// <inheritdoc />
    public Task<string> DownloadUrlAsync(string storageRef, int expiresIn, CancellationToken cancellationToken = default) =>
        Task.FromResult(Url(storageRef, ("method", "GET"), ("expires_in", expiresIn.ToString(CultureInfo.InvariantCulture))));

    /// <inheritdoc />
    public Task<StoredBlob?> StatAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageRef);
        if (!File.Exists(path))
        {
            return Task.FromResult<StoredBlob?>(null);
        }

        // S3's single-part etag is the MD5 of the bytes; the same here.
#pragma warning disable CA5351 // An etag, not a security boundary.
        var etag = Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(path)));
#pragma warning restore CA5351
        return Task.FromResult<StoredBlob?>(new StoredBlob(new FileInfo(path).Length, etag));
    }

    /// <inheritdoc />
    public Task ClearUploadTagAsync(string storageRef, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageRef);
        File.Delete(path);
        File.Delete(path + ContentTypeSuffix);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PutBytesAsync(string storageRef, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageRef);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path + ContentTypeSuffix, contentType, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBytesAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageRef);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    private string Url(string storageRef, params (string Key, string Value)[] terms)
    {
        _ = Resolve(storageRef);
        if (baseUrl is null)
        {
            throw new NotSupportedException("a local document directory with no blob server mints no URLs");
        }

        var query = string.Join("&", terms.Select(term => $"{Uri.EscapeDataString(term.Key)}={Uri.EscapeDataString(term.Value)}"));
        return $"{baseUrl.AbsoluteUri.TrimEnd('/')}/blobs/{Uri.EscapeDataString(storageRef)}?{query}";
    }

    private string Resolve(string storageRef)
    {
        if (string.IsNullOrEmpty(storageRef) || storageRef.EndsWith(ContentTypeSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException("a blob needs a storage ref", nameof(storageRef));
        }

        var full = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(full, storageRef));
        return path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException($"storage ref {storageRef} is outside the document directory", nameof(storageRef));
    }
}
