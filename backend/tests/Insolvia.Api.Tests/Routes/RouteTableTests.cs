using Insolvia.Api.Auth;
using Insolvia.Api.Tests.Support;
using Insolvia.Domain.CaseEntities;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The firm-side route table as a whole: every route 18.14 maps is either
/// one of the two deliberately public ones or answers 401 without a token —
/// which also proves no handler bound as a raw <c>RequestDelegate</c> (that
/// would skip its filters silently).
/// </summary>
public sealed class RouteTableTests : IDisposable
{
    private static readonly string[] Public = ["POST /v1/waitlist", "POST /v1/unsubscribe"];

    private readonly FirmWorld _world = new();

    public static TheoryData<string, string> FirmSideRoutes => new()
    {
        { "GET", "/v1/me" },
        { "PATCH", "/v1/me" },
        { "GET", "/v1/firm" },
        { "PATCH", "/v1/firm" },
        { "GET", "/v1/firm/directory" },
        { "GET", "/v1/firm/users" },
        { "POST", "/v1/firm/users" },
        { "PATCH", "/v1/firm/users/{subject}" },
        { "DELETE", "/v1/firm/users/{subject}" },
        { "GET", "/v1/firm/clients" },
        { "POST", "/v1/firm/clients" },
        { "GET", "/v1/firm/clients/{clientId}" },
        { "PUT", "/v1/firm/clients/{clientId}" },
        { "PUT", "/v1/firm/clients/{clientId}/status" },
        { "PUT", "/v1/firm/clients/{clientId}/prospect-stage" },
        { "POST", "/v1/firm/clients/{clientId}/merge" },
        { "GET", "/v1/firm/creditors" },
        { "POST", "/v1/firm/creditors" },
        { "GET", "/v1/firm/creditors/{creditorId}" },
        { "PUT", "/v1/firm/creditors/{creditorId}" },
        { "DELETE", "/v1/firm/creditors/{creditorId}" },
        { "GET", "/v1/firm/questionnaire" },
        { "PUT", "/v1/firm/questionnaire" },
        { "DELETE", "/v1/firm/questionnaire" },
        { "GET", "/v1/firm/document-checklist" },
        { "PUT", "/v1/firm/document-checklist" },
        { "DELETE", "/v1/firm/document-checklist" },
    };

