using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests.Support;

internal static class HttpAssert
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<(int Status, string Body, HttpResponseMessage Response)> SendAsync(
        this HttpClient client,
        HttpMethod method,
        string path,
        string? bearer = null,
        string? authorization = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (method == HttpMethod.Post || method == HttpMethod.Patch || method == HttpMethod.Put)
        {
            request.Content = new StringContent("{}", MediaTypeHeaderValue.Parse("application/json"));
        }

        var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return ((int)response.StatusCode, body, response);
    }

    public static Task<(int Status, string Body, HttpResponseMessage Response)> Get(this HttpClient client, string path, string? bearer = null) =>
        client.SendAsync(HttpMethod.Get, path, bearer);

    /// <summary>test_auth.py's <c>assert_unauthorized</c>: the status AND the one body.</summary>
    public static void Unauthorized((int Status, string Body, HttpResponseMessage Response) answer)
    {
        Json(answer, 401, """{"error":"Unauthorized","message":"authentication required"}""");
    }

    public static void Json((int Status, string Body, HttpResponseMessage Response) answer, int status, string body)
    {
        Assert.Equal(status, answer.Status);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(answer.Body)), $"expected {body}\nactual   {answer.Body}");
        Assert.Equal("application/json", answer.Response.Content.Headers.ContentType?.MediaType);
    }

    public static string ErrorBody(string error, string message) =>
        $$"""{"error":"{{error}}","message":"{{message}}"}""";
}

/// <summary>A seeded <see cref="AuthHost"/> shared by one test class.</summary>
public sealed class SeededHost : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();

    internal AuthHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Host = await new AuthHost(_factory).SeedAsync();

    public async ValueTask DisposeAsync()
    {
        Host.Dispose();
        await _factory.DisposeAsync();
    }
}
