using Insolvia.Core.Documents;

namespace Insolvia.Core.Ports;

/// <summary>
/// Mints the short-lived, single-object capabilities that move the bytes, and
/// deletes objects (Python's <c>DocumentBlobStore</c>; the one port that reaches
/// the object store). Every URL is minted for ONE object and ONE verb, with the
/// validated content type and size BOUND into it; nothing here takes a bucket,
/// a prefix or a method from a caller.
/// </summary>
public interface IDocumentBlobStore
{
    /// <summary>A URL that accepts exactly one PUT of exactly this object; type and size are bound, not remembered.</summary>
    Task<string> UploadUrlAsync(string storageRef, string contentType, long byteSize, int expiresIn, CancellationToken cancellationToken = default);

    /// <summary>A URL that serves exactly this object, once it exists.</summary>
    Task<string> DownloadUrlAsync(string storageRef, int expiresIn, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the store reports about the object, or null when it is not there —
    /// the ONLY way the service learns an upload landed. MUST NOT distinguish
    /// "missing" from "not allowed".
    /// </summary>
    Task<StoredBlob?> StatAsync(string storageRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the <c>upload=unconfirmed</c> tag — THE ONLY THING THAT MAKES THE
    /// REAPER SAFE for a real document. Idempotent.
    /// </summary>
    Task ClearUploadTagAsync(string storageRef, CancellationToken cancellationToken = default);

    /// <summary>Delete the object. Idempotent.</summary>
    Task DeleteAsync(string storageRef, CancellationToken cancellationToken = default);

    /// <summary>Write an object this SERVICE produced (the packet worker's), with the bucket's required encryption.</summary>
    Task PutBytesAsync(string storageRef, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Read one object whole — the extraction worker's read, never a request handler's. Null when absent.</summary>
    Task<byte[]?> GetBytesAsync(string storageRef, CancellationToken cancellationToken = default);
}
