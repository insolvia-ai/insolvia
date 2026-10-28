using Insolvia.Core.Documents;
using Insolvia.Core.Local;

namespace Insolvia.Core.Tests;

/// <summary>
/// The local directory store: the layout contract/scripts/aux-server.ts
/// shares with it (object, content-type sidecar, URL shape), and that it is
/// no looser than the memory twin about what a capability binds.
/// </summary>
public sealed class DirectoryDocumentBlobStoreTests : IDisposable
{
    private const string Ref = "cases/00000000-0000-4000-8000-0000000000ca/00000000-0000-4000-8000-0000000000d0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"insolvia-directory-blobs-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_capability_names_the_whole_ref_and_binds_the_terms_the_s3_adapter_signs()
    {
        var blobs = new DirectoryDocumentBlobStore(_root, new Uri("http://127.0.0.1:18060/"));

        var put = await blobs.UploadUrlAsync(Ref, "application/pdf", 1024, 900, Ct);
        var get = await blobs.DownloadUrlAsync(Ref, 300, Ct);

        Assert.Equal(
            $"http://127.0.0.1:18060/blobs/{Uri.EscapeDataString(Ref)}?method=PUT&content_type=application%2Fpdf&content_length=1024&expires_in=900&tagging={Uri.EscapeDataString(DocumentRules.UploadTag)}",
            put);
        Assert.Equal($"http://127.0.0.1:18060/blobs/{Uri.EscapeDataString(Ref)}?method=GET&expires_in=300", get);
    }

    [Fact]
    public async Task Without_a_server_it_mints_nothing()
    {
        var blobs = new DirectoryDocumentBlobStore(_root);

        await Assert.ThrowsAsync<NotSupportedException>(() => blobs.DownloadUrlAsync(Ref, 300, Ct));
    }

    [Fact]
    public async Task A_write_lands_with_its_content_type_beside_it_and_reads_back()
    {
        var blobs = new DirectoryDocumentBlobStore(_root);

        await blobs.PutBytesAsync(Ref, "%PDF-1.7"u8.ToArray(), "application/pdf", Ct);

        Assert.Equal("%PDF-1.7"u8.ToArray(), await blobs.GetBytesAsync(Ref, Ct));
        Assert.Equal("application/pdf", await File.ReadAllTextAsync(Path.Combine(_root, Ref) + DirectoryDocumentBlobStore.ContentTypeSuffix, Ct));
        var stat = await blobs.StatAsync(Ref, Ct);
        Assert.Equal(8, stat?.ByteSize);

        await blobs.DeleteAsync(Ref, Ct);
        Assert.Null(await blobs.StatAsync(Ref, Ct));
        Assert.False(File.Exists(Path.Combine(_root, Ref) + DirectoryDocumentBlobStore.ContentTypeSuffix));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("cases/x.content-type")]
    [InlineData("")]
    public async Task A_ref_outside_the_directory_or_naming_a_sidecar_is_refused(string storageRef)
    {
        var blobs = new DirectoryDocumentBlobStore(_root, new Uri("http://127.0.0.1:18060"));

        await Assert.ThrowsAsync<ArgumentException>(() => blobs.UploadUrlAsync(storageRef, "application/pdf", 1, 60, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => blobs.GetBytesAsync(storageRef, Ct));
    }
}
