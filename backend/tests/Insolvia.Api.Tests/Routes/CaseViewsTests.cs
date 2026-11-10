using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Cases;
using Insolvia.Api.Http;
using Insolvia.Domain;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Clients;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.DocumentRequests;
using Insolvia.Domain.Documents;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Provenance;
using Insolvia.Domain.TaxIds;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The views no route of this slice serves yet — the portal's (18.19) and the
/// lifecycle's kinds no PATCH can make — pinned to their wire shapes
/// (<c>portal_document_json</c>, <c>portal_request_json</c>,
/// <c>public_status_json</c>, <c>tax_id_json</c>) and ADR 0027's event.
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
        Assert.Equal(
            """{"byteSize":2048,"contentType":"application/pdf","fileName":"statement.pdf","id":"doc-1","requestId":"req-1","status":"pending","uploadedAt":"2026-03-02T15:04:05.123Z"}""",
            FlaskJson.Serialize(CaseViews.PortalDocument(Upload)));
    }

    [Fact]
    public void A_portal_request_carries_only_the_clients_own_uploads()
    {
        var request = new DocumentRequest("req-1", "case-1", "Bank statements", DocumentKind.BankStatement, null, DocumentRequestStatus.Requested, ["doc-1", "staff-doc"], At, "staff", At);

        var body = CaseViews.PortalRequest(request, [Upload]);

        Assert.Equal(["description", "id", "kind", "status", "title", "uploads"], body.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Null(body["description"]);
        Assert.Single(body["uploads"]!.AsArray());
    }

    [Fact]
    public void The_public_status_and_the_tax_id_are_the_two_short_shapes()
    {
        Assert.Equal("""{"kind":"ssn","last_four":"4321"}""", FlaskJson.Serialize(CaseViews.TaxId(new TaxIdRef(TaxIdKind.Ssn, "4321", "ref-1"))));
        var stored = new Case("case-1", "firm-1", "sub", 13, "Middle District of Florida", CaseStatus.Filed, At, At);
        Assert.Equal("""{"chapter":13,"stage":"filed"}""", FlaskJson.Serialize(CaseViews.PublicStatus(CasePublicStatuses.From(stored, "firm-1")!)));
    }

    [Fact]
    public void A_confirmation_event_carries_its_plan_version_and_a_court_notice_its_candidate()
    {
        var ruling = new LifecycleEvent("case-1", LifecycleEventKind.Confirmation, new DateOnly(2026, 6, 1), At, "sub", LifecycleProvenance.CourtNotice)
        {
            CandidateId = "cand-1",
            PlanVersionId = "plan-1",
        };

        Assert.Equal(
            """{"candidateId":"cand-1","effectiveOn":"2026-06-01","kind":"confirmation","planVersionId":"plan-1","provenance":"court_notice","recordedAt":"2026-03-02T15:04:05.123000Z","recordedBy":"sub"}""",
            FlaskJson.Serialize(CaseViews.LifecycleEvent(ruling)));
    }

    [Fact]
    public void Parity_debtor()
    {
        var empty = new Debtor("d-1", "case-1", FilingRole.Debtor1, At, At, DebtorBody.Empty);
        var full = new Debtor("d-2", "case-1", FilingRole.Debtor2, At, At, DebtorBody.Empty with
        {
            Name = new PersonName("Ann", null, "Lee"),
            OtherNamesUsed = [new OtherName("o-1", "A", null, null, null)],
            ResidenceAddress = new Address(Line1: "1 Main", City: "Tampa"),
            Phone = "",
            Venue = new Venue(VenueBasis.LivedLongest180Days),
            SignedAt = new DateOnly(2026, 1, 2),
        })
        {
            TaxId = new TaxIdRef(TaxIdKind.Ssn, "4321", "ref"),
            ClientId = "client-1",
            Provenance = new Dictionary<string, ProvenanceEntry> { ["name.given"] = new(ProvenanceSource.StaffTyped) { ConfirmedBy = "sub", Confidence = 0.5 } }.ToValueDictionary(),
        };
        foreach (var (debtor, differs) in new[] { (empty, (IReadOnlyList<string>?)null), (full, ["name.given"]) })
        {
            var old = CaseViews.Debtor(debtor, differs);
            var now = JsonSerializer.SerializeToNode(Insolvia.Api.Cases.DebtorView.Of(debtor, differs), WireJson.Options);
            Assert.True(JsonNode.DeepEquals(old, now), $"{FlaskJson.Serialize(old)}\n{FlaskJson.Serialize(now)}");
        }
    }

    private static void Same(JsonNode old, object now) =>
        Assert.True(JsonNode.DeepEquals(old, JsonSerializer.SerializeToNode(now, WireJson.Options)), $"{FlaskJson.Serialize(old)}\n{JsonSerializer.Serialize(now, WireJson.Options)}");

    [Fact]
    public void Parity_portal_document_and_request()
    {
        var bare = Upload with { RequestId = null, Status = DocumentStatus.Stored, Channel = DocumentChannel.Staff };
        foreach (var document in new[] { Upload, bare })
        {
            Same(CaseViews.PortalDocument(document), PortalDocumentView.Of(document));
        }

        var open = new DocumentRequest("req-1", "case-1", "Bank statements", DocumentKind.BankStatement, null, DocumentRequestStatus.Requested, [], At, "staff", At);
        var received = new DocumentRequest("req-2", "case-1", "Pay", DocumentKind.PayStub, "Last 60 days", DocumentRequestStatus.Received, ["doc-1"], At, "staff", At, At);
        foreach (var request in new[] { open, received })
        {
            Same(CaseViews.PortalRequest(request, [Upload]), PortalRequestView.Of(request, [Upload]));
            Same(CaseViews.PortalRequest(request, []), PortalRequestView.Of(request, []));
        }
    }
}
