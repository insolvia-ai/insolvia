using Insolvia.Core.Cases;

namespace Insolvia.Core.Tests;

/// <summary>Reading CM/ECF case numbers, translated from test_case_numbers.py.</summary>
public sealed class CaseNumberTests
{
    private const string Key = "flmb:6:26-bk-10000";

    [Theory]
    [InlineData("6:26-bk-10000")]
    [InlineData("6:26-BK-10000")]
    [InlineData("  6:26-bk-10000  ")]
    [InlineData("6:26-bk-10000-LVV")]
    [InlineData("6:26-bk-10000 - lvv")]
    [InlineData("6:26-bk-10000 (LVV)")]
    [InlineData("06:26-bk-10000")]
    [InlineData("6:2026-bk-10000")]
    [InlineData("6:26-bk-010000")]
    [InlineData("6 : 26-bk-10000")]
    public void Every_printing_of_one_case_has_one_key(string printed) =>
        Assert.Equal(Key, CaseNumbers.Key("flmb", printed));

    [Fact]
    public void The_parts() =>
        Assert.Equal(new CaseNumber("8", "26", "bk", "01234", "RCT"), CaseNumbers.Parse("8:26-bk-01234-RCT"));

    [Fact]
    public void A_short_sequence_is_padded_and_the_court_is_lower_case() =>
        Assert.Equal("flmb:6:26-bk-00042", CaseNumbers.Key("FLMB", "6:26-bk-42"));

    [Fact]
    public void The_judge_is_not_part_of_the_case() =>
        Assert.Equal(CaseNumbers.Key("flmb", "6:26-bk-10000-ABC"), CaseNumbers.Key("flmb", "6:26-bk-10000-XYZ"));

    [Fact]
    public void The_court_is_part_of_the_key() =>
        Assert.NotEqual(CaseNumbers.Key("flmb", "6:26-bk-10000"), CaseNumbers.Key("txsb", "6:26-bk-10000"));

    [Theory]
    [InlineData("6:26-bk-10000", "6:26-bk-10001")]
    [InlineData("6:26-bk-10000", "6:25-bk-10000")]
    [InlineData("6:26-bk-10000", "3:26-bk-10000")]
    [InlineData("6:26-bk-10000", "6:26-ap-10000")]
    public void Different_cases_have_different_keys(string one, string other) =>
        Assert.NotEqual(CaseNumbers.Key("flmb", one), CaseNumbers.Key("flmb", other));

    [Fact]
    public void A_number_without_its_office_parses_but_has_no_key()
    {
        var parsed = CaseNumbers.Parse("26-10000-mg");
        Assert.Equal(new CaseNumber(null, "26", "bk", "10000", "MG"), parsed);
        Assert.Null(CaseNumbers.Parse("26-10000-mg", requireOffice: true));
        Assert.Null(CaseNumbers.Key("nysb", "26-10000-mg"));
        var error = Assert.Throws<InvalidOperationException>(() => parsed!.Key("nysb"));
        Assert.Contains("needs the office", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_matcher_treats_a_missing_office_as_any_office()
    {
        var stored = CaseNumbers.Parse("1:26-bk-10000")!;
        foreach (var printed in new[] { "26-10000", "26-bk-10000-mg", "1:26-bk-10000" })
        {
            Assert.True(CaseNumbers.Parse(printed)!.Matches(stored));
        }

        Assert.False(CaseNumbers.Parse("2:26-bk-10000")!.Matches(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pending")]
    [InlineData("10000")]
    [InlineData("6:26-bk-")]
    [InlineData("6:26-bk-1000a")]
    [InlineData("Case No. 6:26-bk-10000")]
    public void Text_that_is_not_a_case_number_has_no_key(string text)
    {
        Assert.Null(CaseNumbers.Parse(text));
        Assert.Null(CaseNumbers.Key("flmb", text));
    }

    [Fact]
    public void No_court_or_no_number_is_no_key()
    {
        Assert.Null(CaseNumbers.Key(null, "6:26-bk-10000"));
        Assert.Null(CaseNumbers.Key("flmb", null));
    }

    [Fact]
    public void The_canonical_form_drops_the_judge() =>
        Assert.Equal("6:26-bk-10000", CaseNumbers.Parse("6:2026-BK-10000-lvv")!.Canonical);

    [Theory]
    [InlineData("2099-01-15", "2099-01-15")]
    [InlineData("2099-01-15T12:01:00Z", "2099-01-15")]
    [InlineData("2099-01-15T23:10:00-05:00", "2099-01-15")]
    [InlineData("2099-01-15 23:10:00", "2099-01-15")]
    [InlineData("January 15, 2099", null)]
    [InlineData("", null)]
    public void The_petition_date_is_the_date_the_court_printed(string courtSaid, string? date) =>
        Assert.Equal(date, CaseNumbers.PetitionDate(courtSaid));

    // test_the_stored_case_carries_the_key_and_the_wire_does_not and
    // test_a_number_it_cannot_read_stores_no_key asserted on the DynamoDB item
    // (`caseNumberKey`); the key's derivation from the case's two facts is
    // what survives, and 18.10's store computes the column from it.
    [Fact]
    public void A_case_derives_its_notice_key_from_its_court_and_number()
    {
        var filed = new Case("00000000-0000-4000-8000-0000000000c1", "firm", "someone", 7, "Middle District of Florida", CaseStatus.Filed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            Court = "flmb",
            Division = "3",
            FiledAt = new DateOnly(2099, 1, 15),
            CaseNumber = "6:26-bk-10000-LVV",
        };
        Assert.Equal(Key, CaseNumbers.Key(filed.Court, filed.CaseNumber));
        Assert.Null(CaseNumbers.Key(filed.Court, "BK 26/10000"));
        Assert.Null(CaseNumbers.Key(null, filed.CaseNumber));
    }
}
