using Insolvia.Api.Tests.Support;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_creditor_matrix_route.py: <c>GET …/creditor-matrix</c> — a file or a
/// problem list, 200 either way, in the case's court's own format (#360),
/// authorised like the creditor listing it projects. The formatting itself
/// is the Engines' CreditorMatrixTests' (and the court goldens').
/// </summary>
public sealed class CreditorMatrixRouteTests : IAsyncLifetime, IDisposable
{
    private const string Typed = """{"source": "staff_typed"}""";
    private const string MailableCreditor = $$$"""{"name": "Example Bank", "address": {"line1": "PO Box 15168", "city": "Wilmington", "state": "DE", "postal_code": "19850"}, "provenance": {"name": {{{Typed}}}, "address.line1": {{{Typed}}}, "address.city": {{{Typed}}}, "address.state": {{{Typed}}}, "address.postal_code": {{{Typed}}}}}""";

    private readonly AnalysisWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/creditor-matrix", subject: null)).Status);
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await Matrix("no-such-case")).Status);
    }

    [Fact]
    public async Task Another_firms_matrix_is_the_same_404_as_no_case()
    {
        var caseId = await OpenAsync();
        await AddCreditorAsync(caseId);

        var foreign = await Matrix(caseId, Carol);

        Assert.Equal(404, foreign.Status);
        Assert.Equal((await Matrix("no-such-case")).Body, foreign.Body);
    }

    // The case is in N.D. Fla. (`flnb`), whose verified instructions put a
    // comma after the city — the registry's format reaching the file (#360).
    [Fact]
    public async Task A_mailable_creditor_list_produces_the_file()
    {
        var caseId = await OpenAsync();
        await AddCreditorAsync(caseId);

        var answer = await Matrix(caseId);

        Assert.Equal(200, answer.Status);
        Assert.Equal("application/json", answer.ContentType);
        Assert.Equal("""{"content":"Example Bank\r\nPO Box 15168\r\nWilmington, DE 19850\r\n","creditorCount":1,"duplicatesOmitted":0,"fileName":"creditor-matrix.txt","problems":[]}""", answer.Body);
    }

    [Fact]
    public async Task An_unmailable_creditor_answers_problems_instead_of_a_file()
    {
        var caseId = await OpenAsync();
        var creditorId = await AddCreditorAsync(caseId, $$$"""{"name": "Example Bank", "address": {"line1": "PO Box 15168"}, "provenance": {"name": {{{Typed}}}, "address.line1": {{{Typed}}}}}""");

        var body = (await Matrix(caseId)).Object;

        Assert.False(body.ContainsKey("content"));
        Assert.Equal(0, body["creditorCount"]!.GetValue<int>());
        var reported = body["problems"]!.AsArray().Select(p => (p!.AsObject().Str("creditorId"), p.AsObject().Str("field"))).ToHashSet();
        Assert.Equal(
            new HashSet<(string?, string?)> { (creditorId, "address.city"), (creditorId, "address.state"), (creditorId, "address.postal_code") },
            reported);
    }

    [Fact]
    public async Task A_case_with_no_creditors_answers_the_case_level_problem()
    {
        var caseId = await OpenAsync();

        var answer = await Matrix(caseId);

        Assert.False(answer.Object.ContainsKey("content"));
        // Flask's ensure_ascii: the em dash travels as \u2014.
        Assert.Contains(
            """problems":[{"field":"creditors","message":"The case has no creditors \u2014 a matrix must list every creditor before it can be filed."}]""",
            answer.Body,
            StringComparison.Ordinal);
    }

    // "creditor-matrix" shares the /v1/cases/{id}/{segment} shape with the
    // generic collection routes, which answer 404 for unknown collections —
    // so a 200 proves the literal route won; and {collection} still owns
    // the real ones.
    [Fact]
    public async Task The_static_url_is_not_swallowed_by_the_collection_routes()
    {
        var caseId = await OpenAsync();
        await AddCreditorAsync(caseId);

        Assert.Equal(200, (await Matrix(caseId)).Status);
        var listed = await _world.Get($"/v1/cases/{caseId}/creditors");
        Assert.Equal(200, listed.Status);
        Assert.Single(listed.Object["creditors"]!.AsArray());
    }

    // GLBA: the case id and whether a file was produced — never a name, a
    // count, or a problem message.
    [Fact]
    public async Task The_log_line_says_whether_a_file_was_produced_and_nothing_about_the_creditors()
    {
        var caseId = await OpenAsync();
        await AddCreditorAsync(caseId);

        await Matrix(caseId);

        var entry = Assert.Single(_world.World.World.Host.Logs.In("insolvia_api.api.routes.creditor_matrix"));
        Assert.Equal("creditor matrix generated", entry.Message);
        Assert.Equal(caseId, entry.Fields["case_id"]);
        Assert.Equal(true, entry.Fields["generated"]);
        Assert.DoesNotContain("Example Bank", entry.Rendered, StringComparison.Ordinal);
    }

    private async Task<string> AddCreditorAsync(string caseId, string body = MailableCreditor)
    {
        var answer = await _world.Post($"/v1/cases/{caseId}/creditors", body);
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Str("id")!;
    }

    private Task<string> OpenAsync() => _world.OpenCaseAsync(court: "flnb", division: "tallahassee");

    private Task<Answer> Matrix(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/creditor-matrix", subject);
}
