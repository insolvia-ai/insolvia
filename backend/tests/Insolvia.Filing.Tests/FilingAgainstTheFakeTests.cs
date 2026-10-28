using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Documents;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Http;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

// test_filing_against_the_fake.py and test_case_filed.py, translated — ADR
// 0024 PR 7/8's done-when, in the unit tier:
//   1. THE REFERENCE CASE FILES END TO END: approved through the API with a
//      fresh sign-in, enqueued, consumed, signed in with a real TOTP,
//      uploaded, re-checked, marked, submitted ONCE, its receipt stored, and
//      the case filed through the lifecycle writer.
//   2. EVERY FAULT MODE ends handed_back or outcome_unknown with a reason.
//   3. NEVER A SECOND FILING: a redelivery, a crash after the submit, a crash
//      before it, two consumers racing — each leaves at most ONE submission.
public sealed class FilingAgainstTheFakeTests : IAsyncLifetime
{
    private FakeCmEcf _court = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FilingWorld _world = null!;

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

    // ── 1. end to end ──

    [Fact]
    public async Task The_reference_case_files_end_to_end_against_the_fake()
    {
        var (approval, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        var stored = await _world.FilingAsync(approval);
        Assert.Equal(("filed", approval.FilingId), (result.Outcome, result.FilingId));
        Assert.Equal(1, _court.Submissions);
        Assert.Equal(
            [FilingState.Claimed, FilingState.SignedIn, FilingState.Uploading, FilingState.AtFinalSubmit, FilingState.Submitted, FilingState.Filed],
            stored.History.Select(step => step.State));
        Assert.Equal(_court.CaseNumbers[0], stored.Confirmation!.CaseNumber);
        Assert.NotEmpty(stored.Confirmation.DocketEntries);
        Assert.NotNull(stored.Confirmation.PageRef);
        Assert.Equal(FilingWorker.FollowUpFee, stored.FollowUps[0]);
        Assert.Equal(FakeCmEcfDriver.Id, stored.Driver);
        Assert.Equal(1, _court.SignIns);
    }

    [Fact]
    public async Task The_case_is_filed_with_the_courts_number_and_date_and_a_lifecycle_event_carrying_the_petition_chapter()
    {
        var (approval, body) = await _world.ApproveAsync();

        await _world.RunAsync(body);

        var filed = (await _world.CaseAsync())!;
        var confirmation = (await _world.FilingAsync(approval)).Confirmation!;
        Assert.Equal(CaseStatus.Filed, filed.Status);
        Assert.Equal(confirmation.CaseNumber, filed.CaseNumber);
        Assert.Equal(DateOnly.Parse(confirmation.FiledAt[..10], System.Globalization.CultureInfo.InvariantCulture), filed.FiledAt);
        var recorded = (await _world.Service<ICaseStore>().LifecycleAsync(_world.CaseId, Ct))[^1];
        Assert.Equal(
            (LifecycleEventKind.Status, CaseStatus.Filed, 7, approval.FilingId, LifecycleProvenance.FilingWorker, CaseRules.FilingWorkerActor),
            (recorded.Kind, recorded.ToStatus!.Value, recorded.Chapter!.Value, recorded.FilingId, recorded.Provenance, recorded.RecordedBy));
        Assert.Contains((AccessAction.FilingSubmit, AccessOutcome.Allowed, "case_filed"), _world.Actions());
    }

    [Fact]
    public async Task Once_filed_the_filing_set_refuses_a_new_approval()
    {
        var (_, body) = await _world.ApproveAsync();
        await _world.RunAsync(body);

        var basis = (await _world.ReadAsync())["basis"]!;
        Assert.Equal(["filed"], basis["blockers"]!.AsArray().Select(b => b!.GetValue<string>()));
        var (status, answer) = await _world.CallAsync(HttpMethod.Post, _world.ApprovalPath, $$"""{"digest": "{{basis["digest"]}}"}""");
        Assert.Equal((409, "FilingSetNotReady"), (status, answer!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_receipt_and_the_courts_page_are_stored_with_the_case()
    {
        var (approval, body) = await _world.ApproveAsync();

        await _world.RunAsync(body);

        var stored = await _world.FilingAsync(approval);
        var receipt = (await _world.Service<IDocumentStore>().GetAsync(_world.CaseId, stored.ReceiptDocumentId!, Ct))!;
        Assert.Equal((DocumentKind.CourtNotice, DocumentStatus.Stored, DocumentContentType.Pdf), (receipt.Kind, receipt.Status, receipt.ContentType));
        var blobs = _world.Service<IDocumentBlobStore>();
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await blobs.GetBytesAsync(receipt.StorageRef, Ct))![..4]), StringComparison.Ordinal);
        var page = System.Text.Encoding.UTF8.GetString((await blobs.GetBytesAsync(stored.Confirmation!.PageRef!, Ct))!);
        Assert.Contains(stored.Confirmation.CaseNumber, page, StringComparison.Ordinal);
        var prefix = $"cases/{_world.CaseId}/filings/{approval.FilingId}/";
        Assert.StartsWith(prefix, receipt.StorageRef, StringComparison.Ordinal);
        Assert.StartsWith(prefix, stored.Confirmation.PageRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_document_the_court_received_is_an_approved_packet_file()
    {
        var (_, body) = await _world.ApproveAsync();
        var documents = (await _world.ReadAsync())["basis"]!["documents"]!.AsArray();

        await _world.RunAsync(body);

        var filed = documents.Count(d => d!["source"]!.GetValue<string>() == "packet" && d["handling"]!.GetValue<string>() != "not_filed");
        Assert.Equal(filed, _court.Uploads);
    }

    [Fact]
    public async Task The_run_is_recorded_in_the_access_log_the_open_before_the_decrypt()
    {
        var (_, body) = await _world.ApproveAsync();

        await _world.RunAsync(body);

        var actions = _world.Actions();
        Assert.Contains((AccessAction.FilingApprove, AccessOutcome.Allowed, null), actions);
        Assert.Contains((AccessAction.FilingConsume, AccessOutcome.Allowed, null), actions);
        Assert.Contains((AccessAction.CredentialOpen, AccessOutcome.Allowed, FilingWorker.PurposeSignIn), actions);
        Assert.Contains((AccessAction.CredentialOpen, AccessOutcome.Allowed, FilingWorker.PurposeRecheck), actions);
        Assert.Contains((AccessAction.FilingSubmit, AccessOutcome.Allowed, "submitted"), actions);
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Allowed, FilingWorker.PurposeDigest), actions);
    }

    // ── 2. every fault mode stops ──

    public static TheoryData<string, string, string, int> Expected => new()
    {
        { "bad_login", "handed_back", "sign_in_failed", 0 },
        { "totp_rejected", "handed_back", "mfa_rejected", 0 },
        { "slow", "handed_back", "court_timeout", 0 },
        { "upload_500", "handed_back", "court_error", 0 },
        { "duplicate_case", "handed_back", "duplicate_case", 0 },
        { "unexpected_screen", "handed_back", "unrecognised_screen", 0 },
        { "timeout_after_submit", "outcome_unknown", "submit_timeout", 1 },
        { "receipt_missing", "outcome_unknown", "receipt_missing", 1 },
    };

    [Fact]
    public void Every_fault_mode_has_an_expected_outcome() =>
        Assert.Equal(FakeCmEcf.Faults.Where(f => f != "none").Order(StringComparer.Ordinal), Expected.Select(row => row.Data.Item1).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task A_fault_mode_ends_handed_back_or_outcome_unknown_and_a_redelivery_never_submits_again(string fault, string state, string reason, int submissions)
    {
        _court.Fault = fault;
        var (approval, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        var stored = await _world.FilingAsync(approval);
        Assert.Equal((state, reason), (result.Outcome, result.Reason));
        Assert.Equal(state, Core.Wire.ToWire(stored.State));
        Assert.Equal((reason, "packet"), (stored.HandBack!.Reason, stored.HandBack.Link));
        Assert.NotEmpty(stored.HandBack.Action);
        Assert.Equal(submissions, _court.Submissions);

        _court.Fault = "none";
        Assert.Equal("noop", (await _world.RunAsync(body)).Outcome);
        Assert.Equal(submissions, _court.Submissions);
        Assert.NotEqual(CaseStatus.Filed, (await _world.CaseAsync())!.Status);
    }

    // ── 3. never a second filing ──

    [Fact]
    public async Task A_redelivered_job_after_filing_is_a_noop_and_changes_nothing()
    {
        var (approval, body) = await _world.ApproveAsync();
        await _world.RunAsync(body);
        var before = await _world.FilingAsync(approval);
        var events = (await _world.Service<ICaseStore>().LifecycleAsync(_world.CaseId, Ct)).Count;

        var again = await _world.RunAsync(body);

        Assert.Equal(("noop", "filed"), (again.Outcome, again.Reason));
        Assert.Equal(1, _court.Submissions);
        Assert.Equal(before, await _world.FilingAsync(approval));
        Assert.Equal(events, (await _world.Service<ICaseStore>().LifecycleAsync(_world.CaseId, Ct)).Count);
    }

    [Fact]
    public async Task A_crash_after_the_final_submit_ends_outcome_unknown_and_never_resubmits()
    {
        var (approval, body) = await _world.ApproveAsync();
        var baseUrl = _court.BaseUrl;
        _world.Driver = () => new CrashingDriver(new FakeCmEcfDriver(baseUrl), "after_submit");
        await Assert.ThrowsAsync<OperationCanceledException>(() => _world.RunAsync(body));
        Assert.Equal(FilingState.AtFinalSubmit, (await _world.FilingAsync(approval)).State);
        _world.Driver = null;

        var redelivered = await _world.RunAsync(body);

        Assert.Equal(("outcome_unknown", "interrupted_after_submit_mark"), (redelivered.Outcome, redelivered.Reason));
        Assert.Equal("noop", (await _world.RunAsync(body)).Outcome);
        Assert.Equal(1, _court.Submissions);
    }

    [Fact]
    public async Task A_crash_before_the_mark_waits_out_the_lease_then_hands_back()
    {
        var (approval, body) = await _world.ApproveAsync();
        var baseUrl = _court.BaseUrl;
        _world.Driver = () => new CrashingDriver(new FakeCmEcfDriver(baseUrl), "after_upload");
        await Assert.ThrowsAsync<OperationCanceledException>(() => _world.RunAsync(body));
        _world.Driver = null;

        var withinLease = await _world.RunAsync(body);
        _world.Clock.Offset = TimeSpan.FromSeconds(_world.LeaseSeconds + 1);
        var afterLease = await _world.RunAsync(body);

        Assert.Equal(FilingState.HandedBack, (await _world.FilingAsync(approval)).State);
        Assert.Equal(("noop", "attempt_in_flight"), (withinLease.Outcome, withinLease.Reason));
        Assert.Equal(("handed_back", "interrupted"), (afterLease.Outcome, afterLease.Reason));
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task Two_consumers_racing_one_message_submit_once()
    {
        var (_, body) = await _world.ApproveAsync();

        var results = await Task.WhenAll(
            Task.Run(() => _world.RunAsync(body)),
            Task.Run(() => _world.RunAsync(body)));

        Assert.Equal(["filed", "noop"], results.Select(r => r.Outcome).Order(StringComparer.Ordinal));
        Assert.Equal(1, _court.Submissions);
    }

    /// <summary>A process dying mid-run: cancellation, which nothing in the worker catches — as nothing catches a killed Lambda.</summary>
    private sealed class CrashingDriver(ICourtDriver inner, string at) : ICourtDriver
    {
        public string DriverId => inner.DriverId;

        public IReadOnlyList<string> BaseUrls => inner.BaseUrls;

        public bool CaseUpload => inner.CaseUpload;

        public ICourtSession Start(ICourtHttpClient http) => new Session(inner.Start(http), at);

        private sealed class Session(ICourtSession inner, string at) : ICourtSession
        {
            public Task SignInAsync(string login, string password, Func<string> totp, CancellationToken cancellationToken = default) => inner.SignInAsync(login, password, totp, cancellationToken);

            public Task OpenCaseAsync(CaseOpening opening, CancellationToken cancellationToken = default) => inner.OpenCaseAsync(opening, cancellationToken);

            public async Task UploadAsync(IReadOnlyList<FilingFile> files, CancellationToken cancellationToken = default)
            {
                await inner.UploadAsync(files, cancellationToken);
                if (at == "after_upload")
                {
                    throw new OperationCanceledException("the process died");
                }
            }

            public async Task<CourtConfirmation> FinalSubmitAsync(CancellationToken cancellationToken = default)
            {
                var confirmation = await inner.FinalSubmitAsync(cancellationToken);
                return at == "after_submit" ? throw new OperationCanceledException("the process died") : confirmation;
            }
        }
    }
}