    /// <summary>The case-side slice 18.15 maps: part 1's records and part 2's computed analyses (no forms or approval; the pipeline and the portal have their own slices).</summary>
    public static TheoryData<string, string> CaseSideRoutes => WithCollections(new()
    {
        { "POST", "/v1/cases" },
        { "GET", "/v1/cases" },
        { "GET", "/v1/cases/{caseId}" },
        { "PATCH", "/v1/cases/{caseId}" },
        { "DELETE", "/v1/cases/{caseId}" },
        { "GET", "/v1/cases/{caseId}/assignees" },
        { "PUT", "/v1/cases/{caseId}/assignees/{subject}" },
        { "DELETE", "/v1/cases/{caseId}/assignees/{subject}" },
        { "GET", "/v1/firm/clients/{clientId}/cases" },
        { "GET", "/v1/courts" },
        { "GET", "/v1/cases/{caseId}/status-history" },
        { "GET", "/v1/cases/{caseId}/lifecycle" },
        { "POST", "/v1/cases/{caseId}/conversions" },
        { "PUT", "/v1/cases/{caseId}/archived" },
        { "POST", "/v1/cases/{caseId}/copy" },
        { "PUT", "/v1/cases/{caseId}/debtors/{filingRole}" },
        { "GET", "/v1/cases/{caseId}/debtors" },
        { "PUT", "/v1/cases/{caseId}/debtors/{filingRole}/client" },
        { "POST", "/v1/cases/{caseId}/debtors/{filingRole}/copy-from-client" },
        { "POST", "/v1/cases/{caseId}/debtors/{filingRole}/copy-to-client" },
        { "POST", "/v1/cases/{caseId}/notes" },
        { "GET", "/v1/cases/{caseId}/notes" },
        { "PUT", "/v1/cases/{caseId}/notes/{noteId}" },
        { "DELETE", "/v1/cases/{caseId}/notes/{noteId}" },
        { "POST", "/v1/cases/{caseId}/documents" },
        { "POST", "/v1/cases/{caseId}/documents/{documentId}/complete" },
        { "GET", "/v1/cases/{caseId}/documents" },
        { "GET", "/v1/cases/{caseId}/documents/{documentId}/url" },
        { "DELETE", "/v1/cases/{caseId}/documents/{documentId}" },
        { "GET", "/v1/cases/{caseId}/document-requests" },
        { "POST", "/v1/cases/{caseId}/document-requests" },
        { "POST", "/v1/cases/{caseId}/document-requests/from-checklist" },
        { "PATCH", "/v1/cases/{caseId}/document-requests/{requestId}" },
        { "DELETE", "/v1/cases/{caseId}/document-requests/{requestId}" },
        { "POST", "/v1/cases/{caseId}/tasks" },
        { "GET", "/v1/cases/{caseId}/tasks" },
        { "GET", "/v1/cases/{caseId}/tasks/{taskId}" },
        { "PATCH", "/v1/cases/{caseId}/tasks/{taskId}" },
        { "DELETE", "/v1/cases/{caseId}/tasks/{taskId}" },
        { "GET", "/v1/me/tasks" },
        { "GET", "/v1/cases/{caseId}/events" },
        { "POST", "/v1/cases/{caseId}/events" },
        { "GET", "/v1/cases/{caseId}/events/{eventId}" },
        { "PUT", "/v1/cases/{caseId}/events/{eventId}" },
        { "PATCH", "/v1/cases/{caseId}/events/{eventId}" },
        { "DELETE", "/v1/cases/{caseId}/events/{eventId}" },
        { "GET", "/v1/firm/events" },
        { "POST", "/v1/firm/events" },
        { "GET", "/v1/firm/events/{eventId}" },
        { "PUT", "/v1/firm/events/{eventId}" },
        { "PATCH", "/v1/firm/events/{eventId}" },
        { "DELETE", "/v1/firm/events/{eventId}" },
        { "GET", "/v1/calendar" },
        { "GET", "/v1/me/calendar.ics" },
        { "POST", "/v1/me/calendar-token" },
        { "DELETE", "/v1/me/calendar-token" },
        { "GET", "/v1/cases/{caseId}/means-test" },
        { "GET", "/v1/cases/{caseId}/exemption-analysis" },
        { "GET", "/v1/cases/{caseId}/creditor-matrix" },
        { "GET", "/v1/cases/{caseId}/liens" },
        { "GET", "/v1/cases/{caseId}/standards" },
        { "GET", "/v1/cases/{caseId}/plan-calculation" },
        { "POST", "/v1/cases/{caseId}/plan-calculation" },
    });

    // Each case collection's five routes (CaseEntityRoutes).
    private static TheoryData<string, string> WithCollections(TheoryData<string, string> rows)
    {
        foreach (var collection in CaseCollections.Kinds.Select(kind => kind.Collection))
        {
            rows.Add("POST", $"/v1/cases/{{caseId}}/{collection}");
            rows.Add("GET", $"/v1/cases/{{caseId}}/{collection}");
            rows.Add("GET", $"/v1/cases/{{caseId}}/{collection}/{{entityId}}");
            rows.Add("PUT", $"/v1/cases/{{caseId}}/{collection}/{{entityId}}");
            rows.Add("DELETE", $"/v1/cases/{{caseId}}/{collection}/{{entityId}}");
        }

        return rows;
    }

    /// <summary>The portal's staff half (18.18): the firm's invitation routes, on the case.</summary>
    public static TheoryData<string, string> PortalStaffRoutes => new()
    {
        { "GET", "/v1/cases/{caseId}/portal/clients" },
        { "POST", "/v1/cases/{caseId}/portal/invitation" },
        { "POST", "/v1/cases/{caseId}/portal/clients/{subject}/resend" },
        { "DELETE", "/v1/cases/{caseId}/portal/clients/{subject}" },
    };

    /// <summary>The portal's client half (18.18): the only routes that admit a portal token.</summary>
    public static TheoryData<string, string> PortalClientRoutes => new()
    {
        { "GET", "/v1/portal/me" },
        { "GET", "/v1/portal/questionnaire" },
        { "GET", "/v1/portal/answers" },
        { "POST", "/v1/portal/answers" },
        { "PUT", "/v1/portal/answers/{answerId}" },
        { "DELETE", "/v1/portal/answers/{answerId}" },
        { "GET", "/v1/portal/document-requests" },
        { "POST", "/v1/portal/documents" },
        { "POST", "/v1/portal/documents/{documentId}/complete" },
    };

