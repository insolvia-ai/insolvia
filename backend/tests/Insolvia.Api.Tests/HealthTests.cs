using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests;

// Ported from services/api/tests/unit/test_health.py, case for case.
public sealed class HealthTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_reports_service_identity()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            """{"environment":"local","service":"insolvia-api","status":"ok","version":"0.1.0"}""",
            body);
    }

    [Fact]
    public async Task Health_reports_the_configured_environment()
    {
        using var client = factory
            .WithWebHostBuilder(host => host.UseSetting("INSOLVIA_ENV", "staging"))
            .CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("staging", body.RootElement.GetProperty("environment").GetString());
    }

    [Fact]
    public void An_unknown_environment_refuses_to_start()
    {
        using var misconfigured = factory.WithWebHostBuilder(host => host.UseSetting("INSOLVIA_ENV", "prod"));

        var error = Assert.Throws<ArgumentException>(() => misconfigured.CreateClient());

        Assert.Contains("INSOLVIA_ENV must be one of local, staging, production", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_route_stays_a_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
