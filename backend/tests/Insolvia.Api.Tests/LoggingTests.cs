using System.Text.Json.Nodes;
using Insolvia.Api.Logging;
using Insolvia.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

// Translated from services/api tests/unit/test_logging.py: the formatter's
// line shape, and exactly one metadata-only line per request.
public sealed class LoggingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static JsonObject Format(Exception? exception = null, params (string Key, object? Value)[] fields) =>
        JsonNode.Parse(JsonLogFormatter.Render(
            new DateTimeOffset(2026, 10, 27, 12, 0, 0, 123, TimeSpan.Zero),
            LogLevel.Information,
            "insolvia_api.test",
            "hello world",
            fields.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)),
            exception))!.AsObject();

    [Fact]
    public void The_formatter_emits_one_json_object()
    {
        var line = Format();

        Assert.Equal("INFO", line["level"]!.GetValue<string>());
        Assert.Equal("insolvia_api.test", line["logger"]!.GetValue<string>());
        Assert.Equal("hello world", line["message"]!.GetValue<string>());
        Assert.Equal("2026-10-27T12:00:00.123Z", line["timestamp"]!.GetValue<string>());
        Assert.Equal(["timestamp", "level", "logger", "message"], line.Select(pair => pair.Key));
    }

    [Fact]
    public void The_formatter_includes_extra_fields()
    {
        var line = Format(null, ("method", "POST"), ("path", "/v1/waitlist"), ("status", 201), ("duration_ms", 1.2));

        Assert.Equal("POST", line["method"]!.GetValue<string>());
        Assert.Equal("/v1/waitlist", line["path"]!.GetValue<string>());
        Assert.Equal(201, line["status"]!.GetValue<int>());
        Assert.Equal(1.2, line["duration_ms"]!.GetValue<double>());
    }

    [Fact]
    public void The_formatter_serializes_non_json_values_as_strings() =>
        Assert.Equal(System.Text.Json.JsonValueKind.String, Format(null, ("oddity", new Uri("https://example.test/")))["oddity"]!.GetValueKind());

    [Fact]
    public void An_exception_is_its_own_field_and_extra_fields_cannot_overwrite_the_reserved_ones()
    {
        var line = Format(new InvalidOperationException("broken"), ("message", "smuggled"), ("level", "DEBUG"));

        Assert.Equal("hello world", line["message"]!.GetValue<string>());
        Assert.Equal("INFO", line["level"]!.GetValue<string>());
        Assert.Contains("InvalidOperationException: broken", line["exception"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void Levels_use_pythons_names(LogLevel level, string name) => Assert.Equal(name, JsonLogFormatter.LevelName(level));

    [Fact]
    public async Task Each_request_logs_exactly_one_metadata_line()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var token = Tokens.Make();

        var answer = await client.SendAsync(HttpMethod.Get, "/test/staff?email=ada%40lovelace-law.example", token);
        Assert.Equal(200, answer.Status);

        var line = Assert.Single(host.Logs.In(StructuredLog.Categories.Request));
        Assert.Equal("request handled", line.Message);
        Assert.Equal(["method", "path", "status", "duration_ms"], line.Fields.Keys);
        Assert.Equal("GET", line.Fields["method"]);
        Assert.Equal("/test/staff", line.Fields["path"]);
        Assert.Equal(200, line.Fields["status"]);
        Assert.IsType<double>(line.Fields["duration_ms"]);

        // GLBA: no query string, no token, no claim in ANY line.
        foreach (var entry in host.Logs.Entries)
        {
            Assert.DoesNotContain("lovelace", entry.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(token, entry.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(Ids.Alice, entry.Rendered, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/test/staff", 401)]
    [InlineData("/nope", 404)]
    public async Task Every_response_logs_one_line_with_its_final_status(string path, int status)
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        await client.Get(path);

        var line = Assert.Single(host.Logs.In(StructuredLog.Categories.Request));
        Assert.Equal((path, (object?)status), (line.Fields["path"], line.Fields["status"]));
    }

    [Fact]
    public async Task A_failed_request_logs_its_500()
    {
        using var host = new AuthHost(factory, extraRoutes: routes => routes.MapGet("/test/boom", void () => throw new InvalidOperationException("boom")));
        using var client = host.CreateClient();

        await client.Get("/test/boom");

        Assert.Equal(500, Assert.Single(host.Logs.In(StructuredLog.Categories.Request)).Fields["status"]);
    }
}
