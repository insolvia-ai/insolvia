using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Http;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

// test_worker_guards.py, translated — the worker's stops before the final
// submit, each proved to stop BEFORE the court sees a submission and, where
// it can, before the credential is even opened: the kill switch (at the
// start and again before the submit), a credential revoked mid-run, a case
// edited mid-run, no verified driver, a fake composed outside local, a host
// outside the fence (no transport touched), an expired approval, a message
// that is not a job or names no approval of its filing — and nothing secret
// in a log line, a stored record or an access row.
public sealed partial class WorkerGuardTests : IAsyncLifetime
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

    private List<string?> Opened() => [.. _world.Actions().Where(a => a.Action == AccessAction.CredentialOpen).Select(a => a.Purpose)];

    private void AfterUpload(Func<Task> hook)
    {
        var baseUrl = _court.BaseUrl;
        _world.Driver = () => new UploadHook(new FakeCmEcfDriver(baseUrl), hook);
    }

    [Fact]
    public async Task The_kill_switch_off_hands_back_before_anything_is_opened()
    {
        _world.KillSwitch.Enabled = false;
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "submissions_disabled"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task The_kill_switch_is_read_again_before_the_final_submit()
    {
        AfterUpload(() =>
        {
            _world.KillSwitch.Enabled = false;
            return Task.CompletedTask;
        });
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "submissions_disabled"), (result.Outcome, result.Reason));
        Assert.True(_court.Uploads > 0);
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_credential_revoked_mid_run_stops_before_the_final_submit()
    {
        AfterUpload(() => FilingCredentialRules.RevokeAsync(_world.CredentialId, FilingWorld.Firm, FilingWorld.Attorney, _world.Service<IFilingCredentialStore>(), _world.AccessLog));
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "credential_unavailable"), (result.Outcome, result.Reason));
        Assert.Equal([FilingWorker.PurposeSignIn, FilingWorker.PurposeRecheck], Opened());
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_withdrawn_authorization_mid_run_stops_before_the_final_submit()
    {
        AfterUpload(() => FilingCredentialRules.WithdrawAuthorizationAsync(FilingWorld.Firm, FilingWorld.Attorney, _world.Service<IFilingAuthorizationStore>(), _world.Service<IFilingCredentialStore>(), _world.AccessLog));
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "credential_unavailable"), (result.Outcome, result.Reason));
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_case_edited_mid_run_stops_before_the_final_submit()
    {
        AfterUpload(async () =>
        {
            var debtors = _world.Service<IDebtorStore>();
            var debtor = (await debtors.GetAsync(_world.CaseId, FilingRole.Debtor1))!;
            await debtors.PutAsync(debtor with { Body = debtor.Body with { Phone = "555-0199" }, UpdatedAt = debtor.UpdatedAt.AddSeconds(1) });
        });
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "changed_after_approval"), (result.Outcome, result.Reason));
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_court_with_no_verified_driver_hands_back_without_opening()
    {
        _world.NoDriver = true;
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "court_not_verified"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
        Assert.Empty(CourtDrivers.Verified);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("production")]
    public async Task A_fake_driver_composed_outside_local_is_refused(string environment)
    {
        _world.Environment = environment;
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "court_not_verified"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
        Assert.Equal(0, _court.Submissions);
    }

    [Fact]
    public async Task A_driver_aimed_outside_the_fence_is_refused_before_the_credential_or_any_transport()
    {
        _world.Driver = () => new FakeCmEcfDriver("https://ecf.flmb.uscourts.gov");
        var (_, body) = await _world.ApproveAsync();
        var deps = _world.Deps() with { Http = () => throw new InvalidOperationException("a transport was created") };

        var result = await FilingWorker.RunAsync(body, deps, Ct);

        Assert.Equal(("handed_back", "host_not_allowed"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
    }

    [Fact]
    public async Task An_expired_approval_is_handed_back_and_never_signs_in()
    {
        var (_, body) = await _world.ApproveAsync();
        _world.Clock.Offset = TimeSpan.FromSeconds(3600 + 1);

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "approval_unavailable"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
        Assert.Contains((AccessAction.FilingConsume, AccessOutcome.Denied, "expired"), _world.Actions());
    }

    [Fact]
    public async Task A_consumed_approval_is_never_used_again_by_a_new_filing_id()
    {
        var (approval, body) = await _world.ApproveAsync();
        await _world.RunAsync(body);

        // A second consume of the same approval — what a forged or replayed
        // job could attempt — is refused by the store's single-use write.
        var again = await _world.Service<IFilingApprovalStore>().ConsumeAsync(approval.CaseId, approval.ApprovalId, approval.Digest, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow, Ct);
        Assert.False(again);
    }

    public static TheoryData<string> NotJobs => new()
    {
        "not json",
        """{"kind": "packet.assemble"}""",
        """{"kind": "filing.submit", "version": 1, "approval_id": "00000000-0000-4000-8000-000000000000", "filing_id": "00000000-0000-4000-8000-000000000001", "case_id": "00000000-0000-4000-8000-000000000002"}""",
    };

    [Theory]
    [MemberData(nameof(NotJobs))]
    public async Task A_message_naming_no_approval_is_dropped_and_writes_nothing(string body)
    {
        var result = await _world.RunAsync(body);

        Assert.Equal("dropped", result.Outcome);
        Assert.Empty(((MemoryFilingStore)_world.Service<IFilingStore>()).Items);
    }

    [Fact]
    public async Task A_message_whose_filing_id_is_not_its_approvals_is_dropped()
    {
        var (_, body) = await _world.ApproveAsync();
        var forged = JsonNode.Parse(body)!.AsObject();
        forged["filing_id"] = "00000000-0000-4000-8000-0000000000ff";

        var result = await _world.RunAsync(forged.ToJsonString());

        Assert.Equal(("dropped", "no_such_approval"), (result.Outcome, result.Reason));
        Assert.Empty(((MemoryFilingStore)_world.Service<IFilingStore>()).Items);
    }

    [Fact]
    public async Task No_secret_reaches_a_log_line_a_record_or_an_access_row()
    {
        var (approval, body) = await _world.ApproveAsync();

        Assert.Equal("filed", (await _world.RunAsync(body)).Outcome);

        var stored = await _world.FilingAsync(approval);
        var blobs = _world.Service<IDocumentBlobStore>();
        var receipt = (await _world.Service<IDocumentStore>().GetAsync(_world.CaseId, stored.ReceiptDocumentId!, Ct))!;
        var haystack = string.Join(
            "\n",
            [
                .. _world.Log.Lines,
                JsonSerializer.Serialize(((MemoryFilingStore)_world.Service<IFilingStore>()).Items),
                JsonSerializer.Serialize(_world.AccessLog.Events),
                string.Join(",", _world.AccessLog.Events),
                Encoding.Latin1.GetString((await blobs.GetBytesAsync(receipt.StorageRef, Ct))!),
                Encoding.Latin1.GetString((await blobs.GetBytesAsync(stored.Confirmation!.PageRef!, Ct))!),
                .. _world.Queue.Messages,
            ]);
        Assert.DoesNotContain(_court.Password, haystack, StringComparison.Ordinal);
        Assert.DoesNotContain(_court.TotpSeed, haystack, StringComparison.Ordinal);

        // The reference debtors' tax ids — in the file the court received
        // (one Case Upload), and nowhere else.
        Assert.Equal(1, _court.CaseUploads);
        foreach (var digits in new[] { "987654321", "987654322" })
        {
            Assert.DoesNotContain(digits, haystack, StringComparison.Ordinal);
            Assert.DoesNotContain($"{digits[..3]}-{digits[3..5]}-{digits[5..]}", haystack, StringComparison.Ordinal);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var code in new[] { -30, 0, 30 }.Select(drift => Totp.At(_court.TotpSeed, now.AddSeconds(drift))))
        {
            Assert.DoesNotMatch(new Regex($"\\b{code}\\b"), haystack);
        }
    }

    /// <summary>A driver whose upload, once done, runs a hook — the moment between the last upload and the re-check.</summary>
    private sealed class UploadHook(ICourtDriver inner, Func<Task> hook) : ICourtDriver
    {
        public string DriverId => inner.DriverId;

        public IReadOnlyList<string> BaseUrls => inner.BaseUrls;

        public bool CaseUpload => inner.CaseUpload;

        public ICourtSession Start(ICourtHttpClient http) => new Session(inner.Start(http), hook);

        private sealed class Session(ICourtSession inner, Func<Task> hook) : ICourtSession
        {
            public Task SignInAsync(string login, string password, Func<string> totp, CancellationToken cancellationToken = default) => inner.SignInAsync(login, password, totp, cancellationToken);

            public Task OpenCaseAsync(CaseOpening opening, CancellationToken cancellationToken = default) => inner.OpenCaseAsync(opening, cancellationToken);

            public async Task UploadAsync(IReadOnlyList<FilingFile> files, CancellationToken cancellationToken = default)
            {
                await inner.UploadAsync(files, cancellationToken);
                await hook();
            }

            public Task<CourtConfirmation> FinalSubmitAsync(CancellationToken cancellationToken = default) => inner.FinalSubmitAsync(cancellationToken);
        }
    }
}
