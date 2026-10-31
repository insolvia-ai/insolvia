using Insolvia.Api.Logging;
using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

/// <summary>
/// app_factory.py's error handlers, body for body. The JSON shapes are the
/// ones packages/insolvia_api_client/src/exceptions.ts reads:
/// <c>{"error": "ValidationError", "fields": {…}}</c> for a per-field 400,
/// <c>{"error": "ReauthenticationRequired"}</c> on a 403 for a fresh sign-in,
/// and <c>{"error", "message"}</c> for everything else. The HTML bodies are
/// Werkzeug's, captured from Flask 3.1 / Werkzeug 3.1.
/// </summary>
public sealed class ErrorMappingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string, int, string> Mapped() => new()
    {
        { "field-validation", 400, """{"error":"ValidationError","fields":{"email":"Enter an email address.","name":"Required."}}""" },
        { "validation", 400, ErrorBody("ValidationError", "bad value") },
        { "conflict", 409, ErrorBody("ConflictError", "already done") },
        { "client-unavailable", 409, ErrorBody("ConflictError", "that client can no longer be linked to a case") },
        { "reauthenticate", 403, ErrorBody("ReauthenticationRequired", "sign in again to continue") },
        { "forbidden", 403, ErrorBody("ForbiddenError", "not yours") },
        { "forbidden-bare", 403, ErrorBody("ForbiddenError", string.Empty) },
        { "not-found", 404, ErrorBody("NotFoundError", "case not found") },
        { "api", 400, ErrorBody("ApiError", "generic") },
    };

    private static void Throwing(IEndpointRouteBuilder routes) =>
        routes.MapGet("/test/errors/{kind}", void (string kind) => throw Failure(kind));

    private static Exception Failure(string kind) => kind switch
    {
        "field-validation" => new FieldValidationException(new Dictionary<string, string> { ["name"] = "Required.", ["email"] = "Enter an email address." }),
        "validation" => new ValidationException("bad value"),
        "conflict" => new ConflictException("already done"),
        "client-unavailable" => new ClientUnavailableException("client-1"),
        "reauthenticate" => new ReauthenticationRequiredException("sign in again to continue"),
        "forbidden" => new ForbiddenException("not yours"),
        "forbidden-bare" => new ForbiddenException(),
        "not-found" => new NotFoundException("case not found"),
        "api" => new ApiException("generic"),
        "authentication" => new AuthenticationException(AuthFailureReason.Expired),
        _ => new KeyNotFoundException("secret-detail 123-45-6789"),
    };

    [Theory]
    [MemberData(nameof(Mapped))]
    public async Task A_core_exception_answers_its_status_and_body(string kind, int status, string body)
    {
        using var host = new AuthHost(factory, extraRoutes: Throwing);
        using var client = host.CreateClient();

        Json(await client.Get("/test/errors/" + kind), status, body);
        Assert.Empty(host.Logs.In(StructuredLog.Categories.AppFactory));
    }

    [Theory]
    [InlineData("boom")]
    [InlineData("authentication")]
    public async Task Anything_else_is_the_one_500_body_logged_with_its_stack(string kind)
    {
        // An AuthenticationException is not an ApiError: one
        // raised outside the auth filters is a bug, not a 401.
        using var host = new AuthHost(factory, extraRoutes: Throwing);
        using var client = host.CreateClient();

        var answer = await client.Get("/test/errors/" + kind);

        Json(answer, 500, """{"error":"InternalError","message":"request failed"}""");
        Assert.DoesNotContain("secret-detail", answer.Body, StringComparison.Ordinal);
        var logged = Assert.Single(host.Logs.In(StructuredLog.Categories.AppFactory));
        Assert.Equal(("unexpected Insolvia API failure", LogLevel.Error), (logged.Message, logged.Level));
        Assert.NotNull(logged.Exception);
    }

    [Fact]
    public async Task An_unknown_route_is_werkzeugs_404()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        var answer = await client.Get("/nope");

        Assert.Equal(404, answer.Status);
        Assert.Equal(Werkzeug(404), answer.Body);
        Assert.Equal("text/html; charset=utf-8", answer.Response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            "<!doctype html>\n<html lang=en>\n<title>404 Not Found</title>\n<h1>Not Found</h1>\n<p>The requested URL was not found on the server. If you entered the URL manually please check your spelling and try again.</p>\n",
            answer.Body);
    }

    [Fact]
    public async Task A_method_the_route_does_not_take_is_werkzeugs_405_with_allow()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/health");

        Assert.Equal(405, answer.Status);
        Assert.Equal(
            "<!doctype html>\n<html lang=en>\n<title>405 Method Not Allowed</title>\n<h1>Method Not Allowed</h1>\n<p>The method is not allowed for the requested URL.</p>\n",
            answer.Body);
        Assert.Equal(["HEAD", "OPTIONS", "GET"], answer.Response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Options_on_a_protected_route_runs_no_filter()
    {
        // Flask's automatic OPTIONS never calls the view: a preflight must not
        // meet the auth check that the real request will.
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Options, "/test/staff/cases");

        Assert.Equal(200, answer.Status);
        Assert.Equal(string.Empty, answer.Body);
        Assert.Equal(["OPTIONS", "POST"], answer.Response.Content.Headers.Allow);
        Assert.Empty(host.Logs.In(StructuredLog.Categories.Auth));
    }

    [Fact]
    public async Task Head_on_a_get_route_is_that_route_without_a_body()
    {
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Head, "/health");

        Assert.Equal(200, answer.Status);
        Assert.Equal(string.Empty, answer.Body);
        Assert.Equal("HEAD", Assert.Single(host.Logs.In(StructuredLog.Categories.Request)).Fields["method"]);
    }
}
