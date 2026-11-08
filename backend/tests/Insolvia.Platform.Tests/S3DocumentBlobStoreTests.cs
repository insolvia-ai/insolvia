using System.Net;
using System.Web;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Insolvia.Aws.Documents;
using Insolvia.Core.Documents;

namespace Insolvia.Aws.Tests;

/// <summary>
/// The S3 blob adapter (adapters/aws/document_blobs.py, whose Python tests ran
/// against moto): the presigned capabilities are SigV4, virtual-hosted, one key
/// and one verb, with the upload's type, length and tag SIGNED; HEAD's 403 and
/// 404 both read as absent; the tag clear sends an empty tag set. No network:
/// the SDK's HTTP layer is a stub that records each request.
/// </summary>
public sealed class S3DocumentBlobStoreTests
{
    private const string Bucket = "insolvia-local-case-documents-example";
    private const string Key = "cases/case-1/documents/doc-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_upload_url_is_a_sigv4_put_binding_type_length_and_the_unconfirmed_tag()
    {
        var (store, _) = Make();

        var url = new Uri(await store.UploadUrlAsync(Key, "application/pdf", 1234, 900, Ct));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("https", url.Scheme);
        Assert.Equal($"{Bucket}.s3.us-east-1.amazonaws.com", url.Host);
        Assert.Equal("/" + Key, url.AbsolutePath);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.Equal("900", query["X-Amz-Expires"]);
        Assert.Equal(["content-length", "content-type", "host", "x-amz-tagging"], query["X-Amz-SignedHeaders"]!.Split(';'));
        Assert.StartsWith("AKIAFAKEEXAMPLE/", query["X-Amz-Credential"], StringComparison.Ordinal);
        Assert.Equal("upload=unconfirmed", DocumentRules.UploadTag);
    }

    [Fact]
    public async Task A_download_url_is_a_sigv4_get_of_the_one_key_and_signs_only_the_host()
    {
        var (store, _) = Make();

        var url = new Uri(await store.DownloadUrlAsync(Key, 300, Ct));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal($"{Bucket}.s3.us-east-1.amazonaws.com", url.Host);
        Assert.Equal("/" + Key, url.AbsolutePath);
        Assert.Equal("300", query["X-Amz-Expires"]);
        Assert.Equal("host", query["X-Amz-SignedHeaders"]);
    }

    [Fact]
    public async Task Stat_reads_the_length_and_the_unquoted_etag()
    {
        var (store, http) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]) { Headers = { ContentLength = 4321 } },
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"") },
        });

        var stored = await store.StatAsync(Key, Ct);

        Assert.Equal(new StoredBlob(4321, "abc123"), stored);
        Assert.Equal(HttpMethod.Head, http.Requests.Single().Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_missing_or_denied_object_is_absent(HttpStatusCode status)
    {
        var (store, _) = Make(_ => new HttpResponseMessage(status));

        Assert.Null(await store.StatAsync(Key, Ct));
    }

    [Fact]
    public async Task Anything_else_propagates()
    {
        var (store, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<Error><Code>InvalidRequest</Code><Message>no</Message></Error>"),
        });

        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.StatAsync(Key, Ct));
    }

    [Fact]
    public async Task Clearing_the_tag_puts_an_empty_tag_set_on_the_one_key()
    {
        var (store, http) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await store.ClearUploadTagAsync(Key, Ct);

        var request = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/" + Key, request.RequestUri!.AbsolutePath);
        Assert.Contains("tagging", request.RequestUri.Query, StringComparison.Ordinal);
        var body = http.Bodies.Single();
        Assert.Contains("TagSet", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<Tag>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_is_a_delete_of_the_one_key()
    {
        var (store, http) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await store.DeleteAsync(Key, Ct);

        Assert.Equal(HttpMethod.Delete, http.Requests.Single().Method);
        Assert.Equal("/" + Key, http.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public void A_bucket_is_required()
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("a", "b"), RegionEndpoint.USEast1);
        Assert.Throws<ArgumentException>(() => new S3DocumentBlobStore(s3, ""));
    }

    private static (S3DocumentBlobStore Store, StubHttp Http) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var http = new StubHttp(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var config = S3DocumentBlobStore.ClientConfig();
        config.RegionEndpoint = RegionEndpoint.USEast1;
        config.HttpClientFactory = new StubFactory(http);
        config.MaxErrorRetry = 0;
        var s3 = new AmazonS3Client(new BasicAWSCredentials("AKIAFAKEEXAMPLE", "secretsecretsecret"), config);
        return (new S3DocumentBlobStore(s3, Bucket), http);
    }

    private sealed class StubFactory(StubHttp http) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(http, disposeHandler: false);
    }

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = respond(request);
            response.RequestMessage = request;
            response.Content ??= new ByteArrayContent([]);
            return response;
        }
    }
}