    /// <summary>The pipeline slice 18.17 maps: jobs, packets, the case summary, the extraction review queue.</summary>
    public static TheoryData<string, string> PipelineRoutes => new()
    {
        { "POST", "/v1/cases/{caseId}/jobs" },
        { "GET", "/v1/cases/{caseId}/jobs/{jobId}" },
        { "GET", "/v1/cases/{caseId}/packets" },
        { "GET", "/v1/cases/{caseId}/packets/{packetId}/url" },
        { "GET", "/v1/cases/{caseId}/summary" },
        { "GET", "/v1/cases/{caseId}/extraction/candidates" },
        { "POST", "/v1/cases/{caseId}/extraction/candidates/{candidateId}/review" },
    };

    /// <summary>The forms slice 18.15c maps: the hub, the single-form preview, the filing set.</summary>
    public static TheoryData<string, string> FormsRoutes => new()
    {
        { "GET", "/v1/cases/{caseId}/forms" },
        { "GET", "/v1/cases/{caseId}/forms/{form}/preview" },
        { "GET", "/v1/cases/{caseId}/filing-set" },
    };

    /// <summary>Electronic filing (18.19 / #530): the vault, the authorization, the approval and the resolution.</summary>
    public static TheoryData<string, string> FilingRoutes => new()
    {
        { "GET", "/v1/me/filing-credentials" },
        { "POST", "/v1/me/filing-credentials" },
        { "DELETE", "/v1/me/filing-credentials/{credentialId}" },
        { "GET", "/v1/me/filing-authorization" },
        { "POST", "/v1/me/filing-authorization" },
        { "DELETE", "/v1/me/filing-authorization" },
        { "GET", "/v1/cases/{caseId}/filing-approval" },
        { "POST", "/v1/cases/{caseId}/filing-approval" },
        { "DELETE", "/v1/cases/{caseId}/filing-approval" },
        { "POST", "/v1/cases/{caseId}/filings/{filingId}/resolution" },
    };

    public void Dispose() => _world.Dispose();

    [Theory]
    [MemberData(nameof(FirmSideRoutes))]
    [MemberData(nameof(CaseSideRoutes))]
    [MemberData(nameof(PortalStaffRoutes))]
    [MemberData(nameof(PipelineRoutes))]
    [MemberData(nameof(FormsRoutes))]
    [MemberData(nameof(FilingRoutes))]
    public async Task Every_firm_side_route_is_a_staff_route_and_401s_without_a_token(string method, string template)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(
            template.Replace("{subject}", Ids.Bob, StringComparison.Ordinal).Replace("{filingRole}", "debtor_1", StringComparison.Ordinal),
            "\\{[A-Za-z]+\\}",
            "c-1");

        var answer = await _world.SendAsync(new HttpMethod(method), path, json: "{}");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
        var endpoint = Endpoints().Single(e => e.Name == $"{method} {template}");
        Assert.Equal(PrincipalClass.Staff, endpoint.Principal);
    }

    [Theory]
    [MemberData(nameof(PortalClientRoutes))]
    public async Task Every_portal_route_is_a_client_route_and_401s_without_a_token(string method, string template)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(template, "\\{[A-Za-z]+\\}", "c-1");

        var answer = await _world.SendAsync(new HttpMethod(method), path, json: "{}");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
        Assert.Equal(PrincipalClass.Client, Endpoints().Single(e => e.Name == $"{method} {template}").Principal);
    }

    [Fact]
    public void The_route_table_is_exactly_the_firm_and_case_side_slices_plus_the_two_public_routes()
    {
        var mapped = Endpoints().Where(e => e.Name.Contains("/v1/", StringComparison.Ordinal)).Select(e => e.Name).Order(StringComparer.Ordinal);
        var expected = FirmSideRoutes.Concat(CaseSideRoutes).Concat(PortalStaffRoutes).Concat(PortalClientRoutes).Concat(PipelineRoutes).Concat(FormsRoutes).Concat(FilingRoutes).Select(row => $"{row.Data.Item1} {row.Data.Item2}").Concat(Public).Order(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
        Assert.All(Endpoints().Where(e => Public.Contains(e.Name)), e => Assert.Null(e.Principal));
    }

    private List<(string Name, PrincipalClass? Principal)> Endpoints() =>
        [.. _world.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => ($"{m} {e.RoutePattern.RawText}", e.Metadata.GetMetadata<PrincipalClass>())))];
}
