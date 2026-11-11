using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Tests;

// fields.py had no suite of its own (each case entity's tests exercised it).
// These pin the field attributes a collection body is checked with (ADR 0032)
// and the form-date parser the debtor routes still use.
public sealed class FieldsTests
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    // One test per field attribute: what each refuses, with the message the wire carries.
    [Theory]
    [InlineData("-1", "Must be a non-negative dollar amount.")]
    [InlineData("1000000000000", "Amount is too large.")]
    [InlineData("1.001", "Must have at most two decimal places.")]
    [InlineData("1200.5", null)]
    public void Money_is_a_non_negative_amount_in_cents(string sent, string? message) =>
        Assert.Equal(message, Message(new MoneyAttribute(), decimal.Parse(sent, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("10.5", "Must be between 0 and 10 per cent.")]
    [InlineData("6.3755", "Must have at most 3 decimal places.")]
    [InlineData("6.375", null)]
    public void A_rate_respects_its_maximum_and_places(string sent, string? message) =>
        Assert.Equal(message, Message(new RateAttribute(10), decimal.Parse(sent, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_count_is_a_whole_number_in_range()
    {
        Assert.Equal("Must be between 0 and 60.", Message(new CountAttribute(60), 61));
        Assert.Equal("Must be between 0 and 60.", Message(new CountAttribute(60), -1));
        Assert.Null(Message(new CountAttribute(60), 60));
    }

    [Fact]
    public void Text_is_one_line_under_its_cap()
    {
        Assert.Equal("Must be a single line.", Message(new TextAttribute(), "a\nb"));
        Assert.Equal("Must be 3 characters or fewer.", Message(new TextAttribute(3), "abcd"));
        Assert.Equal("Must be 3 characters or fewer.", Message(new TextAttribute(3), (string[])["ab", "abcd"]));
        Assert.Null(Message(new TextAttribute(3), "abc"));
    }

    [Fact]
    public void A_narrative_may_span_lines_under_its_cap()
    {
        Assert.Null(Message(new NarrativeAttribute(5), "a\nb"));
        Assert.Equal("Must be 5 characters or fewer.", Message(new NarrativeAttribute(5), "abcdef"));
    }

    [Fact]
    public void Rows_need_a_unique_addressable_id()
    {
        Assert.Equal(RowsAttribute.IdRequired, Message(new RowsAttribute(), new[] { new Row("ok"), new Row("not ok") }));
        Assert.Equal("Duplicate id.", Message(new RowsAttribute(), new[] { new Row("a"), new Row("a") }));
        Assert.Null(Message(new RowsAttribute(), new[] { new Row("a"), new Row("b") }));
    }

    private static string? Message(System.ComponentModel.DataAnnotations.ValidationAttribute attribute, object value) =>
        attribute.GetValidationResult(value, new System.ComponentModel.DataAnnotations.ValidationContext(value))?.ErrorMessage;

    private sealed record Row(string Id) : IRow;

    [Theory]
    [InlineData("2019-02-14", true)]
    [InlineData("2019-02-30", false)]
    [InlineData("20190214", false)]
    [InlineData("14/02/2019", false)]
    public void A_form_date_is_one_shape_and_a_real_day(string sent, bool valid)
    {
        var parsed = FieldParsers.FormDate(Body.Value($"\"{sent}\""), "date", _errors);
        Assert.Equal(valid, parsed is not null);
        Assert.Equal(!valid, _errors.ContainsKey("date"));
    }

    [Fact]
    public void Debtor_attribution_keeps_its_python_wire_values() =>
        Assert.Equal(["debtor_1", "debtor_2", "both", "at_least_one_plus_another"], Wire.Names<DebtorAttribution>());
}
