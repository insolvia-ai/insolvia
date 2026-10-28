using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Firms;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Tests.Routes;

// test_filing_approval_routes.py's GATES, translated: what runs in front of
// the rule — the feature flag, the attorney rule, another firm's case, the
// fresh sign-in read from a real signed token, the blocker list, and a host
// with no filing queue. The approve → enqueue → file path over the
// assembled reference case is Insolvia.Filing.Tests' (FilingEndToEndTests),
// which composes this API beside the worker and the fake court.
public sealed class FilingApprovalRouteTests : IDisposable
{
    private const string Attorney = CaseWorld.Alice;
    private const string GrantedParalegal = CaseWorld.Greg;

    private readonly MemoryFilingJobSender _queue = new();
    private CaseWorld? _world;

    private CaseWorld World => _world!;

    private async Task<string> SeedAsync(bool queue = true)
    {
        _world = new CaseWorld(services =>
        {
            if (queue)
            {
                services.AddSingleton<IFilingJobSender>(_queue);
            }
        });
        await World.SeedAsync();
        var granted = FirmWorld.DefaultsWith(FirmRole.Attorney, Feature.ElectronicFiling, PermissionLevel.AddEdit);
        await World.ReplaceAsync(FirmWorld.Member(Attorney, isAdmin: true, permissions: granted));
        await World.ReplaceAsync(FirmWorld.Member(GrantedParalegal, role: FirmRole.Paralegal, accessAllCases: true, permissions: FirmWorld.DefaultsWith(FirmRole.Paralegal, Feature.ElectronicFiling, PermissionLevel.AddEdit)));
        await World.ReplaceAsync(FirmWorld.Member(CaseWorld.Carol, Ids.FirmB, isAdmin: true, permissions: granted));
        return await World.OpenCaseIdAsync();
    }

    private static string Path(string caseId) => $"/v1/cases/{caseId}/filing-approval";

    private static string Approve => $$"""{"digest": "{{new string('a', 64)}}"}""";

    [Fact]
    public async Task A_paralegal_without_the_feature_is_refused_by_the_flag_and_one_granted_it_by_the_rule()
    {
        var caseId = await SeedAsync();

        Assert.Equal(403, (await World.Get(Path(caseId), CaseWorld.Bob)).Status);
        var refused = await World.Post(Path(caseId), Approve, GrantedParalegal);

        Assert.Equal((403, "ForbiddenError"), (refused.Status, refused.Str("error")));
        Assert.Contains(World.Events, e => e.Action == AccessAction.FilingApprove && e.Outcome == AccessOutcome.Denied && e.Purpose == "not_an_attorney");
        Assert.Empty(_queue.Messages);
    }

    [Fact]
    public async Task Another_firms_case_is_not_found()
    {
        var caseId = await SeedAsync();

        Assert.Equal(404, (await World.Get(Path(caseId), CaseWorld.Carol)).Status);
        Assert.Equal(404, (await World.Post(Path(caseId), Approve, CaseWorld.Carol)).Status);
    }

    [Fact]
    public async Task An_unassembled_case_is_served_its_blockers_and_approving_it_answers_which()
    {
        var caseId = await SeedAsync();

        var read = await World.Get(Path(caseId), Attorney);
        Assert.Equal(200, read.Status);
        Assert.Equal("no-store", read.Response.Headers.CacheControl!.ToString());
        var basis = read.Object["basis"]!;
        Assert.False(basis["ready"]!.GetValue<bool>());
        Assert.Contains("packet", basis["blockers"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Null(read.Object["approval"]);

        var approve = await World.Post(Path(caseId), $$"""{"digest": "{{basis["digest"]}}"}""", Attorney);
        Assert.Equal((409, "FilingSetNotReady"), (approve.Status, approve.Str("error")));
        Assert.Contains("packet", approve.Object["blockers"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Empty(_queue.Messages);
    }

    public static TheoryData<int?> StaleBy => new() { FilingApprovalsWindow + 1, 3600, (int?)null };

    private const int FilingApprovalsWindow = 300;

    [Theory]
    [MemberData(nameof(StaleBy))]
    public async Task A_stale_session_is_refused_and_recorded(int? signedInAgo)
    {
        var caseId = await SeedAsync();
        var token = Tokens.Make(Attorney, extra: new JsonObject { ["auth_time"] = signedInAgo is { } ago ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ago : null });

        var answer = await World.World.SendAsync(HttpMethod.Post, Path(caseId), json: Approve, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + token });

        Assert.Equal((403, "ReauthenticationRequired"), (answer.Status, answer.Str("error")));
        Assert.Contains(World.Events, e => e.Action == AccessAction.FilingApprove && e.Purpose == "reauthentication_required");
        Assert.Empty(_queue.Messages);
    }

    [Fact]
    public async Task Without_a_filing_queue_nothing_is_approved()
    {
        var caseId = await SeedAsync(queue: false);

        var answer = await World.Post(Path(caseId), Approve, Attorney);

        Assert.Equal((503, "FilingQueueUnavailable"), (answer.Status, answer.Str("error")));
        Assert.DoesNotContain(World.Events, e => e.Action == AccessAction.FilingApprove);
    }

    [Fact]
    public async Task Cancelling_with_nothing_pending_is_a_409_and_a_missing_filing_a_404()
    {
        var caseId = await SeedAsync();

        Assert.Equal(409, (await World.Delete(Path(caseId), Attorney)).Status);
        Assert.Equal(404, (await World.Post($"/v1/cases/{caseId}/filings/{Guid.NewGuid()}/resolution", """{"outcome": "not_filed", "docket_checked": true}""", Attorney)).Status);
    }

    public void Dispose() => _world?.Dispose();
}
