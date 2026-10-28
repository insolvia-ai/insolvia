using System.Text;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.Ports;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Fence;
using Insolvia.Filing.Http;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

// test_case_upload_against_the_fake.py, translated — the Case Upload file in
// the worker (ADR 0024 PR 9), against the fake court:
//   - a court configured for Case Upload (the fake's default) receives
//     Debtor.txt FIRST, built from the approved case with each debtor's tax
//     id opened through a logged `taxid.read` (purpose `case_upload`), and
//     files end to end;
//   - a screen-entry-only court receives no Debtor.txt, and no tax id is
//     opened at all;
//   - a case whose file cannot be built hands back BEFORE the credential is
//     opened, with nothing sent to the court;
//   - a file the court refuses (a wrong statistics field count — the AO
//     spec's own refusal) stops the run with the court's words.
// The SSN never appearing in a log line, record, access row or stored page is
// WorkerGuardTests.No_secret_reaches_a_log_line_a_record_or_an_access_row.
public sealed class CaseUploadAgainstTheFakeTests : IAsyncLifetime
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

    private List<(string Principal, string? Purpose, string? FilingRole)> TaxIdReads() =>
        [.. _world.AccessLog.Events.Where(e => e.Action == AccessAction.TaxIdRead).Select(e => (e.Principal, e.Purpose, e.FilingRole))];

    private List<string?> Opened() => [.. _world.Actions().Where(a => a.Action == AccessAction.CredentialOpen).Select(a => a.Purpose)];

    [Fact]
    public async Task Debtor_txt_is_uploaded_first_and_the_case_files()
    {
        var (approval, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal("filed", result.Outcome);
        Assert.Equal(1, _court.CaseUploads);
        var stored = await _world.FilingAsync(approval);
        Assert.Equal("0 Debtor.txt", stored.Confirmation!.DocketEntries[0]);
        Assert.Equal([(approval.AttorneyId, "case_upload", "debtor_1"), (approval.AttorneyId, "case_upload", "debtor_2")], TaxIdReads());
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Allowed, "case_upload"), _world.Actions());
    }

    [Fact]
    public async Task A_screen_entry_court_gets_no_debtor_txt_and_no_tax_id_is_opened()
    {
        var baseUrl = _court.BaseUrl;
        _world.Driver = () => new FakeCmEcfDriver(baseUrl, caseUpload: false);
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal("filed", result.Outcome);
        Assert.Equal(0, _court.CaseUploads);
        Assert.Empty(TaxIdReads());
    }

    [Fact]
    public async Task A_file_that_cannot_be_built_hands_back_before_the_login_is_opened()
    {
        // A debtor whose residence county the district does not serve: field 17
        // cannot land. Edited BEFORE the approval, so the digest is the case's.
        var debtors = _world.Service<IDebtorStore>();
        var first = (await debtors.GetAsync(_world.CaseId, FilingRole.Debtor1, Ct))!;
        await debtors.PutAsync(first with { Body = first.Body with { ResidenceAddress = first.Body.ResidenceAddress with { County = "Fulton" } } }, Ct);
        var (_, body) = await _world.ApproveAsync();

        var result = await _world.RunAsync(body);

        Assert.Equal(("handed_back", "case_upload_invalid"), (result.Outcome, result.Reason));
        Assert.Empty(Opened());
        Assert.Equal((0, 0, 0), (_court.SignIns, _court.Uploads, _court.Submissions));

        // Only the problem's codes are logged — never a value.
        var refused = Assert.Single(_world.Log.Lines, line => line.Contains("case upload file refused", StringComparison.Ordinal));
        Assert.Contains("problems=debt.17:county", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("Fulton", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fake_court_refuses_a_wrong_field_count_and_the_driver_stops()
    {
        var session = new FakeCmEcfDriver(_court.BaseUrl).Start(new FencedHttpClient(HostFence.For("local"), TimeSpan.FromSeconds(2)));
        await session.SignInAsync(FakeCmEcf.Login, _court.Password, () => Totp.At(_court.TotpSeed, DateTimeOffset.UtcNow), Ct);
        await session.OpenCaseAsync(new CaseOpening("flmb", "3", 7, false), Ct);
        var shortFile = Encoding.ASCII.GetBytes("stat|" + new string('|', 70) + "\ndebt|db|A||B|||000-00-0000||3|||||||||||||\n");

        var stopped = await Assert.ThrowsAsync<HandBackException>(() => session.UploadAsync(
            [new FilingFile(0, "case_upload", "Debtor.txt", "case_upload", shortFile, new string('0', 64))],
            Ct));

        Assert.Equal("court_message", stopped.Reason);
        Assert.Contains("correct number of data items", stopped.CourtSaid ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal((0, 0), (_court.CaseUploads, _court.Submissions));
    }

    [Fact]
    public void The_fake_courts_own_checks()
    {
        Assert.NotNull(FakeCmEcf.CaseUploadRefusal(Encoding.ASCII.GetBytes("stat|" + new string('|', 79) + "\n"))); // no debt
        Assert.Equal(FakeCmEcf.CaseUploadWrongCount, FakeCmEcf.CaseUploadRefusal("debt|db\n"u8.ToArray()));
        Assert.NotNull(FakeCmEcf.CaseUploadRefusal(Encoding.UTF8.GetBytes("stat|é\n")));
    }
}
