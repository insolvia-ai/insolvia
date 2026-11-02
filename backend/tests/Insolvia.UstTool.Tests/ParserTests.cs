using Insolvia.UstTool.Tests.Support;

namespace Insolvia.UstTool.Tests;

// The parsers on their own: money, the XLSX reader's edge cases, and each
// table's refusal when its shape changes — the refusals are what make a
// format change a human's problem instead of a silently wrong release.
public sealed class ParserTests
{
    private static List<IReadOnlyList<string>> Rows(params string[][] rows) => [.. rows];

    [Theory]
    [InlineData("85817", "85817.00")]
    [InlineData("85,817", "85817.00")]
    [InlineData("$1,234.5", "1234.50")]
    [InlineData(" 412 ", "412.00")]
    [InlineData("12.345", "12.34")] // truncated, as the Python did: the files carry whole dollars
    public void Money_is_a_two_decimal_string(string cell, string money)
    {
        Assert.Equal(money, Ingest.Money(cell, "here"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("007")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData("N/A")]
    public void Anything_but_a_positive_dollar_figure_is_refused(string cell)
    {
        var error = Assert.Throws<IngestException>(() => Ingest.Money(cell, "here"));

        Assert.StartsWith("here: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_resolves_shared_and_inline_strings_and_fills_gaps()
    {
        var data = Recordings.Workbook(
            new Dictionary<string, string>
            {
                ["sheet1"] = """
                    <row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1"><v> 42 </v></c><c r="D1" t="inlineStr"><is><t>in</t><r><t>line</t></r></is></c></row>
                    <row r="2"><c t="s"><v>1</v></c><c r="B2" t="s"><v>1</v></c></row>
                    <row r="3"/>
                    """,
            },
            ["<r><t>Rich </t></r><r><t>text</t></r><rPh><t>ignored?</t></rPh>", "<t xml:space=\"preserve\">  padded  </t>"]);

        var rows = Xlsx.ReadSheetRows(data);

        // Every <t> under <si> joins, the phonetic run too, as ElementTree's iter() did.
        Assert.Equal(["Rich textignored?", "", "42", "inline"], rows[0]);
        // A cell with no reference is skipped; the gap before B is filled.
        Assert.Equal(["", "padded"], rows[1]);
        Assert.Empty(rows[2]);
    }

    [Fact]
    public void The_sheets_are_taken_in_the_text_order_of_their_names()
    {
        var data = Recordings.Workbook(new Dictionary<string, string>
        {
            ["sheet2"] = """<row><c r="A1" t="inlineStr"><is><t>two</t></is></c></row>""",
            ["sheet10"] = """<row><c r="A1" t="inlineStr"><is><t>ten</t></is></c></row>""",
            ["sheet1"] = """<row><c r="A1" t="inlineStr"><is><t>one</t></is></c></row>""",
        });

        Assert.Equal("one", Xlsx.ReadSheetRows(data, 0)[0][0]);
        Assert.Equal("ten", Xlsx.ReadSheetRows(data, 1)[0][0]);
        Assert.Equal("two", Xlsx.ReadSheetRows(data, 2)[0][0]);
    }

    [Fact]
    public void The_recorded_medians_table_reads_every_state_and_territory()
    {
        var (medians, addition) = Ingest.ParseMedians(Xlsx.ReadSheetRows(Recordings.Bytes("20260401", "median_income.xlsx")));

        Assert.Equal(Ingest.StateCodes.Count, medians.Count);
        Assert.Equal(4, medians["FL"].Length);
        Assert.Matches(@"^\d+\.00$", addition);
    }

    [Fact]
    public void A_medians_table_missing_a_state_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260401", "median_income.xlsx")).Where(row => row.Count == 0 || row[0] != "Wyoming").ToList();

        var error = Assert.Throws<IngestException>(() => Ingest.ParseMedians(rows));

        Assert.Equal("medians: rows missing for ['WY']", error.Message);
    }

    [Fact]
    public void A_medians_row_with_the_wrong_width_is_refused()
    {
        var error = Assert.Throws<IngestException>(() => Ingest.ParseMedians(Rows(["Florida", "1", "2", "3"])));

        Assert.Equal("medians: Florida has 3 columns, not 4", error.Message);
    }

    [Fact]
    public void Disagreeing_excess_person_footnotes_are_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260401", "median_income.xlsx")).ToList();
        rows.Add(["* Add $1 for each individual in excess of 4."]);

        var error = Assert.Throws<IngestException>(() => Ingest.ParseMedians(rows));

        Assert.StartsWith("medians: excess-person footnotes disagree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_national_table_without_its_total_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260715", "national_expense_standards.xlsx")).Where(row => row.Count == 0 || row[0] != "Total").ToList();

        var error = Assert.Throws<IngestException>(() => Ingest.ParseNational(rows));

        Assert.Equal("national: table shape changed — review the XLSX", error.Message);
    }

    [Fact]
    public void An_oop_table_without_an_age_band_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260715", "national_oop_healthcare.xlsx")).Where(row => !row.Contains("Under 65")).ToList();

        var error = Assert.Throws<IngestException>(() => Ingest.ParseOopHealthcare(rows));

        Assert.Equal("oop healthcare: table shape changed — review the XLSX", error.Message);
    }

