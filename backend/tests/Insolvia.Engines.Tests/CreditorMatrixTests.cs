using System.Text;
using System.Text.Json.Nodes;
using Insolvia.Core.Courts;
using Insolvia.Core.Creditors;
using Insolvia.Core.Fields;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_creditor_matrix.py: the court
// format rules, what the generator refuses, district variance read from the
// court registry, and the per-court goldens (copied byte for byte into
// Goldens/creditor_matrix — CRLFs and all).
public sealed class CreditorMatrixTests
{
    private static readonly string Goldens = Path.Combine(AppContext.BaseDirectory, "Goldens", "creditor_matrix");

    private static readonly Dictionary<string, string> GoldenForCourt = new(StringComparer.Ordinal)
    {
        ["flsb"] = "common",
        ["ganb"] = "common",
        ["gasb"] = "common",
        ["txwb"] = "txwb",
        ["flnb"] = "txwb",
        ["flmb"] = "txwb",
        ["txsb"] = "txsb",
        ["txnb"] = "txnb",
        ["txeb"] = "txnb",
        ["gamb"] = "gamb",
    };

    public static TheoryData<string> Courts() => [.. GoldenForCourt.Keys.Order(StringComparer.Ordinal)];

    // ── Rendering the common format ──

    [Fact]
    public void A_clean_creditor_prints_name_street_and_city_line_with_crlf()
    {
        var matrix = Generate([Creditor()]);
        Assert.Equal("Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n", matrix.Content);
        Assert.Equal(1, matrix.CreditorCount);
        Assert.Empty(matrix.Problems);
    }

    [Fact]
    public void The_second_address_line_prints_between_street_and_city() =>
        Assert.Equal(["Example Bank", "4141 Fourth Ave", "Suite 900", "Wilmington DE 19850"], Lines(Generate([Creditor(line1: "4141 Fourth Ave", line2: "Suite 900")])));

    [Fact]
    public void Creditors_are_separated_by_one_blank_line() =>
        Assert.Equal(
            ["Alpha Card", "PO Box 15168", "Wilmington DE 19850", string.Empty, "Beta Finance", "PO Box 15168", "Wilmington DE 19850"],
            Lines(Generate([Creditor("Alpha Card", "creditor-1"), Creditor("Beta Finance", "creditor-2")])));

    [Fact]
    public void Entries_sort_alphabetically_by_name_ignoring_case()
    {
        var matrix = Generate([Creditor("delta hospital", "creditor-1"), Creditor("Alpha Card", "creditor-2"), Creditor("Charlie & Sons", "creditor-3")]);
        var names = Lines(matrix).Where(line => line.Length > 0 && !line.StartsWith("PO Box", StringComparison.Ordinal) && !line.StartsWith("Wilmington", StringComparison.Ordinal));
        Assert.Equal(["Alpha Card", "Charlie & Sons", "delta hospital"], names);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        (string, CreditorBody)[] creditors = [Creditor("Beta Finance", "creditor-1"), Creditor("Alpha Card", "creditor-2")];
        Assert.Equal(Generate(creditors).Content, Generate(creditors).Content);
    }

    [Fact]
    public void A_nine_digit_zip_prints_hyphenated_as_entered() =>
        Assert.Contains("Wilmington DE 19850-1234", Generate([Creditor(postalCode: "19850-1234")]).Content, StringComparison.Ordinal);

    // ── Deduplication ──

    [Fact]
    public void Blocks_that_print_identically_are_one_entry()
    {
        var matrix = Generate([Creditor(id: "creditor-1"), Creditor(id: "creditor-2")]);
        Assert.Equal(1, matrix.CreditorCount);
        Assert.Equal(1, matrix.DuplicatesOmitted);
        Assert.Single(Lines(matrix), line => line == "Example Bank");
    }

    [Fact]
    public void Case_is_the_one_difference_dedupe_ignores()
    {
        var matrix = Generate([Creditor("Example Bank", "creditor-1"), Creditor("EXAMPLE BANK", "creditor-2")]);
        Assert.Equal(1, matrix.CreditorCount);
        Assert.Equal(1, matrix.DuplicatesOmitted);
    }

    [Fact]
    public void The_same_creditor_at_two_addresses_is_two_noticing_entries()
    {
        var matrix = Generate([Creditor(id: "creditor-1", line1: "PO Box 15168"), Creditor(id: "creditor-2", line1: "PO Box 99999")]);
        Assert.Equal(2, matrix.CreditorCount);
        Assert.Equal(0, matrix.DuplicatesOmitted);
    }

