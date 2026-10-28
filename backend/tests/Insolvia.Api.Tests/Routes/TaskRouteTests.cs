using Insolvia.Api.Tests.Support;
using Insolvia.Core.Firms;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_task_routes.py: case tasks — tenancy, the <c>tasks</c> gate, the
/// assignee rule, <c>overdue</c>, the PATCH's named-fields-only edit, and
/// <c>/v1/me/tasks</c> across the cases the caller may reach.
/// </summary>
public sealed class TaskRouteTests : IAsyncLifetime, IDisposable
{
    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("POST", "/v1/cases/any-id/tasks")]
    [InlineData("GET", "/v1/cases/any-id/tasks")]
    [InlineData("GET", "/v1/cases/any-id/tasks/any-task")]
    [InlineData("PATCH", "/v1/cases/any-id/tasks/any-task")]
    [InlineData("DELETE", "/v1/cases/any-id/tasks/any-task")]
    [InlineData("GET", "/v1/me/tasks")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);
    }

    [Fact]
    public async Task Another_firm_writes_nothing_and_reads_nothing_and_an_id_does_not_cross_cases()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var other = await _world.OpenCaseIdAsync();
        var taskId = (await Add(caseId)).Str("id");

        Assert.Equal(404, (await Add(caseId, subject: Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/tasks", Carol)).Status);
        Assert.Equal("""{"error":"NotFoundError","message":"task not found"}""", (await _world.Get($"/v1/cases/{other}/tasks/{taskId}")).Body);
    }

    [Fact]
    public async Task View_only_cannot_create_and_hidden_cannot_list()
    {
        var caseId = await _world.OpenCaseIdAsync();

        await RestrictAliceAsync(PermissionLevel.ViewOnly);
        Assert.Equal(403, (await Add(caseId)).Status);
        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/tasks")).Status);

        await RestrictAliceAsync(PermissionLevel.Hidden);
        Assert.Equal(403, (await _world.Get($"/v1/cases/{caseId}/tasks")).Status);
    }

    [Fact]
    public async Task Creating_a_task_answers_201_with_the_stored_record()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await Add(caseId, """{"subject": "Collect pay stubs", "description": "Last six months", "dueDate": "2099-10-01", "assigneeSubject": "00000000-0000-4000-8000-00000000da4a", "formSeries": "b106d"}""");

        Assert.Equal(201, answer.Status);
        Assert.Equal(("Collect pay stubs", "2099-10-01", Dana, Alice, caseId), (answer.Str("subject"), answer.Str("dueDate"), answer.Str("assigneeSubject"), answer.Str("createdBy"), answer.Str("caseId")));
        Assert.False(answer.Object["done"]!.GetValue<bool>());
        Assert.False(answer.Object["overdue"]!.GetValue<bool>());
        Assert.Equal(["assigneeSubject", "caseId", "createdAt", "createdBy", "description", "done", "dueDate", "formSeries", "id", "overdue", "subject", "updatedAt"], Answer.Keys(answer.Json));
    }

    [Fact]
    public async Task Subject_is_the_only_required_field()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var bare = await Add(caseId, """{"subject": "Just this"}""");

        Assert.Equal(201, bare.Status);
        Assert.Equal(["caseId", "createdAt", "createdBy", "done", "id", "overdue", "subject", "updatedAt"], Answer.Keys(bare.Json));
        Assert.Contains("subject", (await Add(caseId, """{"description": "no subject"}""")).FieldErrors);
    }

    [Fact]
    public async Task An_assignee_must_be_an_active_user_of_the_callers_firm()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(["assigneeSubject"], (await Add(caseId, $$"""{"subject": "x", "assigneeSubject": "{{Carol}}"}""")).FieldErrors);
        Assert.Equal(["assigneeSubject"], (await Add(caseId, $$"""{"subject": "x", "assigneeSubject": "{{Erin}}"}""")).FieldErrors);
        Assert.Equal(201, (await Add(caseId, $$"""{"subject": "x", "assigneeSubject": "{{Bob}}"}""")).Status);
    }

    [Fact]
    public async Task An_overdue_task_is_marked_overdue()
    {
        Assert.True((await Add(await _world.OpenCaseIdAsync(), """{"subject": "Old task", "dueDate": "2000-01-01"}""")).Object["overdue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Listing_is_creation_order_and_an_unknown_task_is_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var first = (await Add(caseId, """{"subject": "first"}""")).Str("id");
        var second = (await Add(caseId, """{"subject": "second"}""")).Str("id");

        Assert.Equal([first, second], (await _world.Get($"/v1/cases/{caseId}/tasks")).Object["tasks"]!.AsArray().Select(t => t!.AsObject().Str("id")));
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/tasks/no-such-task")).Status);
    }

    [Fact]
    public async Task Patch_edits_only_the_named_fields_and_null_clears()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var taskId = (await Add(caseId, """{"subject": "x", "dueDate": "2099-10-01"}""")).Str("id");
        var url = $"/v1/cases/{caseId}/tasks/{taskId}";

        var renamed = await _world.Patch(url, """{"subject": "Renamed"}""");
        Assert.Equal((200, "Renamed", "2099-10-01"), (renamed.Status, renamed.Str("subject"), renamed.Str("dueDate")));
        Assert.Equal(400, (await _world.Patch(url, "{}")).Status);
        Assert.False((await _world.Patch(url, """{"dueDate": null}""")).Object.ContainsKey("dueDate"));
    }

    [Fact]
    public async Task Completing_stamps_completed_at_and_reopening_clears_it()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var taskId = (await Add(caseId)).Str("id");
        var url = $"/v1/cases/{caseId}/tasks/{taskId}";

        var done = await _world.Patch(url, """{"done": true}""");
        Assert.True(done.Object["done"]!.GetValue<bool>());
        Assert.NotNull(done.Str("completedAt"));

        var reopened = await _world.Patch(url, """{"done": false}""");
        Assert.False(reopened.Object.ContainsKey("completedAt"));
    }

    [Fact]
    public async Task Reassigning_to_a_colleague_or_nobody_succeeds_and_to_a_stranger_is_refused()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var url = $"/v1/cases/{caseId}/tasks/{(await Add(caseId)).Str("id")}";

        Assert.Equal(Bob, (await _world.Patch(url, $$"""{"assigneeSubject": "{{Bob}}"}""")).Str("assigneeSubject"));
        Assert.Equal(["assigneeSubject"], (await _world.Patch(url, $$"""{"assigneeSubject": "{{Carol}}"}""")).FieldErrors);
        Assert.False((await _world.Patch(url, """{"assigneeSubject": null}""")).Object.ContainsKey("assigneeSubject"));
        Assert.Equal(404, (await _world.Patch($"/v1/cases/{caseId}/tasks/no-such-task", """{"subject": "x"}""")).Status);
    }

    [Fact]
    public async Task Deleting_removes_it_once()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var url = $"/v1/cases/{caseId}/tasks/{(await Add(caseId)).Str("id")}";

        Assert.Equal(204, (await _world.Delete(url)).Status);
        Assert.Equal(404, (await _world.Delete(url)).Status);
        Assert.Equal("""{"tasks":[]}""", (await _world.Get($"/v1/cases/{caseId}/tasks")).Body);
    }

    // ── /v1/me/tasks ────────────────────────────────────────────────

    [Fact]
    public async Task Assigned_to_me_lists_my_tasks_across_reachable_cases()
    {
        var first = await _world.OpenCaseIdAsync();
        var second = await _world.OpenCaseIdAsync();
        await Add(first, $$"""{"subject": "In case 1", "assigneeSubject": "{{Alice}}"}""");
        await Add(second, $$"""{"subject": "In case 2", "assigneeSubject": "{{Alice}}"}""");
        await Add(second, $$"""{"subject": "Not mine", "assigneeSubject": "{{Bob}}"}""");

        var subjects = await MineAsync();

        Assert.Equal(["In case 1", "In case 2"], subjects.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Assigned_to_me_never_leaks_an_unreachable_case_or_another_firm_and_reaches_it_once_linked()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId, $$"""{"subject": "Bob's, unreachable", "assigneeSubject": "{{Bob}}"}""");
        await _world.OpenCaseAsync(Carol);

        Assert.Empty(await MineAsync(Bob));
        Assert.Empty(await MineAsync(Carol, "?scope=firm"));

        await _world.Put($"/v1/cases/{caseId}/assignees/{Bob}", null);
        Assert.Equal(["Bob's, unreachable"], await MineAsync(Bob));
    }

    [Fact]
    public async Task Scope_firm_includes_a_colleagues_tasks_but_still_respects_reachability()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId, $$"""{"subject": "Dana's", "assigneeSubject": "{{Dana}}"}""");
        await Add(caseId, """{"subject": "Nobody's"}""");

        Assert.Equal(["Dana's", "Nobody's"], (await MineAsync(Alice, "?scope=firm")).Order(StringComparer.Ordinal));
        Assert.Empty(await MineAsync(Bob, "?scope=firm"));
        Assert.Equal(400, (await _world.Get("/v1/me/tasks?scope=everyone")).Status);
    }

    [Fact]
    public async Task Assigned_to_me_sorts_by_due_date_then_undated_last()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId, $$"""{"subject": "No date", "assigneeSubject": "{{Alice}}"}""");
        await Add(caseId, $$"""{"subject": "Later", "assigneeSubject": "{{Alice}}", "dueDate": "2099-12-01"}""");
        await Add(caseId, $$"""{"subject": "Soonest", "assigneeSubject": "{{Alice}}", "dueDate": "2099-10-01"}""");

        Assert.Equal(["Soonest", "Later", "No date"], await MineAsync());
    }

    private async Task<List<string?>> MineAsync(string subject = Alice, string query = "")
    {
        var answer = await _world.Get($"/v1/me/tasks{query}", subject);
        Assert.True(answer.Status == 200, answer.Body);
        return [.. answer.Object["tasks"]!.AsArray().Select(t => t!.AsObject().Str("subject"))];
    }

    private Task RestrictAliceAsync(PermissionLevel level) =>
        _world.ReplaceAsync(FirmWorld.Member(Alice, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.Tasks, level)));

    private Task<Answer> Add(string caseId, string body = """{"subject": "Call the trustee"}""", string subject = Alice) =>
        _world.Post($"/v1/cases/{caseId}/tasks", body, subject);
}
