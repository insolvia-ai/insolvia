using Insolvia.Core.Fields;

namespace Insolvia.Core.Tests;

// fields.py had no suite of its own (each case entity's tests exercised it).
// These pin the places the C# port changed REPRESENTATION — money and rates
// as decimal, form dates as DateOnly — against the Python's canonical forms.
public sealed class FieldsTests
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("1200", "1200.00")]
    [InlineData("1200.5", "1200.50")]
    [InlineData(" 0.01 ", "0.01")]
    [InlineData("1e2", "100.00")]
    [InlineData("999999999999.99", "999999999999.99")]
    public void Money_is_a_scale_two_decimal(string sent, string canonical)
    {
        var parsed = FieldParsers.Money(Body.Value($"\"{sent}\""), "amount", _errors);
        Assert.Equal(canonical, parsed?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(_errors);
    }

    [Theory]
    [InlineData("1200", "Must be an amount carried as a string, like \"1200.00\".")]
    [InlineData("\"twelve\"", "Must be a dollar amount, like \"1200.00\".")]
    [InlineData("\"-1\"", "Must be a non-negative dollar amount.")]
    [InlineData("\"1000000000000\"", "Amount is too large.")]
    [InlineData("\"1.000\"", "Must have at most two decimal places.")]
    public void Money_refuses_what_python_refused(string sent, string message)
    {
        Assert.Null(FieldParsers.Money(Body.Value(sent), "amount", _errors));
        Assert.Equal(message, _errors["amount"]);
    }

    [Theory]
    [InlineData("8.5", "8.50")]
    [InlineData("8.500", "8.50")]
    [InlineData("6.375", "6.375")]
    [InlineData("10", "10.00")]
    public void A_percentage_is_canonical(string sent, string canonical) =>
        Assert.Equal(canonical, FieldParsers.Percentage(Body.Value($"\"{sent}\""), "rate", _errors)?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void A_percentage_respects_its_statutory_maximum()
    {
        Assert.Null(FieldParsers.Percentage(Body.Value("\"10.5\""), "rate", _errors, maximum: 10m));
        Assert.Equal("Must be between 0 and 10 per cent.", _errors["rate"]);
    }

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
    public void Text_collapses_blank_to_absent_and_refuses_a_second_line()
    {
        Assert.Null(FieldParsers.Text(Body.Value("\"   \""), "t", _errors));
        Assert.Empty(_errors);
        Assert.Null(FieldParsers.Text(Body.Value("\"a\\nb\""), "t", _errors));
        Assert.Equal("Must be a single line.", _errors["t"]);
    }

    [Fact]
    public void A_whole_number_is_not_a_boolean()
    {
        Assert.Null(FieldParsers.WholeNumber(Body.Value("true"), "n", _errors));
        Assert.Equal(3, FieldParsers.WholeNumber(Body.Value("3"), "m", _errors));
        Assert.Null(FieldParsers.WholeNumber(Body.Value("3.0"), "f", _errors));
        Assert.Equal(["f", "n"], _errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Debtor_attribution_keeps_its_python_wire_values() =>
        Assert.Equal(["debtor_1", "debtor_2", "both", "at_least_one_plus_another"], Wire.Names<DebtorAttribution>());
}