    // ── What the generator refuses ──

    [Fact]
    public void A_nameless_creditor_is_a_problem_not_an_omission()
    {
        var matrix = Generate([Creditor(name: null)]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", "name")], ProblemFields(matrix));
    }

    [Fact]
    public void A_raw_only_address_names_the_address_as_unstructured()
    {
        var matrix = Generate([Creditor(line1: null, city: null, state: null, postalCode: null, raw: "Example Bank PO Box 15168 Wilmington DE")]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", "address")], ProblemFields(matrix));
        Assert.Contains("unstructured", matrix.Problems[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creditor_with_no_address_at_all_is_a_problem() =>
        Assert.Equal([("creditor-1", "address")], ProblemFields(Generate([Creditor(line1: null, city: null, state: null, postalCode: null)])));

    [Theory]
    [InlineData("line1", "address.line1")]
    [InlineData("city", "address.city")]
    [InlineData("state", "address.state")]
    [InlineData("postal_code", "address.postal_code")]
    public void Each_missing_address_part_is_named(string missing, string field)
    {
        var creditor = missing switch
        {
            "line1" => Creditor(line1: null),
            "city" => Creditor(city: null),
            "state" => Creditor(state: null),
            _ => Creditor(postalCode: null),
        };
        var matrix = Generate([creditor]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", field)], ProblemFields(matrix));
    }

    [Theory]
    [InlineData("Florida")]
    [InlineData("fl")]
    [InlineData("XX")]
    [InlineData("F")]
    public void A_state_must_be_a_two_letter_usps_abbreviation(string state) =>
        Assert.Equal([("creditor-1", "address.state")], ProblemFields(Generate([Creditor(state: state)])));

    [Theory]
    [InlineData("3330")]
    [InlineData("333015")]
    [InlineData("33301 1234")]
    [InlineData("3330A")]
    public void A_zip_must_be_five_or_hyphenated_nine_digits(string postalCode) =>
        Assert.Equal([("creditor-1", "address.postal_code")], ProblemFields(Generate([Creditor(postalCode: postalCode)])));

    [Fact]
    public void A_forty_character_line_is_the_boundary()
    {
        Assert.Empty(Generate([Creditor(new string('A', 40))]).Problems);
        var matrix = Generate([Creditor(new string('A', 41))]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", "name")], ProblemFields(matrix));
        Assert.Contains("40 characters", matrix.Problems[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_city_line_names_the_address()
    {
        var matrix = Generate([Creditor(city: new string('A', 35))]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", "address")], ProblemFields(matrix));
    }

    [Fact]
    public void Non_ascii_text_is_refused_not_transliterated()
    {
        var matrix = Generate([Creditor("Crédit Municipal")]);
        Assert.Null(matrix.Content);
        Assert.Equal([("creditor-1", "name")], ProblemFields(matrix));
        Assert.Contains("ASCII", matrix.Problems[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_creditor_list_is_a_case_level_problem()
    {
        var matrix = Generate([]);
        Assert.Null(matrix.Content);
        Assert.Equal([((string?)null, "creditors")], ProblemFields(matrix));
    }

    [Fact]
    public void One_bad_creditor_withholds_the_whole_file()
    {
        var matrix = Generate([Creditor("Alpha Card", "creditor-1"), Creditor(name: null, id: "creditor-2")]);
        Assert.Null(matrix.Content);
        Assert.Equal(0, matrix.CreditorCount);
        Assert.Equal([("creditor-2", "name")], ProblemFields(matrix));
    }

    // ── District variance is data, read from the court registry ──

    [Fact]
    public void The_southern_texas_variance_pads_to_six_line_blocks()
    {
        var lines = Lines(Generate([Creditor("Alpha Card", "creditor-1"), Creditor("Beta Finance", "creditor-2")], CreditorMatrices.FormatForCourt("txsb")));
        Assert.Equal(12, lines.Count);
        Assert.Equal([string.Empty, string.Empty, string.Empty], lines[3..6]);
        Assert.Equal("Beta Finance", lines[6]);
    }

    [Fact]
    public void The_northern_texas_variance_widens_the_separator() =>
        Assert.Contains(
            "Wilmington DE 19850\r\n\r\n\r\nBeta Finance",
            Generate([Creditor("Alpha Card", "creditor-1"), Creditor("Beta Finance", "creditor-2")], CreditorMatrices.FormatForCourt("txnb")).Content,
            StringComparison.Ordinal);

    [Fact]
    public void Each_courts_format_is_its_registry_records_verified_knobs()
    {
        Assert.Equal(6, CreditorMatrices.FormatForCourt("txsb").PadToLines);
        Assert.Equal(0, CreditorMatrices.FormatForCourt("txsb").BlankLinesBetween);
        Assert.Equal(2, CreditorMatrices.FormatForCourt("txnb").BlankLinesBetween);
        Assert.Equal(2, CreditorMatrices.FormatForCourt("txeb").BlankLinesBetween);
        Assert.True(CreditorMatrices.FormatForCourt("txeb").CaseNumberHeaderWhenSeparate);
        Assert.Equal(50, CreditorMatrices.FormatForCourt("txwb").NameLineChars);
        Assert.True(CreditorMatrices.FormatForCourt("txwb").CommaAfterCity);
        Assert.True(CreditorMatrices.FormatForCourt("flnb").CommaAfterCity);
        Assert.Equal(new MatrixFormat(BlankLinesBetween: 2, CommaAfterCity: true), CreditorMatrices.FormatForCourt("gamb"));
        Assert.Equal(CreditorMatrices.CommonFormat, CreditorMatrices.FormatForCourt("flsb"));
    }

    [Fact]
    public void The_format_follows_the_release_in_force()
    {
        // Under the first release M.D. Ga.'s knobs were unverified and the
        // common format applied (the Python suite monkeypatches `current`;
        // here the registry's clock stands on a day only that release covers).
        var first = CourtRegistry.Shipped.Get("courts/us-bankruptcy@2026-09-24");
        var registry = new CourtRegistry(CourtRegistry.Shipped.Releases, new FixedClock(first.EffectiveDate));
        Assert.Equal(CreditorMatrices.CommonFormat, CreditorMatrices.FormatForCourt("gamb", registry));
    }

    [Fact]
    public void An_unverified_knob_falls_back_to_the_common_format()
    {
        Assert.Equal(40, CreditorMatrices.FormatForCourt("flmb").MaxLineChars);
        Assert.Equal(5, CreditorMatrices.FormatForCourt("flmb").MaxCreditorLines);
    }

    [Fact]
    public void A_case_without_a_court_gets_the_common_format()
    {
        Assert.Equal(CreditorMatrices.CommonFormat, CreditorMatrices.FormatForCourt(null));
        Assert.Equal(CreditorMatrices.CommonFormat, CreditorMatrices.FormatForCourt("nyeb"));
    }

    [Fact]
    public void The_western_texas_format_widens_the_name_line_and_adds_the_comma()
    {
        var fmt = CreditorMatrices.FormatForCourt("txwb");
        const string name = "Consolidated Example Receivables Servicing LLC";
        Assert.True(name.Length > 40);
        var matrix = Generate([Creditor(name)], fmt);
        Assert.Empty(matrix.Problems);
        Assert.Equal($"{name}\r\nPO Box 15168\r\nWilmington, DE 19850\r\n", matrix.Content);
        Assert.Equal([("creditor-1", "address.line1")], ProblemFields(Generate([Creditor(line1: new string('x', 41))], fmt)));
    }

    // ── E.D. Tex.: the case number heads a separately-filed matrix ──

    [Fact]
    public void A_separately_filed_eastern_texas_matrix_starts_with_the_case_number() =>
        Assert.Equal(["26-40001", string.Empty, string.Empty, "Example Bank"], Generate([Creditor()], CreditorMatrices.FormatForCourt("txeb"), "26-40001").Content!.Split("\r\n")[..4]);

    [Fact]
    public void Without_a_case_number_eastern_texas_prints_no_header() =>
        Assert.Equal("Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n", Generate([Creditor()], CreditorMatrices.FormatForCourt("txeb")).Content);

    [Theory]
    [InlineData("flsb")]
    [InlineData("gamb")]
    [InlineData("txnb")]
    [InlineData("txsb")]
    [InlineData("txwb")]
    public void A_court_that_wants_no_header_never_prints_the_case_number(string court) =>
        Assert.DoesNotContain("26-40001", Generate([Creditor()], CreditorMatrices.FormatForCourt(court), "26-40001").Content, StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("26-40001é")]
    [InlineData("99999999999999999999999999999999999999999")]
    public void A_case_number_that_cannot_print_withholds_the_file(string caseNumber)
    {
        var matrix = Generate([Creditor()], CreditorMatrices.FormatForCourt("txeb"), caseNumber);
        Assert.Null(matrix.Content);
        Assert.Equal([((string?)null, "case_number")], ProblemFields(matrix));
    }

    // ── Per-court goldens ──

    [Fact]
    public void Every_registry_court_has_a_matrix_golden() =>
        Assert.Equal(CourtRegistry.Shipped.DistrictCodes().ToHashSet(), GoldenForCourt.Keys.ToHashSet());

    [Fact]
    public void The_common_format_renders_to_its_golden() =>
        CheckGolden("common", Generate(GoldenCase().Creditors));

    [Theory]
    [MemberData(nameof(Courts))]
    public void Each_courts_matrix_renders_to_its_golden(string court)
    {
        var matrix = Generate(GoldenCase().Creditors, CreditorMatrices.FormatForCourt(court));
        Assert.Equal(1, matrix.DuplicatesOmitted);
        CheckGolden(GoldenForCourt[court], matrix);
    }

    [Fact]
    public void The_eastern_texas_separate_filing_renders_to_its_golden()
    {
        var (creditors, caseNumber) = GoldenCase();
        CheckGolden("txeb_filed_separately", Generate(creditors, CreditorMatrices.FormatForCourt("txeb"), caseNumber));
    }

    [Fact]
    public void The_departing_courts_matrices_all_differ_byte_for_byte()
    {
        string[] names = ["common", "txsb", "txnb", "txwb", "txeb_filed_separately", "gamb"];
        Assert.Equal(names.Length, names.Select(name => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(Goldens, $"{name}.txt")))).Distinct().Count());
    }

    // ── The API representation ──

    [Fact]
    public void Matrix_json_carries_the_file_and_omits_absent_content()
    {
        Assert.True(JsonNode.DeepEquals(
            Build.Json("""{"fileName": "creditor-matrix.txt", "creditorCount": 1, "duplicatesOmitted": 0, "problems": [], "content": "Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n"}"""),
            CreditorMatrices.Json(Generate([Creditor()]))));
        var refused = CreditorMatrices.Json(Generate([Creditor(name: null)]));
        Assert.False(refused.ContainsKey("content"));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[{"creditorId": "creditor-1", "field": "name", "message": "A creditor needs a name to appear on the matrix."}]"""),
            refused["problems"]));
    }

    [Fact]
    public void Matrix_json_omits_creditor_id_on_the_case_level_problem()
    {
        var problem = CreditorMatrices.Json(Generate([]))["problems"]![0]!.AsObject();
        Assert.False(problem.ContainsKey("creditorId"));
        Assert.Equal("creditors", problem["field"]!.GetValue<string>());
    }

    private static (string, CreditorBody) Creditor(
        string? name = "Example Bank",
        string id = "creditor-1",
        string? line1 = "PO Box 15168",
        string? line2 = null,
        string? city = "Wilmington",
        string? state = "DE",
        string? postalCode = "19850",
        string? raw = null) =>
        (id, new CreditorBody(name, new Address(line1, line2, city, state, postalCode, null, raw)));

    private static GeneratedMatrix Generate((string, CreditorBody)[] creditors, MatrixFormat? format = null, string? caseNumber = null) =>
        CreditorMatrices.Generate(creditors, format, caseNumber);

    private static List<string> Lines(GeneratedMatrix matrix)
    {
        Assert.NotNull(matrix.Content);
        var lines = matrix.Content.Split("\r\n").ToList();
        lines.RemoveAt(lines.Count - 1); // Python's splitlines drops the trailing empty line.
        return lines;
    }

    private static List<(string? CreditorId, string Field)> ProblemFields(GeneratedMatrix matrix) =>
        [.. matrix.Problems.Select(problem => (problem.CreditorId, problem.Field)).Distinct()];

    private static ((string, CreditorBody)[] Creditors, string CaseNumber) GoldenCase()
    {
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(Goldens, "matrix_case.json")))!.AsObject();
        var creditors = raw["creditors"]!.AsArray().Select((node, index) =>
        {
            var address = node!["address"]!.AsObject();
            return Creditor(
                node["name"]!.GetValue<string>(),
                $"creditor-{index + 1}",
                address["line1"]?.GetValue<string>(),
                address["line2"]?.GetValue<string>(),
                address["city"]?.GetValue<string>(),
                address["state"]?.GetValue<string>(),
                address["postal_code"]?.GetValue<string>());
        }).ToArray();
        return (creditors, raw["case_number"]!.GetValue<string>());
    }

    private static void CheckGolden(string name, GeneratedMatrix matrix)
    {
        Assert.Empty(matrix.Problems);
        Assert.NotNull(matrix.Content);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Goldens, $"{name}.txt")), Encoding.ASCII.GetBytes(matrix.Content));
    }

    private sealed class FixedClock(DateOnly day) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
    }
}
