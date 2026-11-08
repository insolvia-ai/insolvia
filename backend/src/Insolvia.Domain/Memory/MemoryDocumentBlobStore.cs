using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Insolvia.Core.Documents;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>One capability the memory blob store handed out — the terms the real one binds into a signature.</summary>
public sealed record MintedUrl(string Method, string StorageRef, int ExpiresIn, string? ContentType = null, long? ByteSize = null, string? Tag = null);

/// <summary>
/// The in-memory <see cref="IDocumentBlobStore"/>. It mints <c>memory+s3://</c>
/// URLs no HTTP client can fetch — the honest shape: there is no local object
/// store. NO LOOSER THAN THE REAL ONE where a fake can be: every term the real
/// one binds into a signature is in the URL and in <see cref="Minted"/>, so a
/// caller that forgot one fails a test.
/// </summary>
public sealed class MemoryDocumentBlobStore : IDocumentBlobStore
{
    private readonly Lock _gate = new();
    private readonly List<MintedUrl> _minted = [];
    private readonly List<string> _deleted = [];
    private readonly List<string> _cleared = [];
    private readonly Dictionary<string, StoredBlob> _objects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tagged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _contentTypes = new(StringComparer.Ordinal);

    /// <summary>Every capability minted, in order.</summary>
    public IReadOnlyList<MintedUrl> Minted => Snapshot(() => _minted.ToList());

    /// <summary>Every delete, in order.</summary>
    public IReadOnlyList<string> Deleted => Snapshot(() => _deleted.ToList());

    /// <summary>Every tag clear, in order, repeats included.</summary>
    public IReadOnlyList<string> Cleared => Snapshot(() => _cleared.ToList());

    /// <summary>The objects that exist.</summary>
    public IReadOnlyDictionary<string, StoredBlob> Objects => Snapshot(() => new Dictionary<string, StoredBlob>(_objects, StringComparer.Ordinal));

    /// <summary>The objects still carrying <c>upload=unconfirmed</c> — what the lifecycle rule would reap.</summary>
    public IReadOnlySet<string> Tagged => Snapshot(() => new HashSet<string>(_tagged, StringComparer.Ordinal));

    /// <summary>The content type each server-side write declared.</summary>
    public IReadOnlyDictionary<string, string> ContentTypes => Snapshot(() => new Dictionary<string, string>(_contentTypes, StringComparer.Ordinal));

    /// <summary>
    /// Stand in for the client's PUT through a minted capability — NOT part of
    /// the port (the service never writes an upload). Tags what it writes, as
    /// every real PUT through our URLs does.
    /// </summary>
    public void AcceptUpload(string storageRef, long byteSize, string etag = "00000000000000000000000000000000", byte[]? content = null)
    {
        lock (_gate)
        {
            _objects[storageRef] = new StoredBlob(byteSize, etag);
            _tagged.Add(storageRef);
            if (content is not null)
            {
                _contents[storageRef] = content;
            }
        }
    }

    /// <inheritdoc />
    public Task<string> UploadUrlAsync(string storageRef, string contentType, long byteSize, int expiresIn, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _minted.Add(new MintedUrl("PUT", storageRef, expiresIn, contentType, byteSize, DocumentRules.UploadTag));
        }

        return Task.FromResult(Url(storageRef, ("method", "PUT"), ("content_type", contentType), ("content_length", byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("expires_in", expiresIn.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("tagging", DocumentRules.UploadTag)));
    }

    /// <inheritdoc />
    public Task<string> DownloadUrlAsync(string storageRef, int expiresIn, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _minted.Add(new MintedUrl("GET", storageRef, expiresIn));
        }

        return Task.FromResult(Url(storageRef, ("method", "GET"), ("expires_in", expiresIn.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <inheritdoc />
    public Task<StoredBlob?> StatAsync(string storageRef, CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot(() => _objects.GetValueOrDefault(storageRef)));

    /// <inheritdoc />
    public Task ClearUploadTagAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _cleared.Add(storageRef);
            _tagged.Remove(storageRef);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string storageRef, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _deleted.Add(storageRef);
            _objects.Remove(storageRef);
            _tagged.Remove(storageRef);
            _contents.Remove(storageRef);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]?> GetBytesAsync(string storageRef, CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot(() => _contents.GetValueOrDefault(storageRef)));

    /// <inheritdoc />
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 is what the object store's etag for a single-part put IS — identity, not security.")]
    public Task PutBytesAsync(string storageRef, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storageRef))
        {
            throw new ArgumentException("a blob write needs a storage ref", nameof(storageRef));
        }

        var bytes = content.ToArray();
        lock (_gate)
        {
            _objects[storageRef] = new StoredBlob(bytes.Length, Convert.ToHexStringLower(MD5.HashData(bytes)));
            _contents[storageRef] = bytes;
            _contentTypes[storageRef] = contentType;
        }

        return Task.CompletedTask;
    }

    private static string Url(string storageRef, params (string Key, string Value)[] terms)
    {
        if (string.IsNullOrEmpty(storageRef))
        {
            // A key is always two server-minted uuids; an empty one is a bug here.
            throw new ArgumentException("a blob URL needs a storage ref", nameof(storageRef));
        }

        var query = string.Join("&", terms.Select(term => $"{Uri.EscapeDataString(term.Key)}={Uri.EscapeDataString(term.Value)}"));
        return $"memory+s3://documents/{Uri.EscapeDataString(storageRef).Replace("%2F", "/", StringComparison.Ordinal)}?{query}";
    }

    private T Snapshot<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }
}
