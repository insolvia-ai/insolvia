using System.Security.Cryptography;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Expenses;
using Insolvia.Core.Income;
using Insolvia.Core.Plans;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Regulatory;
using Insolvia.Forms.Tests.Support;

namespace Insolvia.Forms.Tests;

// test_packet_assembly.py's pure half, translated: the gate, which forms a
// case files, the render and the zip, output options and amendments. The
// worker's store calls are 18.17's and are tested there.
public sealed class PacketAssemblyTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DateTimeOffset PrintedAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static CaseData Ch7 => CaseDatas.Named("reference-ch7");

    private static CaseData Ch13 => CaseDatas.Named("reference-ch13");

    private static AssembledPacket Assembled(CaseData data, OutputOptions? options = null)
    {
        var (packet, problems) = PacketAssembly.Assemble(data, Today, PrintedAt, options);
        Assert.Empty(problems);
        return packet!;
    }

    private static IReadOnlyList<PacketProblem> Refused(CaseData data, OutputOptions? options = null, DateOnly? asOf = null)
    {
        var (packet, problems) = PacketAssembly.Assemble(data, asOf ?? Today, PrintedAt, options);
        Assert.Null(packet);
        Assert.NotEmpty(problems);
        return problems;
    }

    [Fact]
    public void The_reference_cases_pass_the_gate()
    {
        Assert.Empty(PacketAssembly.CompletenessProblems(Ch7));
        Assert.Empty(PacketAssembly.CompletenessProblems(Ch13));
    }

    [Fact]
    public void The_reference_case_assembles_the_full_chapter_7_set()
    {
        var packet = Assembled(Ch7);
        var names = packet.Parts.Select(p => p.Name).ToList();
        var filed = PacketAssembly.PacketFormSeriesFor(Ch7);

        Assert.Equal(19, PacketAssembly.PacketFormSeries.Count);
        Assert.Equal(19, names.Count);
        Assert.Equal("01-b101.pdf", names[0]);
        Assert.Equal("02-b121.pdf", names[1]);
        Assert.Equal(filed.Select(s => s["form/".Length..] + ".pdf"), names[..^1].Select(n => n.Split('-', 2)[1]));
        Assert.DoesNotContain("form/b106j2", filed);
        Assert.Equal(CreditorMatrices.MatrixFileName, names[^1]);
        Assert.Equal(FormTemplates.RevisionsAsOf(Today).OrderBy(p => p.Key, StringComparer.Ordinal), packet.FormRevisions.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(DollarAmounts.Resolve(Today).ReleaseId, packet.ConstantsSetId);
        Assert.True(packet.CreditorCount > 0);
    }

    [Fact]
    public void The_chapter_13_reference_case_assembles_with_its_plan()
    {
        var packet = Assembled(Ch13);
        var forms = packet.Parts.SkipLast(1).Select(p => p.Name.Split('-', 2)[1]).ToList();

        Assert.Equal(["b122c1.pdf", "b122c2.pdf", "b113.pdf"], forms[^3..]);
        Assert.DoesNotContain("b108.pdf", forms);
        Assert.DoesNotContain("b122a1.pdf", forms);
        Assert.Equal(new TextFill("30,000.00"), packet.Projections["form/b113"]["line_2_5_total"]);
        Assert.Equal("2017-12-01", packet.FormRevisions["form/b113"]);
    }

    [Fact]
    public void A_chapter_13_case_without_one_plan_is_refused()
    {
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch13 with { Plans = [] }), p => p.Source == "plans" && p.ItemId is null);
        var extra = CaseDatas.Entity(PlanRules.Kind, "plan-extra", Ch13.Case.Id, Ch13.Plans[0].Body);
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch13 with { Plans = [.. Ch13.Plans, extra] }), p => p.Source == "plans" && p.ItemId == "plan-extra");
    }

    [Fact]
    public void A_chapter_7_case_is_not_asked_for_a_plan_and_a_chapter_11_case_is_refused()
    {
        Assert.DoesNotContain("form/b113", PacketAssembly.PacketFormSeriesFor(Ch7));
        var eleven = Ch7 with { Case = Ch7.Case with { Chapter = 11 } };
        Assert.Contains(PacketAssembly.CompletenessProblems(eleven), p => p.Source == "case" && p.Field == "chapter" && p.Message.Contains("Chapter 11", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chapter_13_case_files_the_b122c_pair_in_place_of_b122a()
    {
        var thirteen = Ch7 with { Case = Ch7.Case with { Chapter = 13 } };
        var series = PacketAssembly.PacketFormSeriesFor(thirteen);

        Assert.Equal(["form/b122c1", "form/b122c2", "form/b113"], series.TakeLast(3));
        Assert.DoesNotContain("form/b108", series);
        Assert.Equal(PacketAssembly.PacketFormSeriesFor(Ch7).SkipLast(2).Where(s => s != "form/b108"), series.SkipLast(3));
        var shrunk = thirteen with { PayPeriodRecords = [.. Ch7.PayPeriodRecords.Select(e => e with { Body = e.Body with { Gross = 4500.00m } })] };
        Assert.Contains("form/b122c1", PacketAssembly.PacketFormSeriesFor(shrunk));
        Assert.DoesNotContain("form/b122c2", PacketAssembly.PacketFormSeriesFor(shrunk));
    }

    [Fact]
    public void The_pin_map_covers_both_chapters_form_sets()
    {
        Assert.Equal(FormTemplates.RevisionsAsOf(Today).Keys.Order(StringComparer.Ordinal), PacketAssembly.AllFormSeries.Order(StringComparer.Ordinal));
        Assert.Same(PacketAssembly.Chapter13FormSeries, PacketAssembly.ChapterFormSeries(13));
        Assert.Same(PacketAssembly.PacketFormSeries, PacketAssembly.ChapterFormSeries(7));
    }

    [Fact]
    public void Structural_problems_are_named_per_record()
    {
        Assert.Contains(Refused(Ch7 with { Case = Ch7.Case with { Status = CaseStatus.Filed } }), p => p.Field == "status");
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { Debtors = [] }), p => p.Source == "debtors");
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { Petitions = [] }), p => p.Source == "petitions");
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { Petitions = [.. Ch7.Petitions, .. Ch7.Petitions] }), p => p.Source == "petitions" && p.ItemId is not null);
        var extraHouse = CaseDatas.Entity(ExpenseRules.Household, "house-extra", Ch7.Case.Id, Ch7.Households[0].Body with { WhichHousehold = WhichHousehold.Main });
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { Households = [.. Ch7.Households, extraHouse] }), p => p.Source == "households" && p.ItemId == "house-extra");
        var unplaced = CaseDatas.Entity(ExpenseRules.Household, "house-unplaced", Ch7.Case.Id, Ch7.Households[0].Body with { WhichHousehold = null });
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { Households = [.. Ch7.Households, unplaced] }), p => p.Field == "which_household");
        var duplicate = CaseDatas.Entity(IncomeRules.IncomeSummary, "income-duplicate", Ch7.Case.Id, Ch7.IncomeSummaries[0].Body);
        Assert.Contains(PacketAssembly.CompletenessProblems(Ch7 with { IncomeSummaries = [.. Ch7.IncomeSummaries, duplicate] }), p => p.ItemId == "income-duplicate" && p.Field == "debtor_id");
    }

    [Fact]
    public void A_dangling_reference_is_reported_per_item()
    {
        var claim = Ch7.Claims[0] with { Body = Ch7.Claims[0].Body with { CreditorId = "creditor-gone" } };
        var problems = PacketAssembly.CompletenessProblems(Ch7 with { Claims = [claim, .. Ch7.Claims.Skip(1)] });

        var problem = Assert.Single(problems);
        Assert.Equal(new PacketProblem("claims", claim.Id, "creditor_id", "References a creditor record that does not exist — fix the reference or re-enter the record it pointed at."), problem);
    }

    [Fact]
    public void Statement_answers_gate_b108_and_b2030()
    {
        var secured = Ch7.Claims.First(e => e.Body.ClaimClass == ClaimClass.Secured);
        var unanswered = Ch7 with { Claims = [.. Ch7.Claims.Select(e => e.Id == secured.Id ? e with { Body = e.Body with { Intention = null } } : e)] };
        Assert.Contains(PacketAssembly.CompletenessProblems(unanswered), p => p.Source == "claims" && p.ItemId == secured.Id && p.Field == "intention");

        var attorney = Ch7.FilingProfessionals[0];
        var silent = Ch7 with { FilingProfessionals = [attorney with { Body = attorney.Body with { CompensationAgreed = null, CompensationShared = null } }] };
        Assert.Equal(["compensation_agreed", "compensation_shared"], PacketAssembly.CompletenessProblems(silent).Where(p => p.Source == "filing_professionals").Select(p => p.Field));
    }

    [Fact]
    public void Matrix_problems_gate_the_packet()
    {
        var first = Ch7.Creditors[0];
        var nameless = first with { Body = first.Body with { Name = null } };

        Assert.Contains(Refused(Ch7 with { Creditors = [nameless, .. Ch7.Creditors.Skip(1)] }), p => p.Source == "creditors" && p.ItemId == first.Id);
    }

    [Fact]
    public void Assembly_gates_when_no_dollar_amounts_release_is_effective() =>
        Assert.Contains(Refused(Ch7, asOf: new DateOnly(2024, 1, 1)), p => p.Source == "code/dollar-amounts");

    [Fact]
    public void Problem_json_omits_absent_keys()
    {
        var json = PacketAssembly.ProblemJson(new PacketProblem("petitions", null, string.Empty, "m"));

        Assert.Equal(["message", "source"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void J2_files_when_debtor_2_keeps_a_separate_household()
    {
        var second = CaseDatas.Entity(ExpenseRules.Household, "hh-second", Ch7.Case.Id, Ch7.Households[0].Body with { WhichHousehold = WhichHousehold.Debtor2Separate, SeparateHousehold = true });
        var withSecond = Ch7 with { Households = [.. Ch7.Households, second] };

        Assert.Contains("form/b106j2", PacketAssembly.PacketFormSeriesFor(withSecond));
        Assert.Equal(PacketAssembly.PacketFormSeries.Count + 1, Assembled(withSecond).Parts.Count);
    }

    [Fact]
    public void Assembly_is_deterministic_to_the_byte_and_the_default_options_are_the_plain_set()
    {
        var first = PacketAssembly.PacketZip(Assembled(Ch7).Parts);

        Assert.Equal(first, PacketAssembly.PacketZip(Assembled(Ch7).Parts));
        Assert.Equal(first, PacketAssembly.PacketZip(Assembled(Ch7, new OutputOptions()).Parts));
    }

    [Fact]
    public void The_zip_is_cpythons_zip_byte_for_byte()
    {
        // packet_zip((("01-b101.pdf", b"%PDF-1.3 synthetic"), ("creditor-matrix.txt", b"ACME\r\n"), ("Ñ-name.pdf", b"")))
        var zip = PacketAssembly.PacketZip([("01-b101.pdf", "%PDF-1.3 synthetic"u8.ToArray()), ("creditor-matrix.txt", "ACME\r\n"u8.ToArray()), ("Ñ-name.pdf", [])]);

        Assert.Equal(356, zip.Length);
        Assert.Equal("77e210056f1e37f7811efba030158dede687d6116bda6b9f4e1b68a2b0706acc", Convert.ToHexStringLower(SHA256.HashData(zip)));
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Equal(["01-b101.pdf", "creditor-matrix.txt", "Ñ-name.pdf"], archive.Entries.Select(e => e.FullName));
        Assert.All(archive.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
    }

    [Fact]
    public void A_draft_watermark_changes_the_bytes_but_not_the_form_set()
    {
        var plain = Assembled(Ch7);
        var draft = Assembled(Ch7, new OutputOptions { DraftWatermark = true });

        Assert.Equal(plain.Parts.Select(p => p.Name), draft.Parts.Select(p => p.Name));
        Assert.NotEqual(PacketAssembly.PacketZip(plain.Parts), PacketAssembly.PacketZip(draft.Parts));
    }

    [Fact]
    public void A_subset_renders_only_those_forms_and_an_unfiled_one_is_a_problem()
    {
        var subset = Assembled(Ch7, new OutputOptions { Forms = Core.ValueCollections.Create(["b101", "b106d"]) });
        Assert.Equal(["01-b101.pdf", "06-b106d.pdf", CreditorMatrices.MatrixFileName], subset.Parts.Select(p => p.Name));

        Assert.Contains(Refused(Ch7, new OutputOptions { Forms = Core.ValueCollections.Create(["b106j2"]) }), p => p.Source == "form/b106j2");
    }

    [Fact]
    public void Amended_only_refuses_an_unfiled_case_and_an_amendment_with_nothing_marked()
    {
        Assert.Contains(Refused(Ch7, new OutputOptions { AmendedOnly = true }), p => p.Field == "status" && p.Message.StartsWith("amendedOnly", StringComparison.Ordinal));
        var filed = Ch7 with { Case = Ch7.Case with { Status = CaseStatus.Filed } };
        Assert.Contains(Refused(filed, new OutputOptions { AmendedOnly = true }), p => p.Message.StartsWith("No schedule items are marked amended", StringComparison.Ordinal));
    }

    [Fact]
    public void Amended_only_renders_the_amended_schedules_with_sum_dec_and_a_cover()
    {
        var amended = CaseDatas.Amended(Ch7);
        var packet = Assembled(amended, new OutputOptions { AmendedOnly = true });

        Assert.Equal(["00-amendment-cover.pdf", "03-b106sum.pdf", "04-b106ab.pdf", "07-b106ef.pdf", "12-b106dec.pdf", CreditorMatrices.MatrixFileName], packet.Parts.Select(p => p.Name));
        Assert.Equal(["form/b106ab", "form/b106ef"], PacketAssembly.AmendedSeriesIds(amended, PacketAssembly.PacketFormSeriesFor(amended)));
        Assert.Single(PacketAssembly.FilteredForRender(amended, "form/b106ab").Assets);
        Assert.Equal(amended.Assets.Count, PacketAssembly.FilteredForRender(amended, "form/b106c").Assets.Count);
        Assert.False(PacketAssembly.PrintsB121(new OutputOptions { AmendedOnly = true }));
        Assert.True(PacketAssembly.PrintsB121(new OutputOptions()));
        Assert.False(PacketAssembly.PrintsB121(new OutputOptions { Forms = Core.ValueCollections.Create(["b101"]) }));
    }
}
