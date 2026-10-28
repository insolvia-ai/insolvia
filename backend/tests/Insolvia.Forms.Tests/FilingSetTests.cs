using System.Text;
using Insolvia.Core;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Forms.Tests.Support;
using PdfSharp.Pdf;

namespace Insolvia.Forms.Tests;

// test_filing_set.py and test_packets.py, translated, with the measurements
// (pdf_measure) and the approval digest (filing_approval's basis): the
// filing set over a real assembly of the reference case, in every district.
public sealed class FilingSetTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly CourtsRelease Release = CourtRegistry.Shipped.Latest;
    private static readonly Lazy<Packet> ReferencePacket = new(() => PacketOf(CaseDatas.Named("reference-ch7"), new OutputOptions()));

    public static TheoryData<string> Districts() => [.. Release.Districts.Select(d => d.Code)];

    public static TheoryData<string, string> Divisions() => [.. Release.Districts.SelectMany(d => d.Divisions.Select(v => (d.Code, v.Code)))];

    private static Packet PacketOf(CaseData data, OutputOptions options)
    {
        var (assembled, problems) = PacketAssembly.Assemble(data, Today, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), options);
        Assert.Empty(problems);
        var zip = PacketAssembly.PacketZip(assembled!.Parts);
        return Packets.NewPacket(
            "11111111-1111-4111-8111-111111111111",
            data.Case.Chapter,
            "job-1",
            zip.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)),
            assembled.FormRevisions,
            assembled.ConstantsSetId,
            assembled.CreditorCount,
            "subject-1",
            options,
            PdfMeasure.MeasureParts(assembled.Parts));
    }

    private static CaseData InCourt(CaseData data, string code, string? division = null)
    {
        var district = Release.District(code)!;
        return data with { Case = data.Case with { Court = code, Division = division ?? district.Divisions[0].Code, District = district.Name } };
    }

    private static FilingSet Build(CaseData data, params Packet[] packets) => FilingSets.Build(data, packets, Release, Today);

    private static Dictionary<string, ChecklistItem> Items(FilingSet set) => set.Checklist.ToDictionary(item => item.Id, StringComparer.Ordinal);

    // ── the measurements ──

    [Fact]
    public void The_assembly_measures_every_part_as_letter_text_bearing_pdf()
    {
        var parts = ReferencePacket.Value.Parts;

        Assert.Equal(CreditorMatrices.MatrixFileName, parts[^1].Name);
        Assert.All(parts, part =>
        {
            Assert.True(part.ByteSize > 0);
            Assert.Equal(64, part.Sha256!.Length);
            if (part.Name == CreditorMatrices.MatrixFileName)
            {
                Assert.Null(part.PageCount);
                return;
            }

            Assert.True(part.PageCount > 0, part.Name);
            Assert.Equal(0, part.NonLetterPages);
            Assert.Equal(0, part.PagesWithoutText);
        });
    }

    private static byte[] BlankPdf(double width, double height)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(width, height));
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void An_a4_page_without_text_is_measured_as_both_and_landscape_letter_is_letter()
    {
        var a4 = PdfMeasure.MeasurePart("scan.pdf", BlankPdf(595, 842));

        Assert.Equal((1, 1, 1), (a4.PageCount, a4.NonLetterPages, a4.PagesWithoutText));
        Assert.Equal(0, PdfMeasure.MeasurePart("wide.PDF", BlankPdf(792, 612)).NonLetterPages);
    }

    [Fact]
    public void A_text_file_is_a_size_and_a_digest_and_garbage_is_unreadable()
    {
        var text = PdfMeasure.MeasurePart("creditor-matrix.txt", "ACME\r\n"u8.ToArray());
        Assert.Equal(new PacketPart("creditor-matrix.txt", 6, Sha256: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("ACME\r\n"u8.ToArray()))), text);

        var garbage = PdfMeasure.MeasurePart("broken.pdf", "not a pdf"u8.ToArray());
        Assert.True(garbage.Unreadable);
        Assert.Equal((0, 0, 0), (garbage.PageCount, garbage.NonLetterPages, garbage.PagesWithoutText));
    }

    [Fact]
    public void A_measured_part_never_carries_the_tax_id()
    {
        var data = CaseDatas.Named("reference-ch7");
        var stored = string.Join(",", ReferencePacket.Value.Parts.Select(p => Packets.PacketPartJson(p).ToJsonString()));

        Assert.NotEmpty(data.TaxIds);
        foreach (var digits in data.TaxIds.Values)
        {
            Assert.DoesNotContain(digits, stored, StringComparison.Ordinal);
        }
    }

    // ── the filing set, per district ──

    [Theory]
    [MemberData(nameof(Divisions))]
    public void Every_division_of_every_district_builds_a_filing_set(string code, string division)
    {
        var set = Build(InCourt(CaseDatas.Named("reference-ch7"), code, division), ReferencePacket.Value);

        Assert.Equal(code, set.CourtCode);
        Assert.Equal(Release.District(code)!.Division(division)!.Name, set.DivisionName);
        Assert.Equal("ready", Items(set)["court"].Status);
    }

    [Theory]
    [MemberData(nameof(Districts))]
    public void The_documents_follow_the_packet_order_and_names_and_pass_their_checks(string code)
    {
        var data = InCourt(CaseDatas.Named("reference-ch7"), code);
        var set = Build(data, ReferencePacket.Value);
        var fromPacket = set.Documents.Where(d => d.Source == "packet").ToList();

        Assert.Equal("default", set.OrderBasis);
        Assert.Equal([.. PacketAssembly.PacketFormSeriesFor(data), FilingSets.MatrixKey], fromPacket.Select(d => d.Key));
        Assert.Equal(ReferencePacket.Value.Parts.Select(p => p.Name), fromPacket.Select(d => d.FileName));
        foreach (var document in set.Documents)
        {
            Assert.Equal(document.Source == "outside" ? ["unmeasured"] : document.Checks.Select(_ => "pass"), document.Checks.Select(c => c.Outcome));
        }

        var b121 = Assert.Single(set.Documents, d => d.Key == FilingSets.B121Series);
        Assert.Equal(Release.District(code)!.Opening.SsnStatement.Value == SsnStatementHandling.NotFiled ? "not_filed" : "restricted", b121.Handling);
        Assert.Contains("Social Security", b121.Note, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Districts))]
    public void The_checklist_covers_every_step_of_the_hand_off(string code)
    {
        var items = Items(Build(InCourt(CaseDatas.Named("reference-ch7"), code), ReferencePacket.Value));

        foreach (var id in new[] { "court", "case_data", "packet", "pdf_checks", "ssn_statement", "signature", "fee", "registration", "filing_method" })
        {
            Assert.True(items.ContainsKey(id), id);
        }

        Assert.Equal("ready", items["case_data"].Status);
        Assert.Equal("ready", items["packet"].Status);
        Assert.Equal("action", items["filing_method"].Status);
    }

    [Fact]
    public void The_default_size_cap_is_the_strictest_recorded()
    {
        var caps = Release.Districts.Select(d => d.Pdf.MaxBytes.Value).OfType<long>().ToList();

        Assert.Equal(caps.Min(), FilingSets.DefaultMaxBytes(Release));
    }

    [Fact]
    public void Without_a_packet_every_file_is_unmeasured_and_the_packet_is_missing()
    {
        var set = Build(InCourt(CaseDatas.Named("reference-ch7"), Release.Districts[0].Code));

        Assert.All(set.Documents.Where(d => d.Source == "packet"), d => Assert.Equal("unmeasured", Assert.Single(d.Checks).Outcome));
        Assert.Equal("missing", Items(set)["packet"].Status);
        Assert.Equal("missing", Items(set)["pdf_checks"].Status);
    }

    [Fact]
    public void A_draft_is_not_the_filing_set_and_a_file_over_the_cap_must_be_split()
    {
        var draft = ReferencePacket.Value with { Options = new OutputOptions { DraftWatermark = true } };
        Assert.Null(FilingSets.LatestFilingSetPacket([draft]));
        Assert.Same(ReferencePacket.Value, FilingSets.LatestFilingSetPacket([draft, ReferencePacket.Value]));

        var data = InCourt(CaseDatas.Named("reference-ch7"), Release.Districts[0].Code);
        var big = ReferencePacket.Value with { Parts = ReferencePacket.Value.Parts.Select((p, i) => i == 0 ? p with { ByteSize = 100L * 1024 * 1024 } : p).ToValueCollection() };
        var check = Build(data, big).Documents[0].Checks[0];
        Assert.Equal("fail", check.Outcome);
        Assert.Contains("split it into", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_case_says_what_is_missing_and_where_to_fix_it()
    {
        var data = CaseDatas.Named("reference-ch7");
        var items = Items(Build(InCourt(data with { Petitions = [] }, Release.Districts[0].Code), ReferencePacket.Value));

        Assert.Equal("missing", items["case_data:petitions"].Status);
        Assert.Equal("petition", items["case_data:petitions"].Link);
    }

    [Fact]
    public void A_case_with_no_court_falls_back_to_the_common_defaults()
    {
        var data = CaseDatas.Named("reference-ch7");
        var set = Build(data with { Case = data.Case with { Court = null, Division = null } }, ReferencePacket.Value);

        Assert.Null(set.CourtCode);
        Assert.Equal("default", set.MaxBytesBasis);
        Assert.Equal(FilingSets.DefaultMaxBytes(Release), set.MaxBytes);
        Assert.Equal("missing", Items(set)["court"].Status);
        Assert.Equal("confirm", Items(set)["size_limit"].Status);
    }

    [Fact]
    public void A_filed_case_is_one_state_with_its_case_number_and_date()
    {
        var data = CaseDatas.Named("reference-ch7");
        var filed = data with { Case = data.Case with { Status = CaseStatus.Filed, CaseNumber = "8:26-bk-01234", FiledAt = new DateOnly(2026, 9, 15) } };
        var item = Assert.Single(Build(filed, ReferencePacket.Value).Checklist);

        Assert.Equal("filed", item.Id);
        Assert.StartsWith("Case number 8:26-bk-01234. Filed 2026-09-15.", item.Detail, StringComparison.Ordinal);
    }

    // ── the approval digest ──

    [Fact]
    public void The_digest_is_the_sha256_of_the_canonical_document_and_moves_with_any_change()
    {
        var data = InCourt(CaseDatas.Named("reference-ch7"), Release.Districts[0].Code);
        var basis = ApprovalDigest.Basis(data, [ReferencePacket.Value], Release, Today);

        Assert.Equal(basis.Digest, ApprovalDigest.DigestOf(ApprovalDigest.BasisDocument(data, basis.FilingSet, basis.Fee)));
        Assert.Equal(basis.Digest, ApprovalDigest.Basis(data, [ReferencePacket.Value], Release, Today).Digest);
        var edited = data with { Creditors = [data.Creditors[0] with { Body = data.Creditors[0].Body with { Name = "Another Name" } }, .. data.Creditors.Skip(1)] };
        Assert.NotEqual(basis.Digest, ApprovalDigest.Basis(edited, [ReferencePacket.Value], Release, Today).Digest);
        var reassembled = ReferencePacket.Value with { Id = "22222222-2222-4222-8222-222222222222" };
        Assert.NotEqual(basis.Digest, ApprovalDigest.Basis(data, [reassembled], Release, Today).Digest);
    }

    [Fact]
    public void The_canonical_encoding_is_pythons_json_dumps()
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse("""{"b":[1,true,null,"é \"q\" \\ \n"],"a":{"z":"€","Z":0},"é":"x","scheme":"s"}""")!.AsObject();

        // json.dumps(doc, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        Assert.Equal("""{"a":{"Z":0,"z":"€"},"b":[1,true,null,"é \"q\" \\ \n"],"scheme":"s","é":"x"}""".Replace("\n", "\\n", StringComparison.Ordinal), Encoding.UTF8.GetString(ApprovalDigest.CanonicalBytes(document)));
    }

    [Fact]
    public void Blockers_name_what_must_clear_and_a_filed_case_has_only_one()
    {
        var data = InCourt(CaseDatas.Named("reference-ch7"), Release.Districts[0].Code);

        Assert.Empty(ApprovalDigest.Basis(data, [ReferencePacket.Value], Release, Today).Blockers);
        Assert.Contains("packet", ApprovalDigest.Basis(data, [], Release, Today).Blockers);
        var unmeasured = ReferencePacket.Value with { Parts = ReferencePacket.Value.Parts.Select(p => p with { Sha256 = null }).ToValueCollection() };
        Assert.Contains("file_digests", ApprovalDigest.Basis(data, [unmeasured], Release, Today).Blockers);
        var filed = data with { Case = data.Case with { Status = CaseStatus.Filed } };
        Assert.Equal(["filed"], ApprovalDigest.Basis(filed, [ReferencePacket.Value], Release, Today).Blockers);
    }

    // ── the packet record ──

    [Fact]
    public void A_new_packet_derives_its_storage_ref_and_its_name_from_the_chapter()
    {
        var packet = ReferencePacket.Value;

        Assert.Equal($"cases/11111111-1111-4111-8111-111111111111/packets/{packet.Id}", packet.StorageRef);
        Assert.Equal("chapter7-packet.zip", packet.FileName);
        Assert.Equal("chapter13-packet.zip", Packets.PacketFileName(13));
        Assert.Throws<ValidationException>(() => Packets.PacketObjectKey("case-1", "packet-1"));
        Assert.DoesNotContain("storageRef", Packets.PacketJson(packet).Select(p => p.Key));
        Assert.Equal(5, Packets.PacketJson(packet)["options"]!.AsObject().Count);
    }

    [Fact]
    public void Only_the_plain_set_or_a_dated_signed_one_is_a_filing_set()
    {
        Assert.True(Packets.IsFilingSet(new OutputOptions()));
        Assert.True(Packets.IsFilingSet(new OutputOptions { PrintDate = true, SignElectronically = true }));
        Assert.False(Packets.IsFilingSet(new OutputOptions { DraftWatermark = true }));
        Assert.False(Packets.IsFilingSet(new OutputOptions { SignaturePages = SignaturePagesMode.Only }));
        Assert.False(Packets.IsFilingSet(new OutputOptions { AmendedOnly = true }));
        Assert.False(Packets.IsFilingSet(new OutputOptions { Forms = ValueCollections.Create(["b101"]) }));
    }
}
