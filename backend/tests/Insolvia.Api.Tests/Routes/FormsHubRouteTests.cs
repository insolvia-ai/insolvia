using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Forms;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_forms_hub_routes.py, translated: what the hub and the preview REFUSE
/// (the case lookup is the only authorisation there is), the per-form rows,
/// the open-task count, the preview's 200-either-way contract, its object
/// under <c>form-previews/</c>, B121's logged full-value read, and the output
/// options. Plus the parity pin the port owes: the bytes the route stores are
/// exactly <see cref="FormsHub.RenderFormPreview"/> of the same case file.
/// </summary>
public sealed partial class FormsHubRouteTests : IAsyncLifetime, IDisposable
{
    private const string Typed = """{"source": "staff_typed"}""";

    private readonly CaseWorld _world;

    // One instant for the whole test, so the route's render and the test's
    // own agree on "today" and on the printed-date stamp. Pinned at the real
    // time and taken PER TEST (xUnit builds a fresh instance for each): a
    // staff token is minted from the real clock and checked against this one,
    // so an instant taken once per class drifted minutes behind the tokens on
    // a slow runner and every later request read as a token from the future.
    public FormsHubRouteTests()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Now = now;
        _world = new(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new PinnedClock(now));
        });
    }

    private DateTimeOffset Now { get; }

    private MemoryDocumentBlobStore Blobs => (MemoryDocumentBlobStore)_world.Service<IDocumentBlobStore>();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private Task<Answer> Forms(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/forms", subject);

    private Task<Answer> Preview(string caseId, string form, string query = "", string subject = Alice) =>
        _world.Get($"/v1/cases/{caseId}/forms/{form}/preview{query}", subject);

    private async Task<Dictionary<string, JsonObject>> RowsAsync(string caseId)
    {
        var answer = await Forms(caseId);
        Assert.True(answer.Status == 200, answer.Body);
        return answer.Object["forms"]!.AsArray().Select(row => row!.AsObject()).ToDictionary(row => row["series"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private async Task AddDebtor1Async(string caseId, bool withTaxId = false)
    {
        var body = withTaxId
            ? $$$"""{"name": {"given": "Ada"}, "tax_id": {"kind": "ssn", "value": "987-65-4321"}, "provenance": {"name.given": {{{Typed}}}, "tax_id": {{{Typed}}}}}"""
            : $$$"""{"name": {"given": "Ada"}, "provenance": {"name.given": {{{Typed}}}}}""";
        var answer = await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", body);
        Assert.True(answer.Status == 200, answer.Body);
    }

    private async Task<byte[]> TheOnlyPreviewAsync(string caseId)
    {
        var (storageRef, content) = Assert.Single(PreviewObjects());
        Assert.StartsWith($"form-previews/{caseId}/", storageRef, StringComparison.Ordinal);
        Assert.Equal("application/pdf", Blobs.ContentTypes[storageRef]);
        return await Task.FromResult(content);
    }

    private List<(string StorageRef, byte[] Content)> PreviewObjects() =>
        [.. Blobs.Objects.Keys.Where(key => key.StartsWith("form-previews/", StringComparison.Ordinal)).Select(key => (key, Blobs.GetBytesAsync(key).GetAwaiter().GetResult()!))];

    // ── Auth and ownership ──────────────────────────────────────────

    [Theory]
    [InlineData("/v1/cases/any-id/forms")]
    [InlineData("/v1/cases/any-id/forms/b101/preview")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string path) =>
        Assert.Equal(401, (await _world.Get(path, subject: null)).Status);

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await Forms("no-such-case")).Status);
        Assert.Equal(404, (await Preview("no-such-case", "b101")).Status);
    }

    [Fact]
    public async Task Another_firms_forms_are_the_same_404_as_no_case()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var missing = await Forms("no-such-case", Carol);
        var listing = await Forms(caseId, Carol);
        var preview = await Preview(caseId, "b101", subject: Carol);
        Assert.Equal(404, listing.Status);
        Assert.Equal(404, preview.Status);
        Assert.Equal(missing.Body, listing.Body);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.Outcome == AccessOutcome.Denied && e.Principal == Carol);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.FormPreviewRender && e.Outcome == AccessOutcome.Denied && e.Principal == Carol);
    }

    [Fact]
    public async Task The_static_segments_are_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await Forms(caseId);
        Assert.Equal(200, answer.Status);
        Assert.Equal(["forms"], Answer.Keys(answer.Object));
    }

    // ── GET /v1/cases/{id}/forms ────────────────────────────────────

    [Fact]
    public async Task A_bare_case_lists_every_chapter_7_form_all_blocked()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var series = (await Forms(caseId)).Object["forms"]!.AsArray().Select(row => row!["series"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(series);
        Assert.Equal(series.Distinct(StringComparer.Ordinal), series);
        Assert.Contains("form/b101", series);
        Assert.NotEmpty((await RowsAsync(caseId))["form/b101"]["problems"]!.AsArray());
    }

    [Fact]
    public async Task Each_row_is_the_hubs_summary_in_filing_order()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var stored = (await _world.StoredAsync(caseId))!;
        var data = await CaseDataReads.ReadAsync(stored, _world.Service<IDebtorStore>(), _world.Service<ICaseEntityStore>(), HttpAssert.Ct);
        var expected = new JsonArray([.. FormsHub.Summaries(data, DateOnly.FromDateTime(Now.UtcDateTime)).Select(summary => (JsonNode)Insolvia.Api.Routes.FormsHubRoutes.FormSummaryJson(summary, 0))]);

        var answer = await Forms(caseId);

        Assert.Equal(Http.FlaskJson.Serialize(new JsonObject { ["forms"] = expected }), answer.Body.TrimEnd('\n'));
    }

    [Fact]
    public async Task Count_metrics_are_zero_on_an_empty_case()
    {
        var rows = await RowsAsync(await _world.OpenCaseIdAsync());
        Assert.Equal("""{"kind":"count","value":"0"}""", rows["form/b106ab"]["metric"]!.ToJsonString());
        Assert.Equal("""{"kind":"count","value":"0"}""", rows["form/b106g"]["metric"]!.ToJsonString());
    }

    [Fact]
    public async Task Dollar_metrics_are_zero_on_an_empty_case()
    {
        var rows = await RowsAsync(await _world.OpenCaseIdAsync());
        Assert.Equal("""{"kind":"total","value":"0"}""", rows["form/b106i"]["metric"]!.ToJsonString());
        Assert.Equal("""{"kind":"total","value":"0"}""", rows["form/b106j"]["metric"]!.ToJsonString());
    }

    [Fact]
    public async Task Forms_with_no_defined_metric_omit_the_key()
    {
        var rows = await RowsAsync(await _world.OpenCaseIdAsync());
        Assert.False(rows["form/b101"].ContainsKey("metric"));
    }

    // ── The row's task count (issue #356 / 14.4) ────────────────────

    [Fact]
    public async Task Open_task_count_is_zero_with_no_anchored_tasks()
    {
        var rows = await RowsAsync(await _world.OpenCaseIdAsync());
        Assert.All(rows.Values, row => Assert.Equal(0, row["openTaskCount"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Open_task_count_counts_only_not_done_tasks_anchored_to_that_form()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(201, (await _world.Post($"/v1/cases/{caseId}/tasks", """{"subject": "Get the vehicle payoff", "formSeries": "b106d"}""")).Status);
        var second = await _world.Post($"/v1/cases/{caseId}/tasks", """{"subject": "Confirm the lien", "formSeries": "b106d"}""");
        Assert.Equal(201, second.Status);
        Assert.Equal(201, (await _world.Post($"/v1/cases/{caseId}/tasks", """{"subject": "Unrelated to any form"}""")).Status);
        // Completed — does not count.
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}/tasks/{second.Str("id")}", """{"done": true}""")).Status);

        var rows = await RowsAsync(caseId);

        Assert.Equal(1, rows["form/b106d"]["openTaskCount"]!.GetValue<int>());
        Assert.Equal(0, rows["form/b101"]["openTaskCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_problem_names_where_the_fix_belongs()
    {
        var rows = await RowsAsync(await _world.OpenCaseIdAsync());
        Assert.All(rows["form/b101"]["problems"]!.AsArray(), problem =>
        {
            Assert.False(string.IsNullOrEmpty(problem!["source"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(problem["message"]!.GetValue<string>()));
        });
    }

    [Fact]
    public async Task The_read_is_access_logged()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Forms(caseId);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.CaseId == caseId && e.Outcome == AccessOutcome.Allowed);
    }

    // ── GET /v1/cases/{id}/forms/{form}/preview ─────────────────────

    [Fact]
    public async Task A_form_this_case_does_not_file_is_not_found()
    {
        // B106J-2 files only when Debtor 2 keeps a separate household.
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await Preview(caseId, "b106j2");
        Assert.Equal(404, answer.Status);
        Assert.Equal("""{"error":"NotFoundError","message":"form not found"}""", answer.Body);
    }

    [Fact]
    public async Task An_unknown_form_key_is_not_found()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await Preview(caseId, "not-a-form")).Status);
    }

    [Fact]
    public async Task A_blocked_form_answers_200_with_problems_and_no_url()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await Preview(caseId, "b101");
        Assert.Equal(200, answer.Status);
        Assert.NotEmpty(answer.Object["problems"]!.AsArray());
        Assert.Equal(["options", "problems"], Answer.Keys(answer.Object));
        Assert.Empty(PreviewObjects());
    }

    [Fact]
    public async Task A_renderable_form_mints_a_short_lived_get()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);

        var answer = await Preview(caseId, "b106g");

        Assert.Equal(200, answer.Status);
        Assert.Equal(["expiresAt", "method", "options", "problems", "url"], Answer.Keys(answer.Object));
        Assert.Empty(answer.Object["problems"]!.AsArray());
        Assert.Equal("GET", answer.Str("method"));
        Assert.Equal(Http.FlaskJson.Millis(Now.AddSeconds(5 * 60)), answer.Str("expiresAt"));
        var minted = Blobs.Minted[^1];
        Assert.Equal("GET", minted.Method);
        Assert.Equal(5 * 60, minted.ExpiresIn);
        Assert.Matches($"^form-previews/{caseId}/[0-9a-f-]{{36}}\\.pdf$", minted.StorageRef);
    }

    [Fact]
    public async Task The_rendered_bytes_are_a_pdf()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);
        await Preview(caseId, "b106g");
        Assert.Equal("%PDF-"u8.ToArray(), (await TheOnlyPreviewAsync(caseId))[..5]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?draftWatermark=true&printDate=true")]
    public async Task The_stored_bytes_are_the_forms_engines_render_of_the_same_case_file(string query)
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);
        Assert.Equal(200, (await Preview(caseId, "b106g", query)).Status);

        var stored = (await _world.StoredAsync(caseId))!;
        var data = await CaseDataReads.ReadAsync(stored, _world.Service<IDebtorStore>(), _world.Service<ICaseEntityStore>(), HttpAssert.Ct);
        var options = query.Length == 0 ? FormOverlay.DefaultOutputOptions : FormOverlay.DefaultOutputOptions with { DraftWatermark = true, PrintDate = true };
        var (expected, problems) = FormsHub.RenderFormPreview(data, "form/b106g", DateOnly.FromDateTime(Now.UtcDateTime), Now, options);

        Assert.Empty(problems);
        Assert.Equal(expected, await TheOnlyPreviewAsync(caseId));
    }

    [Fact]
    public async Task Two_previews_of_the_same_form_get_two_distinct_objects()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);
        await Preview(caseId, "b106g");
        await Preview(caseId, "b106g");
        Assert.Equal(2, PreviewObjects().Count);
    }

    [Fact]
    public async Task The_preview_is_access_logged()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);
        await Preview(caseId, "b106g");
        Assert.Contains(_world.Events, e => e.Action == AccessAction.FormPreviewRender && e.CaseId == caseId && e.Outcome == AccessOutcome.Allowed);
    }

    // ── The tax id's full-value read (issue 13.12 / #382) ───────────
    // 987-65-4321 is from the SSA's never-issued advertising block.

    [Fact]
    public async Task Previewing_b121_performs_the_logged_read_against_the_caller()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId, withTaxId: true);

        var answer = await Preview(caseId, "b121");

        Assert.Equal(200, answer.Status);
        Assert.Empty(answer.Object["problems"]!.AsArray());
        var reads = _world.Events.Where(e => e.Action == AccessAction.TaxIdRead).Select(e => (e.Principal, e.CaseId, e.FilingRole, e.Purpose)).ToList();
        Assert.Equal([(Alice, (string?)caseId, (string?)"debtor_1", (string?)"b121")], reads);

        // The number is on the rendered form — and nowhere in the response.
        Assert.Equal("987-65-4321", FieldValue(await TheOnlyPreviewAsync(caseId), "Debtor1a.SSNum"));
        foreach (var spelling in new[] { "987654321", "987-65-4321", "987 65 4321" })
        {
            Assert.DoesNotContain(spelling, answer.Body, StringComparison.Ordinal);
        }

        // The last four, behind any mask. The URL carries random uuids, which
        // can spell any short digit run — struck out first.
        Assert.DoesNotContain("4321", Uuid().Replace(answer.Body, "<uuid>"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previewing_any_other_form_never_opens_the_envelope()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId, withTaxId: true);
        var answer = await Preview(caseId, "b106g");
        Assert.Equal(200, answer.Status);
        Assert.Empty(answer.Object["problems"]!.AsArray());
        Assert.DoesNotContain(_world.Events, e => e.Action == AccessAction.TaxIdRead);
    }

    [Fact]
    public async Task The_hub_listing_never_opens_the_envelope()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId, withTaxId: true);
        await Forms(caseId);
        Assert.DoesNotContain(_world.Events, e => e.Action == AccessAction.TaxIdRead);
    }

    // ── Output options (issue 13.11) ────────────────────────────────

    [Fact]
    public async Task No_query_params_echo_the_default_options()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await Preview(caseId, "b101");
        Assert.Equal(
            """{"amendedOnly":false,"draftWatermark":false,"printDate":false,"signElectronically":false,"signaturePages":"all"}""",
            Http.FlaskJson.Serialize(answer.Object["options"]));
    }

    [Fact]
    public async Task Query_params_are_validated_and_echoed_back()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await AddDebtor1Async(caseId);

        var answer = await Preview(caseId, "b106g", "?draftWatermark=true&printDate=true");

        Assert.Equal(200, answer.Status);
        Assert.Equal(
            """{"amendedOnly":false,"draftWatermark":true,"printDate":true,"signElectronically":false,"signaturePages":"all"}""",
            Http.FlaskJson.Serialize(answer.Object["options"]));
        var page = PdfReader.Open(new MemoryStream(await TheOnlyPreviewAsync(caseId)), PdfDocumentOpenMode.Import).Pages[0];
        Assert.Contains("(DRAFT) Tj", PageContents(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_signature_pages_query_value_is_a_400()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(400, (await Preview(caseId, "b101", "?signaturePages=sideways")).Status);
    }

    [Fact]
    public async Task A_forms_query_param_is_refused_on_the_preview_route()
    {
        // The preview already names one form in the URL.
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(400, (await Preview(caseId, "b101", "?forms=b101")).Status);
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Uuid();

    // pypdf's get_fields()[name].value: the fully-qualified field's /V, read
    // off the raw dictionaries (PDFsharp's AcroForm wrapper resolves fonts).
    private static string? FieldValue(byte[] pdf, string qualifiedName)
    {
        var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var root = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        var pending = new Stack<(PdfDictionary Field, string? Parent)>(root.Elements.OfType<PdfSharp.Pdf.Advanced.PdfReference>().Select(item => ((PdfDictionary)item.Value, (string?)null)));
        while (pending.TryPop(out var entry))
        {
            var part = entry.Field.Elements.GetString("/T");
            var name = part.Length == 0 ? entry.Parent : entry.Parent is null ? part : $"{entry.Parent}.{part}";
            if (name == qualifiedName && entry.Field.Elements.ContainsKey("/V"))
            {
                return entry.Field.Elements.GetString("/V");
            }

            foreach (var kid in entry.Field.Elements.GetArray("/Kids")?.Elements.OfType<PdfSharp.Pdf.Advanced.PdfReference>() ?? [])
            {
                pending.Push(((PdfDictionary)kid.Value, name));
            }
        }

        return null;
    }

    // Every content stream of the page, decoded — the draw ops the stamp wrote.
    private static string PageContents(PdfPage page)
    {
        var text = new StringBuilder();
        foreach (var item in page.Contents.Elements)
        {
            if (item is PdfSharp.Pdf.Advanced.PdfReference { Value: PdfDictionary { Stream: { } stream } })
            {
                text.Append(Encoding.Latin1.GetString(stream.UnfilteredValue));
            }
        }

        return text.ToString();
    }

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
