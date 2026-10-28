using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;
using Insolvia.Worker.Anthropic;
using Insolvia.Worker.Workers;

namespace Insolvia.Worker.Tests;

/// <summary>Records every request and answers from a queue of canned responses — nothing leaves the process.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

    public Queue<Func<HttpResponseMessage>> Responses { get; } = new();

    public static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Ok(string stop = "end_turn", string text = "{\"findings\": []}") =>
        "{\"id\":\"msg_x\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\","
        + $"\"stop_reason\":\"{stop}\",\"stop_sequence\":null,"
        + $"\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}],"
        + "\"usage\":{\"input_tokens\":7,\"output_tokens\":3}}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return Responses.Count > 0 ? Responses.Dequeue()() : Json(200, Ok());
    }
}

/// <summary>
/// The Anthropic seam pinned to Python's wire (research
/// dotnet-mcp-and-anthropic-sdks-2026-10-27 § 3): the production adapters'
/// request bodies are DEEP-EQUAL to the bodies the Python adapters sent
/// (captured by spikes/sdk/anthropic/capture_python_requests.py) — which pins
/// the review prompt and schema and the credit-report prompt and schema to
/// Python's byte for byte. Plus the client policy and the failure split.
/// </summary>
public sealed class AnthropicWireParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AnthropicFixtures", name)))!;

    private static (RecordingHandler Handler, AnthropicReviewModel Review, AnthropicExtractionModel Extraction) Models()
    {
        var handler = new RecordingHandler();
        var client = AnthropicPolicy.NewClient("sk-test-not-a-key", new HttpClient(handler));
        var log = new RecordingJobLog();
        return (handler, new AnthropicReviewModel(client, log), new AnthropicExtractionModel(client, log));
    }

    private static void AssertJsonEqual(JsonNode expected, JsonNode actual)
    {
        if (!JsonNode.DeepEquals(expected, actual))
        {
            Assert.Fail("The request body differs from the Python adapter's.\n--- python ---\n"
                + expected.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                + "\n--- csharp ---\n" + actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public async Task The_review_request_is_pythons_body()
    {
        var fixture = Fixture("review.json");
        var (handler, review, _) = Models();

        var result = await review.ReviewAsync(fixture["document"]!.GetValue<string>(), Ct);

        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("POST", request.Method.Method);
        Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
        AssertJsonEqual(fixture["request"]!["body"]!, JsonNode.Parse(body)!);
        Assert.Equal("claude-opus-5", result.Model);
        Assert.Equal("{\"findings\":[]}", result.Raw.ToJsonString());
    }

    [Fact]
    public void The_prompt_and_schema_constants_are_pythons()
    {
        var review = Fixture("review.json");
        Assert.Equal(PetitionReview.SystemPrompt, review["system_prompt"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(review["schema"], PetitionReview.OutputSchema()));
        Assert.Equal(AnthropicPolicy.Model, review["model_constant"]!.GetValue<string>());

        var extraction = Fixture("extraction.json");
        Assert.Equal(DocumentExtraction.CreditReportSystemPrompt, extraction["system_prompt"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(extraction["schema"], DocumentExtraction.CreditReportSchema()));
    }

    [Theory]
    [InlineData("pdf", "document")]
    [InlineData("png", "image")]
    public async Task The_extraction_request_is_pythons_body(string kind, string firstBlock)
    {
        var fixture = Fixture("extraction.json");
        var shape = fixture["cases"]![kind]!;
        var (handler, _, extraction) = Models();
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok(text: "{\"creditors\": []}")));

        await extraction.ExtractAsync(
            new ExtractionRequest(
                shape["media_type"]!.GetValue<string>(),
                Convert.FromHexString(shape["data_hex"]!.GetValue<string>()),
                DocumentExtraction.CreditReportSystemPrompt,
                DocumentExtraction.CreditReportSchema()),
            Ct);

        var (_, body) = Assert.Single(handler.Seen);
        AssertJsonEqual(shape["request"]!["body"]!, JsonNode.Parse(body)!);
        Assert.Equal(firstBlock, JsonNode.Parse(body)!["messages"]![0]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_request_carries_a_sampling_parameter()
    {
        // anthropic-version 2026-10-21 (what C# SDK 13.3.0 sends; Python sent
        // 2023-06-01) refuses temperature/top_p/top_k with a 400.
        var (handler, review, extraction) = Models();
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok()));
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok(text: "{\"employer\": {}, \"pay_periods\": []}")));
        await review.ReviewAsync("d", Ct);
        await extraction.ExtractAsync(new ExtractionRequest("image/jpeg", new byte[] { 1, 2 }, DocumentExtraction.PayStubSystemPrompt, DocumentExtraction.PayStubSchema()), Ct);
        foreach (var (request, body) in handler.Seen)
        {
            var sent = JsonNode.Parse(body)!.AsObject();
            Assert.DoesNotContain("temperature", sent.Select(pair => pair.Key));
            Assert.DoesNotContain("top_p", sent.Select(pair => pair.Key));
            Assert.DoesNotContain("top_k", sent.Select(pair => pair.Key));
            Assert.DoesNotContain("thinking", sent.Select(pair => pair.Key));
            Assert.Equal("2026-10-21", request.Headers.GetValues("anthropic-version").Single());
        }
    }

    [Fact]
    public void The_client_policy_is_240_seconds_and_one_retry_like_python()
    {
        var fixture = Fixture("review.json");
        var client = AnthropicPolicy.NewClient("sk-test-not-a-key");
        Assert.Equal(fixture["client_max_retries"]!.GetValue<int>(), client.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(fixture["request"]!["timeout"]!["read"]!.GetValue<double>()), client.Timeout);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_once_then_propagates()
    {
        var (handler, review, _) = Models();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            handler.Responses.Enqueue(() => RecordingHandler.Json(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"x\"}}"));
        }

        var error = await Assert.ThrowsAnyAsync<Exception>(() => review.ReviewAsync("d", Ct));
        Assert.IsNotType<JobException>(error);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_rejected_key_is_a_deterministic_not_configured(int status)
    {
        var (handler, review, extraction) = Models();
        handler.Responses.Enqueue(() => RecordingHandler.Json(status, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"x\"}}"));
        handler.Responses.Enqueue(() => RecordingHandler.Json(status, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"x\"}}"));
        Assert.Equal("not_configured", (await Assert.ThrowsAsync<JobException>(() => review.ReviewAsync("d", Ct))).Category);
        Assert.Equal("not_configured", (await Assert.ThrowsAsync<JobException>(() => extraction.ExtractAsync(new ExtractionRequest("application/pdf", new byte[] { 1 }, "s", new JsonObject()), Ct))).Category);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task A_refusal_is_deterministic_for_both_adapters()
    {
        var (handler, review, extraction) = Models();
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok("refusal", string.Empty)));
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok("refusal", string.Empty)));
        Assert.Equal("model_refused", (await Assert.ThrowsAsync<JobException>(() => review.ReviewAsync("d", Ct))).Category);
        Assert.Equal("model_refused", (await Assert.ThrowsAsync<JobException>(() => extraction.ExtractAsync(new ExtractionRequest("application/pdf", new byte[] { 1 }, "s", new JsonObject()), Ct))).Category);
    }

    [Fact]
    public async Task Max_tokens_is_a_retry_for_the_review_and_deterministic_for_extraction()
    {
        var (handler, review, extraction) = Models();
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok("max_tokens", "{\"findings\": [")));
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok("max_tokens", "{\"creditors\": [")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.ReviewAsync("d", Ct));
        Assert.Equal("too_dense", (await Assert.ThrowsAsync<JobException>(() => extraction.ExtractAsync(new ExtractionRequest("application/pdf", new byte[] { 1 }, "s", new JsonObject()), Ct))).Category);
    }

    [Fact]
    public async Task The_call_is_logged_as_metadata_only()
    {
        var handler = new RecordingHandler();
        var log = new RecordingJobLog();
        var review = new AnthropicReviewModel(AnthropicPolicy.NewClient("sk-test-not-a-key", new HttpClient(handler)), log);
        handler.Responses.Enqueue(() => RecordingHandler.Json(200, RecordingHandler.Ok(text: "{\"findings\": [{\"severity\": \"high\", \"category\": \"other\", \"form\": \"packet\", \"line\": \"\", \"message\": \"SECRET CASE FACT\"}]}")));
        await review.ReviewAsync("SECRET DOCUMENT", Ct);
        var record = Assert.Single(log.Records);
        Assert.Equal("review model call finished", record.Message);
        Assert.Equal(["input_tokens", "model", "output_tokens", "stop_reason"], record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("end_turn", record.Fields["stop_reason"]);
    }
}
