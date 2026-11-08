namespace Insolvia.Engines.Tests;

// The Python semantics the ports lean on, pinned against what CPython's
// decimal module answers (each expectation below is the Python REPL's):
// the four quantize modes at the .xx5 boundary, the format spec's half-even
// rounding, the sign a negative value keeps through quantize, and repr.
public sealed class ExactTests
{
    [Theory]
    [InlineData("2.675", Rounding.HalfUp, "2.68")]
    [InlineData("2.665", Rounding.HalfUp, "2.67")]
    [InlineData("-2.675", Rounding.HalfUp, "-2.68")]
    [InlineData("2.675", Rounding.HalfEven, "2.68")]
    [InlineData("2.665", Rounding.HalfEven, "2.66")]
    [InlineData("-2.665", Rounding.HalfEven, "-2.66")]
    [InlineData("2.679", Rounding.Down, "2.67")]
    [InlineData("-2.679", Rounding.Down, "-2.67")]
    [InlineData("2.671", Rounding.Up, "2.68")]
    [InlineData("-2.671", Rounding.Up, "-2.68")]
    [InlineData("2.67", Rounding.Up, "2.67")]
    [InlineData("5", Rounding.HalfUp, "5.00")]
    [InlineData("0.005", Rounding.HalfUp, "0.01")]
    [InlineData("0.005", Rounding.HalfEven, "0.00")]
    [InlineData("-0.004", Rounding.HalfUp, "-0.00")]
    [InlineData("-0.005", Rounding.HalfEven, "-0.00")]
    public void Quantize_to_the_cent_matches_python(string value, Rounding mode, string expected) =>
        Assert.Equal(expected, Exact.Money(Exact.Dec(value), mode));

    [Theory]
    [InlineData("1234.5", "1,234.50")]
    [InlineData("0", "0.00")]
    [InlineData("-1234567.891", "-1,234,567.89")]
    [InlineData("1.005", "1.00")]
    [InlineData("1.015", "1.02")]
    [InlineData("999999.995", "1,000,000.00")]
    public void Grouped_formatting_rounds_half_even(string value, string expected) =>
        Assert.Equal(expected, Exact.Grouped(Exact.Dec(value)));

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("both ' and \"", "'both \\' and \"'")]
    [InlineData("back\\slash", "'back\\\\slash'")]
    public void Repr_quotes_the_way_python_does(string value, string expected) => Assert.Equal(expected, Exact.Repr(value));

    [Fact]
    public void A_key_errors_str_is_its_repr() =>
        Assert.Equal("'no row'", Exact.Str(new EngineKeyException("no row")));

    [Fact]
    public void Integral_powers_multiply_out() =>
        Assert.Equal(Math.Round(1.005m * 1.005m * 1.005m * 1.005m * 1.005m, 20), Math.Round(Exact.Pow(1.005m, 5), 20));

    [Fact]
    public void Max_and_min_keep_the_first_of_equals()
    {
        Assert.Equal(0, Exact.Max(0m, 0.00m).Scale);
        Assert.Equal(2, Exact.Max(0.00m, 0m).Scale);
        Assert.Equal(2, Exact.Min(1.00m, 1m).Scale);
    }
}
