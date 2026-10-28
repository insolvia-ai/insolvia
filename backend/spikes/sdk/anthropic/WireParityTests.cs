using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Exceptions;
using Xunit;

namespace AnthropicSpike;

/// <summary>Records every request and answers from a queue of canned responses.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
    public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Seen.Add((request, body));
        return Responses.Count > 0 ? Responses.Dequeue()() : Json(200, Ok());
    }

    public static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static string Ok(string stop = "end_turn", string text = "{\"ok\": true}") =>
        "{\"id\":\"msg_x\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\","
        + $"\"stop_reason\":\"{stop}\",\"stop_sequence\":null,"
        + $"\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}],"
        + "\"usage\":{\"input_tokens\":7,\"output_tokens\":3}}";
}

public class WireParityTests
{
    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;

    private static JsonElement Elem(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement;

    /// <summary>Deep, order-insensitive JSON equality with a readable diff on failure.</summary>
    private static void AssertJsonEqual(JsonNode expected, JsonNode actual)
    {
        if (!JsonNode.DeepEquals(expected, actual))
        {
            Assert.Fail(
                "Serialized request differs from the Python adapter's.\n--- python ---\n"
                + expected.ToJsonString(new() { WriteIndented = true })
                + "\n--- csharp ---\n" + actual.ToJsonString(new() { WriteIndented = true }));
        }
    }

    private static (RecordingHandler, Anthropic.AnthropicClient) Client()
    {
        var h = new RecordingHandler();
        return (h, ModelAdapters.NewClient("sk-test-not-a-key", new HttpClient(h)));
    }

    [Fact]
    public async Task Review_request_matches_python_body()
    {
        var fx = Fixture("review.json");
        var (h, client) = Client();

        var resp = await client.Messages.Create(ModelAdapters.ReviewRequest(
            fx["document"]!.GetValue<string>(),
            fx["system_prompt"]!.GetValue<string>(),
            Elem(fx["schema"]!)));

        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("POST", req.Method.Method);
        Assert.Equal("/v1/messages", req.RequestUri!.AbsolutePath);
        AssertJsonEqual(fx["request"]!["body"]!, JsonNode.Parse(body)!);
        Assert.Equal("claude-opus-5", fx["request"]!["body"]!["model"]!.GetValue<string>());
        // Known, benign difference: Python SDK 1.3.0 pins anthropic-version 2023-06-01;
        // C# SDK 13.3.0 sends 2026-10-21 (sampling params become a 400; we send none).
        Assert.Equal("2023-06-01", fx["request"]!["headers"]!["anthropic-version"]!.GetValue<string>());
        Assert.Equal("2026-10-21", req.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("end_turn", resp.StopReason!.ToString()!);
        // The body must carry no sampling parameters (400 on 2026-10-21).
        var sent = JsonNode.Parse(body)!.AsObject();
        Assert.DoesNotContain("temperature", sent.Select(kv => kv.Key));
        Assert.DoesNotContain("top_p", sent.Select(kv => kv.Key));
        Assert.DoesNotContain("top_k", sent.Select(kv => kv.Key));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("png")]
    public async Task Extraction_request_matches_python_body(string kind)
    {
        var fx = Fixture("extraction.json");
        var c = fx["cases"]![kind]!;
        var (h, client) = Client();

        await client.Messages.Create(ModelAdapters.ExtractionRequest(
            c["media_type"]!.GetValue<string>(),
            Convert.FromHexString(c["data_hex"]!.GetValue<string>()),
            fx["system_prompt"]!.GetValue<string>(),
            Elem(fx["schema"]!)));

        var (_, body) = Assert.Single(h.Seen);
        AssertJsonEqual(c["request"]!["body"]!, JsonNode.Parse(body)!);
        var first = JsonNode.Parse(body)!["messages"]![0]!["content"]![0]!;
        Assert.Equal(kind == "pdf" ? "document" : "image", first["type"]!.GetValue<string>());
    }

    [Fact]
    public void Client_policy_is_240s_and_one_retry_like_python()
    {
        var fx = Fixture("review.json");
        var (_, client) = Client();
        Assert.Equal(1, fx["client_max_retries"]!.GetValue<int>());
        Assert.Equal(1, client.MaxRetries);
        // Python records connect/read/write/pool = 240.0 on every request.
        Assert.Equal(240.0, fx["request"]!["timeout"]!["read"]!.GetValue<double>());
        Assert.Equal(TimeSpan.FromSeconds(240), client.Timeout);
    }

    [Fact]
    public async Task One_retry_means_two_attempts_on_5xx_then_success()
    {
        var (h, client) = Client();
        h.Responses.Enqueue(() => RecordingHandler.Json(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"x\"}}"));
        var fx = Fixture("review.json");
        var r = await client.Messages.Create(ModelAdapters.ReviewRequest("d", "s", Elem(fx["schema"]!)));
        Assert.Equal(2, h.Seen.Count);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task Persistent_5xx_gives_up_after_exactly_two_attempts()
    {
        var (h, client) = Client();
        for (var i = 0; i < 5; i++)
            h.Responses.Enqueue(() => RecordingHandler.Json(500, "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"x\"}}"));
        var fx = Fixture("review.json");
        await Assert.ThrowsAnyAsync<AnthropicApiException>(() =>
            client.Messages.Create(ModelAdapters.ReviewRequest("d", "s", Elem(fx["schema"]!))));
        Assert.Equal(2, h.Seen.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Auth_failures_are_typed_and_not_retried(int status)
    {
        var (h, client) = Client();
        h.Responses.Enqueue(() => RecordingHandler.Json(status, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"x\"}}"));
        var fx = Fixture("review.json");
        var ex = await Assert.ThrowsAnyAsync<AnthropicApiException>(() =>
            client.Messages.Create(ModelAdapters.ReviewRequest("d", "s", Elem(fx["schema"]!))));
        Assert.Single(h.Seen);
        Assert.Equal(status == 401 ? "AnthropicUnauthorizedException" : "AnthropicForbiddenException",
            ex.GetType().Name);
    }

    [Fact]
    public async Task Response_surface_matches_what_the_adapters_read()
    {
        var (h, client) = Client();
        h.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok("refusal", "")));
        var fx = Fixture("review.json");
        var r = await client.Messages.Create(ModelAdapters.ReviewRequest("d", "s", Elem(fx["schema"]!)));
        // model, stop_reason, usage.{input,output}_tokens, content[].type=="text".text
        Assert.Equal("claude-opus-5", r.Model.ToString().Trim((char)34));
        Assert.Equal(7, r.Usage.InputTokens);
        Assert.Equal(3, r.Usage.OutputTokens);
        Assert.Contains("refusal", r.StopReason!.ToString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.Content, b => b.TryPickText(out _));
    }

    [Fact]
    public async Task Timeout_applies_per_attempt_and_is_retried_once()
    {
        // Same policy as 240 s / 1 retry, scaled down so the test is fast.
        var hang = new HangingHandler();
        var client = new Anthropic.AnthropicClient
        {
            ApiKey = "sk-test-not-a-key",
            HttpClient = new HttpClient(hang),
            Timeout = TimeSpan.FromMilliseconds(200),
            MaxRetries = ModelAdapters.ClientMaxRetries,
        };
        var fx = Fixture("review.json");
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.Messages.Create(ModelAdapters.ReviewRequest("d", "s", Elem(fx["schema"]!))));
        Assert.Equal(2, hang.Attempts);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Attempts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
