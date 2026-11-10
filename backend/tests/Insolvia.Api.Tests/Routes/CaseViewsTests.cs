using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Cases;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Clients;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.TaxIds;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The short views pinned to their wire shapes: the portal's document and
/// request, the public status, the tax id's last-four view, and ADR 0027's
/// lifecycle event.
/// </summary>
public sealed class CaseViewsTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 2, 15, 4, 5, 123, TimeSpan.Zero);

    private static readonly Document Upload = new("doc-1", "case-1", DocumentKind.BankStatement, "statement.pdf", DocumentContentType.Pdf, 2048, "cases/case-1/doc-1", "client-sub", At)
    {
        Channel = DocumentChannel.Client,
        RequestId = "req-1",
    };

    [Fact]
    public void A_portal_document_names_no_case_and_no_channel()
    {
        Same("""{"byteSize":2048,"contentType":"application/pdf","fileName":"statement.pdf","id":"doc-1","requestId":"req-1","status":"pending","uploadedAt":"2026-03-02T15:04:05.123Z"}""", PortalDocumentView.Of(Upload));
    }

    [Fact]
    public void A_portal_request_carries_only_the_clients_own_uploads()
    {
        var request = new DocumentRequest("req-1", "case-1", "Bank statements", DocumentKind.BankStatement, null, DocumentRequestStatus.Requested, ["doc-1", "staff-doc"], At, "staff", At);

        var body = Wire(PortalRequestView.Of(request, [Upload])).AsObject();

        Assert.Equal(["description", "id", "kind", "status", "title", "uploads"], body.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Null(body["description"]);
        Assert.Single(body["uploads"]!.AsArray());
    }

    [Fact]
    public void The_public_status_and_the_tax_id_are_the_two_short_shapes()
    {
        Same("""{"kind":"ssn","last_four":"4321"}""", TaxIdView.Of(new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-1")));
        var stored = new Case("case-1", "firm-1", "sub", 13, "Middle District of Florida", CaseStatus.Filed, At, At);
        Same("""{"chapter":13,"stage":"filed"}""", PublicStatusView.Of(CasePublicStatuses.From(stored, "firm-1")!));
    }

    [Fact]
    public void A_confirmation_event_carries_its_plan_version_and_a_court_notice_its_candidate()
    {
        var ruling = new LifecycleEvent("case-1", LifecycleEventKind.Confirmation, new DateOnly(2026, 6, 1), At, "sub", LifecycleProvenance.CourtNotice)
        {
            CandidateId = "cand-1",
            PlanVersionId = "plan-1",
        };

        Same("""{"candidateId":"cand-1","effectiveOn":"2026-06-01","kind":"confirmation","planVersionId":"plan-1","provenance":"court_notice","recordedAt":"2026-03-02T15:04:05.123000Z","recordedBy":"sub"}""", LifecycleEventView.Of(ruling));
    }

    private static JsonNode Wire(object view) => JsonSerializer.SerializeToNode(view, WireJson.Options)!;

    private static void Same(string expected, object view)
    {
        var actual = Wire(view);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
    }
}
