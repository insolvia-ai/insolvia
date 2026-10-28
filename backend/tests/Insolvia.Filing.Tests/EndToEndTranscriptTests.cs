using Insolvia.Core.Ports;
using Insolvia.Filing.Tests.FakeCourt;
using Insolvia.Filing.Tests.Support;

namespace Insolvia.Filing.Tests;

/// <summary>
/// The local end-to-end run, narrated: approve with a fresh-sign-in token
/// through the API → the job on the queue → the worker signs in to the fake
/// court with TOTP, uploads Debtor.txt (the Case Upload court, the fake's default) then the packet, final-submits, stores the receipt and files the
/// case with a lifecycle event carrying the petition chapter → the same
/// message delivered again does nothing. The transcript goes to the test
/// output (<c>dotnet test --logger "console;verbosity=detailed"</c>).
/// </summary>
public sealed class EndToEndTranscriptTests
{
    [Fact]
    public async Task Approve_enqueue_file_and_a_second_delivery_does_nothing()
    {
        var output = TestContext.Current.TestOutputHelper!;
        await using var court = await FakeCmEcf.StartAsync();
        await using var world = await FilingWorld.CreateAsync(court);
        output.WriteLine($"fake CM/ECF on {court.BaseUrl}; case {world.CaseId}; credential {world.CredentialId} enrolled through POST /v1/me/filing-credentials");

        var (approval, body) = await world.ApproveAsync();
        output.WriteLine($"POST /v1/cases/{world.CaseId}/filing-approval (auth_time = now) -> 201, approval {approval.ApprovalId} pending, digest {approval.Digest[..16]}…");
        output.WriteLine($"queue body: {body}");

        var first = await world.RunAsync(body);
        var filing = await world.FilingAsync(approval);
        var filed = (await world.CaseAsync())!;
        var recorded = (await world.Service<ICaseStore>().LifecycleAsync(world.CaseId, TestContext.Current.CancellationToken))[^1];
        output.WriteLine($"delivery 1 -> {first.Outcome}; states {string.Join(" > ", filing.History.Select(s => Core.Wire.ToWire(s.State)))}");
        var reads = world.AccessLog.Events.Where(e => e.Action == Core.AccessLog.AccessAction.TaxIdRead).Select(e => $"{e.FilingRole}/{e.Purpose}");
        output.WriteLine($"court: sign-ins {court.SignIns}, uploads {court.Uploads} (Case Upload files accepted: {court.CaseUploads}; docket {string.Join(", ", filing.Confirmation!.DocketEntries.Take(2))}, …), submissions {court.Submissions}; case number {filing.Confirmation.CaseNumber}; receipt document {filing.ReceiptDocumentId}");
        output.WriteLine($"taxid.read rows: {string.Join(", ", reads)}");
        output.WriteLine($"case: status {Core.Wire.ToWire(filed.Status)}, number {filed.CaseNumber}, petition date {filed.FiledAt:yyyy-MM-dd}; lifecycle event {Core.Wire.ToWire(recorded.Kind)} -> {Core.Wire.ToWire(recorded.ToStatus!.Value)}, chapter {recorded.Chapter}, filing {recorded.FilingId}, by {recorded.RecordedBy} ({Core.Wire.ToWire(recorded.Provenance)})");

        var second = await world.RunAsync(body);
        output.WriteLine($"delivery 2 -> {second.Outcome} ({second.Reason}); court submissions still {court.Submissions}");

        Assert.Equal("filed", first.Outcome);
        Assert.Equal(("noop", "filed"), (second.Outcome, second.Reason));
        Assert.Equal(1, court.Submissions);
        Assert.Equal(1, court.CaseUploads);
        Assert.Equal("0 Debtor.txt", filing.Confirmation.DocketEntries[0]);
        Assert.Equal(7, recorded.Chapter);
    }
}
