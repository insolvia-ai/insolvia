using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Insolvia.Domain.Fields;

// The form-field rules, as DataAnnotations attributes on a record's
// properties; RecordValidator checks them. Each passes an absent value: intake
// is progressive, so nothing is required.

/// <summary>A single-line text field: at most <see cref="Limit"/> characters, no line break. On a list of strings, each item.</summary>
/// <param name="limit">The cap, in characters.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class TextAttribute(int limit = TextAttribute.DefaultLimit) : ValidationAttribute
{
    /// <summary>The cap when none is given.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The cap, in characters.</summary>
    public int Limit { get; } = limit;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) => value switch
    {
        null => ValidationResult.Success,
        string text => Check(text),
        IEnumerable items => items.Cast<object?>().Select(item => item is string text ? Check(text) : ValidationResult.Success).FirstOrDefault(result => result != ValidationResult.Success) ?? ValidationResult.Success,
        _ => new ValidationResult("Must be text."),
    };

    private ValidationResult? Check(string text) =>
        TextRules.Length(text) > Limit ? new ValidationResult($"Must be {Limit} characters or fewer.")
        : text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) ? new ValidationResult("Must be a single line.")
        : ValidationResult.Success;
}

/// <summary>A multi-line text field: at most <see cref="Limit"/> characters.</summary>
/// <param name="limit">The cap, in characters.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NarrativeAttribute(int limit = 2000) : ValidationAttribute
{
    /// <summary>The cap, in characters.</summary>
    public int Limit { get; } = limit;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) => value switch
    {
        null => ValidationResult.Success,
        string text when TextRules.Length(text) > Limit => new ValidationResult($"Must be {Limit} characters or fewer."),
        string => ValidationResult.Success,
        _ => new ValidationResult("Must be text."),
    };
}

/// <summary>A dollar amount: not negative, at most two decimal places, below a trillion.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class MoneyAttribute : ValidationAttribute
{
    // Far above any consumer schedule, far below anything decimal handles awkwardly.
    private const decimal MaxMoney = 999_999_999_999.99m;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) => value switch
    {
        null => ValidationResult.Success,
        decimal amount when amount < 0 => new ValidationResult("Must be a non-negative dollar amount."),
        decimal amount when amount > MaxMoney => new ValidationResult("Amount is too large."),
        decimal amount when amount.Scale > 2 => new ValidationResult("Must have at most two decimal places."),
        decimal => ValidationResult.Success,
        _ => new ValidationResult("Must be a dollar amount."),
    };
}

/// <summary>A rate or share in per cent (8.5 is eight and a half per cent): 0 to <see cref="Maximum"/>, at most three places.</summary>
/// <param name="maximum">The highest value, in per cent.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class RateAttribute(int maximum = 100) : ValidationAttribute
{
    private const int MaxPlaces = 3;

    /// <summary>The highest value, in per cent.</summary>
    public int Maximum { get; } = maximum;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) => value switch
    {
        null => ValidationResult.Success,
        decimal rate when rate < 0 || rate > Maximum => new ValidationResult($"Must be between 0 and {Maximum.ToString(CultureInfo.InvariantCulture)} per cent."),
        decimal rate when rate.Scale > MaxPlaces => new ValidationResult($"Must have at most {MaxPlaces} decimal places."),
        decimal => ValidationResult.Success,
        _ => new ValidationResult("Must be a percentage."),
    };
}

/// <summary>A count: a whole number from 0 to <see cref="Maximum"/>.</summary>
/// <param name="maximum">The highest value.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class CountAttribute(int maximum = 1000) : ValidationAttribute
{
    /// <summary>The highest value.</summary>
    public int Maximum { get; } = maximum;

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) => value switch
    {
        null => ValidationResult.Success,
        int count when count < 0 || count > Maximum => new ValidationResult($"Must be between 0 and {Maximum}."),
        int => ValidationResult.Success,
        _ => new ValidationResult("Must be a whole number."),
    };
}

/// <summary>A row in a list that provenance addresses by its <see cref="Id"/>, never by position.</summary>
public interface IRow
{
    /// <summary>The client-chosen id, unique in its list.</summary>
    string Id { get; }
}

/// <summary>
/// A list of <see cref="IRow"/>s: each row's id is present, one that
/// provenance can name, and unique in the list. A failure names the row
/// (<c>steps[1].id</c>) — RecordValidator joins the list's path in front.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class RowsAttribute : ValidationAttribute
{
    /// <summary>The message for a missing or malformed row id.</summary>
    public const string IdRequired =
        "Required, and must be letters, digits, hyphen or underscore — generate one per row so provenance can name it.";

    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not IEnumerable<IRow> rows)
        {
            return ValidationResult.Success;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in rows)
        {
            if (row?.Id is not { } id || !Provenance.ProvenanceRules.AddressableId().IsMatch(id))
            {
                return new ValidationResult(IdRequired, [$"[{index}].id"]);
            }

            if (!seen.Add(id))
            {
                return new ValidationResult("Duplicate id.", [$"[{index}].id"]);
            }

            index++;
        }

        return ValidationResult.Success;
    }
}
