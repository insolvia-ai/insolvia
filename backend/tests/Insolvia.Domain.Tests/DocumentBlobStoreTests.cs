using Insolvia.Core.Documents;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

/// <summary>
/// The memory blob store's contract. test_document_blobs.py is the S3
/// adapter's (presigned terms, SigV4, the 403-as-absent read) and moves with
/// that adapter to 18.12; these pin what the memory twin promises a route.
/// </summary>
public sealed class DocumentBlobStoreTests
{
    private const string Ref = "cases/00000000-0000-4000-8000-0000000000ca/00000000-0000-4000-8000-0000000000d0";

    private readonly MemoryDocumentBlobStore _blobs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upload_url_binds_every_term_the_server_decided()
    {
        var url = await _blobs.UploadUrlAsync(Ref, "application/pdf", 1024, 900, Ct);
        var minted = Assert.Single(_blobs.Minted);
        Assert.Equal(new MintedUrl("PUT", Ref, 900, "application/pdf", 1024, DocumentRules.UploadTag), minted);
        Assert.Contains("content_length=1024", url, StringComparison.Ordinal);
        Assert.StartsWith("memory+s3://documents/cases/", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minting_is_not_an_upload_and_an_upload_is_tagged_until_confirmed()
    {
        await _blobs.UploadUrlAsync(Ref, "application/pdf", 1024, 900, Ct);
        Assert.Null(await _blobs.StatAsync(Ref, Ct));
        _blobs.AcceptUpload(Ref, 1000);
        Assert.Equal(1000, (await _blobs.StatAsync(Ref, Ct))!.ByteSize);
        Assert.Contains(Ref, _blobs.Tagged);
        await _blobs.ClearUploadTagAsync(Ref, Ct);
        await _blobs.ClearUploadTagAsync(Ref, Ct);
        Assert.DoesNotContain(Ref, _blobs.Tagged);
        Assert.Equal([Ref, Ref], _blobs.Cleared);
    }

    [Fact]
    public async Task Delete_is_idempotent_and_takes_the_bytes_with_it()
    {
        await _blobs.PutBytesAsync(Ref, new byte[] { 1, 2, 3 }, "application/zip", Ct);
        Assert.Equal(new StoredBlob(3, "5289df737df57326fcdd22597afb1fac"), await _blobs.StatAsync(Ref, Ct));
        Assert.Equal([1, 2, 3], await _blobs.GetBytesAsync(Ref, Ct));
        await _blobs.DeleteAsync(Ref, Ct);
        await _blobs.DeleteAsync(Ref, Ct);
        Assert.Null(await _blobs.StatAsync(Ref, Ct));
        Assert.Null(await _blobs.GetBytesAsync(Ref, Ct));
    }

    [Fact]
    public async Task A_url_without_an_object_is_refused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _blobs.DownloadUrlAsync(string.Empty, 60, Ct));
}
