using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

// test_filing_approval.py / test_filing_approval_routes.py /
// test_filing_outcome.py over the assembled reference case, through the
// REAL API: the digest binds what was shown, any change voids, single use,
// one live approval per case, a fresh sign-in, and the one way out of a
// hand-back — the attorney's word about the docket.
public sealed class ApprovalAndResolutionTests : IAsyncLifetime
{
    private FakeCmEcf _court = null!;
    private FilingWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _court = await FakeCmEcf.StartAsync();
        _world = await FilingWorld.CreateAsync(_court);
    }

    public async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await _court.DisposeAsync();
    }

    private Task<(int Status, JsonObject? Body)> ApproveRawAsync(string digest, string? token = null) =>
        _world.CallAsync(HttpMethod.Post, _world.ApprovalPath, $$"""{"digest": "{{digest}}"}""", token);

    private async Task<string> DigestAsync() => (await _world.ReadAsync())["basis"]!["digest"]!.GetValue<string>();

    [Fact]
    public async Task The_screen_is_served_what_it_will_approve_and_a_fresh_sign_in_enqueues_exactly_one_ids_only_job()
    {
        var view = await _world.ReadAsync();
        var basis = view["basis"]!;
        Assert.True(basis["ready"]!.GetValue<bool>(), basis.ToJsonString());
        Assert.Equal(64, basis["digest"]!.GetValue<string>().Length);
        Assert.DoesNotContain("987-65-4321", view.ToJsonString(), StringComparison.Ordinal);

        var (approval, body) = await _world.ApproveAsync();

        var job = JsonNode.Parse(Assert.Single(_world.Queue.Messages))!.AsObject();
        Assert.Equal(["approval_id", "case_id", "filing_id", "kind", "version"], job.Select(p => p.Key));
        Assert.Equal($$"""{"approval_id":"{{approval.ApprovalId}}","case_id":"{{_world.CaseId}}","filing_id":"{{approval.FilingId}}","kind":"filing.submit","version":1}""", body);
        Assert.Equal((ApprovalStatus.Pending, approval.Digest), (approval.Status, basis["digest"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_digest_the_screen_did_not_show_is_refused_and_queues_nothing()
    {
        var (status, body) = await ApproveRawAsync(new string('0', 64));

        Assert.Equal((409, "ConflictError"), (status, body!["error"]!.GetValue<string>()));
        Assert.Empty(_world.Queue.Messages);
        Assert.Contains((AccessAction.FilingApprove, AccessOutcome.Denied, "changed_since_shown"), _world.Actions());
    }

    [Fact]
    public async Task A_stale_session_cannot_approve()
    {
        var token = Tokens.Make(FilingWorld.Attorney, extra: new JsonObject { ["auth_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 301 });

        var (status, body) = await ApproveRawAsync(await DigestAsync(), token);

        Assert.Equal((403, "ReauthenticationRequired"), (status, body!["error"]!.GetValue<string>()));
        Assert.Empty(_world.Queue.Messages);
    }

    [Fact]
    public async Task A_packet_reassembled_after_approval_shows_voided_and_the_worker_hands_back()
    {
        var (_, job) = await _world.ApproveAsync();
        await _world.ReassembleAsync();

        var view = await _world.ReadAsync();
        Assert.Equal(("voided", VoidReasons.Changed), (view["approval"]!["status"]!.GetValue<string>(), view["approval"]!["voidReason"]!.GetValue<string>()));

        var result = await _world.RunAsync(job);
        Assert.Equal(("handed_back", "approval_unavailable"), (result.Outcome, result.Reason));
        Assert.Equal(0, _court.SignIns);
    }

    [Fact]
    public async Task A_debtor_edit_after_approval_voids_it_at_consume()
    {
        var (approval, job) = await _world.ApproveAsync();
        var debtors = _world.Service<IDebtorStore>();
        var debtor = (await debtors.GetAsync(_world.CaseId, FilingRole.Debtor1, Ct))!;
        await debtors.PutAsync(debtor with { Body = debtor.Body with { Phone = "555-0199" }, UpdatedAt = debtor.UpdatedAt.AddSeconds(1) }, Ct);

        var result = await _world.RunAsync(job);

        Assert.Equal(("handed_back", "approval_unavailable"), (result.Outcome, result.Reason));
        Assert.Equal((ApprovalStatus.Voided, VoidReasons.Changed), ((await _world.Service<IFilingApprovalStore>().GetAsync(_world.CaseId, approval.ApprovalId, Ct))!.Status, (await _world.Service<IFilingApprovalStore>().GetAsync(_world.CaseId, approval.ApprovalId, Ct))!.VoidReason));
        Assert.Contains((AccessAction.FilingConsume, AccessOutcome.Denied, "changed"), _world.Actions());
    }

    [Fact]
    public async Task Cancelling_voids_the_pending_approval_and_its_job_files_nothing()
    {
        var (_, job) = await _world.ApproveAsync();

        var (status, body) = await _world.CallAsync(HttpMethod.Delete, _world.ApprovalPath);
        Assert.Equal((200, "voided"), (status, body!["approval"]!["status"]!.GetValue<string>()));

        var result = await _world.RunAsync(job);
        Assert.Equal(("handed_back", "approval_unavailable"), (result.Outcome, result.Reason));
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_newer_approval_supersedes_the_pending_one()
    {
        var (first, firstJob) = await _world.ApproveAsync();
        var (second, _) = await _world.ApproveAsync();

        var old = (await _world.Service<IFilingApprovalStore>().GetAsync(_world.CaseId, first.ApprovalId, Ct))!;
        Assert.Equal((ApprovalStatus.Voided, VoidReasons.Superseded), (old.Status, old.VoidReason));
        Assert.Equal(second.ApprovalId, (await _world.Service<IFilingApprovalStore>().CurrentAsync(_world.CaseId, Ct))!.ApprovalId);
        Assert.Equal("handed_back", (await _world.RunAsync(firstJob)).Outcome);
    }

    [Fact]
    public async Task An_unqueueable_approval_is_voided_not_left_pending()
    {
        _world.Queue.Fail = true;

        var (status, body) = await ApproveRawAsync(await DigestAsync());

        Assert.Equal((503, "FilingQueueUnavailable"), (status, body!["error"]!.GetValue<string>()));
        Assert.Equal(VoidReasons.EnqueueFailed, (await _world.Service<IFilingApprovalStore>().CurrentAsync(_world.CaseId, Ct))!.VoidReason);
    }

    [Fact]
    public async Task A_handed_back_filing_blocks_a_new_approval_until_resolved_not_filed()
    {
        _court.Fault = "bad_login";
        var (approval, job) = await _world.ApproveAsync();
        Assert.Equal("handed_back", (await _world.RunAsync(job)).Outcome);

        var (blocked, refusal) = await ApproveRawAsync(await DigestAsync());
        Assert.Equal(409, blocked);
        Assert.Contains((AccessAction.FilingApprove, AccessOutcome.Denied, "filing_unresolved"), _world.Actions());

        var (resolved, view) = await _world.CallAsync(HttpMethod.Post, $"/v1/cases/{_world.CaseId}/filings/{approval.FilingId}/resolution", """{"outcome": "not_filed", "docket_checked": true}""");
        Assert.True(resolved == 200, view?.ToJsonString());
        Assert.Equal("not_filed", view!["filing"]!["resolution"]!["outcome"]!.GetValue<string>());
        Assert.False(view["filing"]!["resolvable"]!.GetValue<bool>());

        _court.Fault = "none";
        var (_, again) = await _world.ApproveAsync();
        Assert.Equal("filed", (await _world.RunAsync(again)).Outcome);
        Assert.Equal(1, _court.Submissions);
        _ = refusal;
    }

    [Fact]
    public async Task A_hand_back_resolved_as_filed_files_the_case_through_the_lifecycle()
    {
        _court.Fault = "duplicate_case";
        var (approval, job) = await _world.ApproveAsync();
        await _world.RunAsync(job);
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var (status, view) = await _world.CallAsync(
            HttpMethod.Post,
            $"/v1/cases/{_world.CaseId}/filings/{approval.FilingId}/resolution",
            $$"""{"outcome": "filed", "docket_checked": true, "case_number": "8:26-bk-01234", "filed_at": "{{today}}"}""");

        Assert.True(status == 200, view?.ToJsonString());
        var filed = (await _world.CaseAsync())!;
        Assert.Equal((CaseStatus.Filed, "8:26-bk-01234"), (filed.Status, filed.CaseNumber));
        var recorded = (await _world.Service<ICaseStore>().LifecycleAsync(_world.CaseId, Ct))[^1];
        Assert.Equal((approval.FilingId, LifecycleProvenance.Manual, FilingWorld.Attorney, 7), (recorded.FilingId, recorded.Provenance, recorded.RecordedBy, recorded.Chapter!.Value));
        Assert.Contains((AccessAction.FilingResolve, AccessOutcome.Allowed, "filed"), _world.Actions());
    }

    [Fact]
    public async Task An_unknown_outcome_cannot_be_called_not_filed_while_the_attempt_may_be_live()
    {
        _court.Fault = "timeout_after_submit";
        var (approval, job) = await _world.ApproveAsync();
        Assert.Equal("outcome_unknown", (await _world.RunAsync(job)).Outcome);

        var (status, body) = await _world.CallAsync(HttpMethod.Post, $"/v1/cases/{_world.CaseId}/filings/{approval.FilingId}/resolution", """{"outcome": "not_filed", "docket_checked": true}""");

        Assert.Equal((409, "ResolutionRefused", "attempt_may_be_live"), (status, body!["error"]!.GetValue<string>(), body["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_resolution_without_the_docket_checked_is_a_400()
    {
        _court.Fault = "bad_login";
        var (approval, job) = await _world.ApproveAsync();
        await _world.RunAsync(job);

        var (status, body) = await _world.CallAsync(HttpMethod.Post, $"/v1/cases/{_world.CaseId}/filings/{approval.FilingId}/resolution", """{"outcome": "not_filed"}""");

        Assert.Equal(400, status);
        Assert.NotNull(body!["fields"]!["docket_checked"]);
    }
}