    [Fact]
    public void A_changed_housing_header_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260715", "housing_util_standards_FIPS.trimmed.xlsx")).ToList();
        rows[0] = [.. rows[0].Select(cell => cell == "Mort 5+" ? "Mortgage 5+" : cell)];

        var error = Assert.Throws<IngestException>(() => Ingest.ParseHousing(rows));

        Assert.StartsWith("housing: header changed — review the XLSX", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_state_with_no_counties_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260715", "housing_util_standards_FIPS.trimmed.xlsx")).Where(row => row.Count < 2 || row[1] != "GA").ToList();

        var error = Assert.Throws<IngestException>(() => Ingest.ParseHousing(rows));

        Assert.Equal("housing: no counties parsed for GA", error.Message);
    }

    [Fact]
    public void A_transportation_table_without_its_regions_is_refused()
    {
        var rows = Xlsx.ReadSheetRows(Recordings.Bytes("20260715", "transportation_standards.xlsx"))
            .Where(row => !row.Any(cell => cell.EndsWith("Region", StringComparison.Ordinal))).ToList();

        var error = Assert.Throws<IngestException>(() => Ingest.ParseTransportation(rows));

        Assert.Equal("transportation: table shape changed — review the XLSX", error.Message);
    }

    [Fact]
    public void Ch13_fractions_lose_their_float_noise_rounding_half_to_even()
    {
        var rows = Enumerable.Range(0, 50).Select(i => (IReadOnlyList<string>)[$"District {i:00}", "0.1"]).ToList();
        rows.Add(["Noisy", "9.2999999999999999E-2"]);
        rows.Add(["Half even down", "0.00125"]);
        rows.Add(["Half even up", "0.00135"]);
        rows.Add(["Effective as of April 1, 2026"]);
        rows.Add(["JUDICIAL DISTRICT", "PERCENT"]);

        var (multipliers, note) = Ingest.ParseCh13(rows);

        Assert.Equal("0.093", multipliers["Noisy"]);
        Assert.Equal("0.0012", multipliers["Half even down"]);
        Assert.Equal("0.0014", multipliers["Half even up"]);
        Assert.Equal("0.1", multipliers["District 00"]);
        Assert.Equal("Effective as of April 1, 2026", note);
        Assert.DoesNotContain("JUDICIAL DISTRICT", multipliers.Keys);
    }

    [Fact]
    public void A_ch13_multiplier_that_is_not_a_fraction_is_refused()
    {
        var error = Assert.Throws<IngestException>(() => Ingest.ParseCh13(Rows(["Middle Florida", "1.5"])));

        Assert.Equal("ch13 multiplier Middle Florida: '1.5' is not a fraction", error.Message);
    }

    [Fact]
    public void A_ch13_table_with_too_few_districts_is_refused()
    {
        var error = Assert.Throws<IngestException>(() => Ingest.ParseCh13(Rows(["Middle Florida", "0.1"])));

        Assert.StartsWith("ch13 multipliers: only 1 districts parsed", error.Message, StringComparison.Ordinal);
    }
}
