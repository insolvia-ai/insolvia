using System.Text;
using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Engines;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Projections.Forms;
using Insolvia.Forms.Tests.Support;
using PdfSharp.Pdf.IO;

namespace Insolvia.Forms.Tests;

// test_case_summary's figures (the route's wire shape is the API's, 18.x),
// test_forms_hub.py and test_amendment_cover_sheet.py, translated.
public sealed class CaseSummaryAndHubTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset PrintedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static CaseData Ch7 => CaseDatas.Named("reference-ch7");

    // ── the case summary ──

    [Fact]
    public void Every_total_is_the_figure_its_schedule_prints()
    {
        var data = Ch7;
        var file = data.ToCaseFile();
        var totals = CaseSummaries.Summarise(data).Totals;

        Assert.Equal(B106AB.RealEstateTotal(file) + B106AB.PersonalPropertyTotal(file), totals.Assets);
        Assert.Equal(B106D.SecuredTotal(file), totals.Secured);
        Assert.Equal(totals.Secured + totals.PriorityUnsecured + totals.NonpriorityUnsecured, totals.Liabilities);
        Assert.Equal(B106I.MonthlyIncomeLine12(file), totals.MonthlyIncome);
        Assert.Equal(B106J.MonthlyExpensesLine22c(file), totals.MonthlyExpenses);
        Assert.Equal(totals.MonthlyIncome - totals.MonthlyExpenses, totals.MonthlyExcess);
        Assert.Equal(totals.Assets - totals.TotalExempt, totals.TotalNonExempt);
        Assert.Equal(file.Exemptions.Sum(e => e.Amount ?? 0m), totals.TotalExempt);
    }

    [Fact]
    public void An_empty_case_totals_zero_and_is_not_ready()
    {
        var bare = new CaseData(Ch7.Case);
        var summary = CaseSummaries.Summarise(bare);

        Assert.Equal(0m, summary.Totals.Assets);
        Assert.Equal(0m, summary.Totals.Liabilities);
        Assert.Equal(0m, summary.Totals.MonthlyExcess);
        Assert.False(summary.ReadyToFile);
        Assert.Contains(summary.Problems, p => p.Source == "debtors");
        Assert.Empty(summary.Liens.Claims);
    }

    [Fact]
    public void Non_exempt_is_not_floored_at_zero()
    {
        var data = Ch7;
        var over = data with { Exemptions = [.. data.Exemptions.Select(e => e with { Body = e.Body with { Amount = 10_000_000m } })] };

        Assert.True(CaseSummaries.Summarise(over).Totals.TotalNonExempt < 0);
    }

    [Fact]
    public void The_reference_case_is_ready_to_file()
    {
        Assert.True(CaseSummaries.Summarise(Ch7).ReadyToFile);
    }

    // ── the forms hub ──

    [Fact]
    public void Every_required_form_gets_a_row_in_filing_order_with_its_title()
    {
        var rows = FormsHub.Summaries(Ch7, Today);

        Assert.Equal(PacketAssembly.PacketFormSeriesFor(Ch7), rows.Select(r => r.Series));
        Assert.All(rows, row =>
        {
            Assert.Empty(row.Problems);
            Assert.NotEmpty(row.Title);
            Assert.Equal(row.Series["form/".Length..], row.Form);
        });
    }

    [Fact]
    public void The_metrics_count_what_each_schedule_prints_and_total_i_and_j()
    {
        var data = Ch7;
        var rows = FormsHub.Summaries(data, Today).ToDictionary(r => r.Series, StringComparer.Ordinal);
        var totals = CaseSummaries.Summarise(data).Totals;

        Assert.Equal(new FormMetric("count", data.Assets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), rows["form/b106ab"].Metric);
        Assert.Equal(new FormMetric("count", data.Claims.Count(c => c.Body.ClaimClass == ClaimClass.Secured).ToString(System.Globalization.CultureInfo.InvariantCulture)), rows["form/b106d"].Metric);
        Assert.Equal(new FormMetric("total", Engines.Exact.F(totals.MonthlyIncome)), rows["form/b106i"].Metric);
        Assert.Equal(new FormMetric("total", Engines.Exact.F(totals.MonthlyExpenses)), rows["form/b106j"].Metric);
        Assert.Null(rows["form/b101"].Metric);
        Assert.Null(rows["form/b107"].Metric);
    }

    [Fact]
    public void A_missing_debtor_1_blocks_every_form_and_a_missing_petition_only_b101()
    {
        var noDebtors = FormsHub.GroupProblems(Ch7 with { Debtors = [] });
        Assert.All(noDebtors.Values, problems => Assert.Contains(problems, p => p.Source == "debtors"));

        var noPetition = FormsHub.GroupProblems(Ch7 with { Petitions = [] });
        Assert.Equal(["form/b101"], noPetition.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key));
    }

    [Fact]
    public void A_pinned_case_resolves_its_pin_and_a_floating_one_the_release_in_force()
    {
        var floating = FormsHub.ResolveCaseForm(Ch7.Case, "form/b101", Today);
        Assert.Equal(FormTemplates.Resolve("form/b101", Today), floating);

        var pinned = Ch7.Case with { FormRevisions = new Dictionary<string, string> { ["form/b101"] = floating.Pin }.ToValueDictionary() };
        Assert.Equal(floating, FormsHub.ResolveCaseForm(pinned, "form/b101", new DateOnly(2099, 1, 1)));
        Assert.Throws<EngineLookupException>(() => FormsHub.ResolveCaseForm(Ch7.Case, "form/b101", new DateOnly(2000, 1, 1)));
    }

    [Fact]
    public void A_preview_is_the_packets_own_bytes_or_its_problems_never_both()
    {
        var (pdf, problems) = FormsHub.RenderFormPreview(Ch7, "form/b106d", Today, PrintedAt);
        var (packet, _) = PacketAssembly.Assemble(Ch7, Today, PrintedAt);

        Assert.Empty(problems);
        Assert.Equal(packet!.Parts.Single(p => p.Name == "06-b106d.pdf").Content, pdf);
        var (blocked, why) = FormsHub.RenderFormPreview(Ch7 with { Petitions = [] }, "form/b101", Today, PrintedAt);
        Assert.Null(blocked);
        Assert.Contains(why, p => p.Source == "petitions");
        var (none, noSignature) = FormsHub.RenderFormPreview(Ch7, "form/b106ab", Today, PrintedAt, new OutputOptions { SignaturePages = SignaturePagesMode.Only });
        Assert.Null(none);
        Assert.StartsWith("This form has no signature lines", Assert.Single(noSignature).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amendment_preview_prints_what_the_amendment_prints_and_a_plain_one_of_a_filed_case_is_refused()
    {
        var amended = CaseDatas.Amended(Ch7);
        var options = new OutputOptions { AmendedOnly = true };
        var (pdf, problems) = FormsHub.RenderFormPreview(amended, "form/b106ab", Today, PrintedAt, options);
        var (packet, _) = PacketAssembly.Assemble(amended, Today, PrintedAt, options);

        Assert.Empty(problems);
        Assert.Equal(packet!.Parts.Single(p => p.Name == "04-b106ab.pdf").Content, pdf);
        Assert.Contains(FormsHub.RenderFormPreview(amended, "form/b106ab", Today, PrintedAt).Problems, p => p.Field == "status");
        Assert.StartsWith("This form is not part of an amendment", Assert.Single(FormsHub.RenderFormPreview(amended, "form/b106c", Today, PrintedAt, options).Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_object_key_is_scoped_and_built_from_uuids_only()
    {
        var id = FormsHub.NewPreviewId();

        Assert.Equal($"form-previews/11111111-1111-4111-8111-111111111111/{id}.pdf", FormsHub.FormPreviewObjectKey("11111111-1111-4111-8111-111111111111", id));
        Assert.Throws<Core.ValidationException>(() => FormsHub.FormPreviewObjectKey("../etc", id));
    }

    // ── the amendment cover sheet ──

    private static string CoverText(byte[] pdf)
    {
        var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
        return Encoding.Latin1.GetString(PdfDraw.ConcatenatedContents(doc.Pages[0])!);
    }

    [Fact]
    public void The_cover_sheet_is_one_page_naming_debtors_forms_dates_and_its_own_status()
    {
        var file = Ch7.ToCaseFile() with { };
        file = file with { Case = file.Case with { FiledAt = new DateOnly(2026, 9, 15) } };
        var releases = new[] { FormTemplates.Latest("form/b106sum"), FormTemplates.Latest("form/b106ab") };
        var text = CoverText(AmendmentCoverSheet.Render(file, releases, PrintedAt));

        Assert.Contains("(AMENDMENT COVER SHEET) Tj", text, StringComparison.Ordinal);
        Assert.Contains("not an official bankruptcy", text, StringComparison.Ordinal);
        Assert.Contains("(Debtor 1: Ada Quinn Lovelace) Tj", text, StringComparison.Ordinal);
        Assert.Contains("(Debtor 2: Ben Lovelace Jr.) Tj", text, StringComparison.Ordinal);
        Assert.Contains("(Filing date: 09/15/2026) Tj", text, StringComparison.Ordinal);
        Assert.Contains("(Generated 2026-10-27 12:00 UTC) Tj", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("B106Sum", StringComparison.Ordinal) < text.IndexOf("106A/B", StringComparison.Ordinal));
        Assert.Contains("(Filing date: Not yet recorded) Tj", CoverText(AmendmentCoverSheet.Render(Ch7.ToCaseFile(), releases, PrintedAt)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cover_sheet_is_deterministic_and_survives_a_name_outside_cp1252()
    {
        var file = Ch7.ToCaseFile();
        var debtor = file.Debtors[0];
        var renamed = file with { Debtors = [debtor with { Body = debtor.Body with { Name = debtor.Body.Name with { Given = "Nguyễn" } } }, .. file.Debtors.Skip(1)] };
        var releases = new[] { FormTemplates.Latest("form/b106ab") };

        Assert.Equal(AmendmentCoverSheet.Render(file, releases, PrintedAt), AmendmentCoverSheet.Render(file, releases, PrintedAt));
        Assert.Contains("Nguy?n", CoverText(AmendmentCoverSheet.Render(renamed, releases, PrintedAt)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20, new[] { "- B106A/B - Schedule", "A/B: Property, a", "deliberately long-", "winded well-known", "title with", "hyphenated-words and", "anti-dis-", "establishment" })]
    [InlineData(32, new[] { "- B106A/B - Schedule A/B:", "Property, a deliberately long-", "winded well-known title with", "hyphenated-words and anti-dis-", "establishment" })]
    [InlineData(76, new[] { "- B106A/B - Schedule A/B: Property, a deliberately long-winded well-known", "title with hyphenated-words and anti-dis-establishment" })]
    public void Wrapping_is_cpythons_textwrap(int width, string[] lines) =>
        Assert.Equal(lines, TextWrap.Wrap("- B106A/B - Schedule A/B: Property, a deliberately long-winded well-known title with hyphenated-words and anti-dis-establishment", width));
}
