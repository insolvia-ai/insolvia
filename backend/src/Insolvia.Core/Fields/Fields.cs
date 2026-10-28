using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Core.Fields;

// Ported from insolvia_core/fields.py: the scalar and structured field
// parsers every entity shares. SHAPE AND TYPE ONLY, absent values accepted
// everywhere (docs/reference/case-data-model.md's storage-validation
// contract) — intake is progressive, and completeness is the forms engine's
// check. Errors accumulate into a caller-owned dictionary keyed by field
// path, so one request reports every malformed field at once.

/// <summary>Four discrete parts, never one string — the IEPD has no single-string fallback for names.</summary>
public sealed record PersonName(string? Given = null, string? Middle = null, string? Surname = null, string? Suffix = null);

/// <summary>
/// Structured parts AND a raw fallback (the IEPD's free-text form, for a
/// creditor block that will not parse). <see cref="County"/> is B101 line 5's box.
/// </summary>
public sealed record Address(
    string? Line1 = null,
    string? Line2 = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null,
    string? County = null,
    string? Raw = null);

/// <summary>The "which debtor" column that recurs across the schedules (106A/B, 106D/E/F).</summary>
public enum DebtorAttribution
{
    /// <summary>Debtor 1 only.</summary>
    [JsonStringEnumMemberName("debtor_1")]
    Debtor1,

    /// <summary>Debtor 2 only.</summary>
    [JsonStringEnumMemberName("debtor_2")]
    Debtor2,

    /// <summary>Debtor 1 and Debtor 2 only.</summary>
    [JsonStringEnumMemberName("both")]
    Both,

    /// <summary>At least one of the debtors and another — verbatim from the forms.</summary>
    [JsonStringEnumMemberName("at_least_one_plus_another")]
    AtLeastOnePlusAnother,
}

/// <summary>The shared field parsers (Python's module-level functions in <c>fields.py</c>).</summary>
public static class FieldParsers
{
    /// <summary>The default cap for a single-line free-text field.</summary>
    public const int MaxText = 200;

    private const int MaxRatePlaces = 3;

    // Money's ceiling: far above any consumer schedule, far below anything
    // decimal handles awkwardly.
    private static readonly decimal MaxMoney = 999_999_999_999.99m;

    /// <summary>
    /// A single-line free-text field, or null when absent. Empty or
    /// whitespace-only collapses to null: "cleared" and "never filled in" are
    /// the same state on a form.
    /// </summary>
    public static string? Text(JsonNode? value, string path, IDictionary<string, string> errors, int limit = MaxText)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors[path] = "Must be text.";
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (PyText.Length(stripped) > limit)
        {
            errors[path] = $"Must be {limit} characters or fewer.";
            return null;
        }

        if (stripped.Contains('\n', StringComparison.Ordinal) || stripped.Contains('\r', StringComparison.Ordinal))
        {
            errors[path] = "Must be a single line.";
            return null;
        }

        return stripped;
    }

    /// <summary>A multi-line free-text field — the "describe"/"explain" boxes. Same collapse-to-null rule as <see cref="Text"/>.</summary>
    public static string? Narrative(JsonNode? value, string path, IDictionary<string, string> errors, int limit = 2000)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors[path] = "Must be text.";
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (PyText.Length(stripped) > limit)
        {
            errors[path] = $"Must be {limit} characters or fewer.";
            return null;
        }

        return stripped;
    }

    /// <summary>
    /// A calendar date, <c>YYYY-MM-DD</c>, or null when absent. Parsed, not
    /// pattern-matched: <c>2019-02-30</c> matches every plausible regex and is
    /// not a day. A court/form date is a calendar fact, never converted to UTC.
    /// </summary>
    public static DateOnly? FormDate(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors[path] = "Must be a date.";
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(stripped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != stripped)
        {
            errors[path] = "Must be a date in YYYY-MM-DD form.";
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// A dollar amount: carried on the wire as a string (never a JSON number —
    /// a number has already been through the sender's binary floating point),
    /// returned as a <see cref="decimal"/> with scale 2 (<c>1200.00</c>).
    /// Non-negative, at most two places, at most 999,999,999,999.99.
    /// </summary>
    public static decimal? Money(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors[path] = "Must be an amount carried as a string, like \"1200.00\".";
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (!TryParseDecimal(stripped, out var parsed))
        {
            errors[path] = "Must be a dollar amount, like \"1200.00\".";
            return null;
        }

        if (parsed < 0)
        {
            errors[path] = "Must be a non-negative dollar amount.";
            return null;
        }

        if (parsed > MaxMoney)
        {
            errors[path] = "Amount is too large.";
            return null;
        }

        if (parsed.Scale > 2)
        {
            errors[path] = "Must have at most two decimal places.";
            return null;
        }

        // Scale 2 exactly: the sum's scale is the larger of the two.
        return parsed + 0.00m;
    }

    /// <summary>
    /// A rate or share in percent (<c>"8.5"</c> is eight and a half per cent),
    /// carried as a string for money's reason. At most three places, between
    /// 0 and <paramref name="maximum"/>; canonical scale is at least 2, with
    /// trailing zeros past two dropped, so <c>"8.5"</c> and <c>"8.500"</c> are
    /// the same value (8.50).
    /// </summary>
    public static decimal? Percentage(JsonNode? value, string path, IDictionary<string, string> errors, decimal maximum = 100m)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null)
        {
            errors[path] = "Must be a percentage carried as a string, like \"8.5\".";
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (!TryParseDecimal(stripped, out var parsed))
        {
            errors[path] = "Must be a percentage, like \"8.5\".";
            return null;
        }

        if (parsed < 0 || parsed > maximum)
        {
            errors[path] = $"Must be between 0 and {maximum.ToString(CultureInfo.InvariantCulture)} per cent.";
            return null;
        }

        if (parsed.Scale > MaxRatePlaces)
        {
            errors[path] = $"Must have at most {MaxRatePlaces} decimal places.";
            return null;
        }

        var canonical = (parsed + 0.000m).ToString(CultureInfo.InvariantCulture);
        while (canonical.EndsWith('0') && canonical.Split('.')[1].Length > 2)
        {
            canonical = canonical[..^1];
        }

        return decimal.Parse(canonical, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    /// <summary>A yes/no answer, or null when unanswered. <c>false</c> is an answer, not an absence.</summary>
    public static bool? Boolean(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var parsed = Json.AsBool(value);
        if (parsed is null)
        {
            errors[path] = "Must be true or false.";
        }

        return parsed;
    }

    /// <summary>A small non-negative integer — an age, a count. JSON <c>true</c> is not 1.</summary>
    public static int? WholeNumber(JsonNode? value, string path, IDictionary<string, string> errors, int maximum = 1000)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        if (!Json.TryGetWholeNumber(value, out var number))
        {
            errors[path] = "Must be a whole number.";
            return null;
        }

        if (number < 0 || number > maximum)
        {
            errors[path] = $"Must be between 0 and {maximum}.";
            return null;
        }

        return (int)number;
    }

    /// <summary>One of <paramref name="allowed"/>, or null when absent.</summary>
    public static string? Choice(JsonNode? value, IReadOnlyList<string> allowed, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return null;
        }

        var raw = Json.AsString(value);
        if (raw is null || !allowed.Contains(raw, StringComparer.Ordinal))
        {
            errors[path] = "Must be one of " + string.Join(", ", allowed) + ".";
            return null;
        }

        return raw;
    }

    /// <summary>One member of <typeparamref name="TEnum"/> by its wire string, or null when absent.</summary>
    public static TEnum? Choice<TEnum>(JsonNode? value, string path, IDictionary<string, string> errors)
        where TEnum : struct, Enum
    {
        var raw = Choice(value, Wire.Names<TEnum>(), path, errors);
        return raw is not null && Wire.TryParse<TEnum>(raw, out var member) ? member : null;
    }

    /// <summary>
    /// A "check all that apply" answer: distinct members of <typeparamref name="TEnum"/>,
    /// in the order sent. A duplicate is refused rather than collapsed.
    /// </summary>
    public static ValueCollection<TEnum> ChoiceList<TEnum>(JsonNode? value, string path, IDictionary<string, string> errors)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray array)
        {
            errors[path] = "Must be a list.";
            return [];
        }

        var chosen = new List<TEnum>();
        for (var index = 0; index < array.Count; index++)
        {
            var member = Choice<TEnum>(array[index], $"{path}[{index}]", errors);
            if (member is null)
            {
                continue;
            }

            if (chosen.Contains(member.Value))
            {
                errors[$"{path}[{index}]"] = "Duplicate choice.";
                continue;
            }

            chosen.Add(member.Value);
        }

        return chosen.ToValueCollection();
    }

    /// <summary>A list of single-line strings, attributed whole by provenance (its elements carry no ids).</summary>
    public static ValueCollection<string> StringList(JsonNode? value, string path, IDictionary<string, string> errors, int limit = MaxText)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray array)
        {
            errors[path] = "Must be a list.";
            return [];
        }

        var members = new List<string>();
        for (var index = 0; index < array.Count; index++)
        {
            var member = Text(array[index], $"{path}[{index}]", errors, limit);
            if (member is not null)
            {
                members.Add(member);
            }
        }

        return members.ToValueCollection();
    }

    /// <summary>An object, or an empty one when absent; anything else is an error.</summary>
    public static JsonObject Mapping(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            return [];
        }

        if (value is not JsonObject obj)
        {
            errors[path] = "Must be an object.";
            return [];
        }

        return obj;
    }

    /// <summary>A <see cref="PersonName"/>; absent parts stay null.</summary>
    public static PersonName ParseName(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        var raw = Mapping(value, path, errors);
        return new PersonName(
            Given: Text(raw["given"], $"{path}.given", errors),
            Middle: Text(raw["middle"], $"{path}.middle", errors),
            Surname: Text(raw["surname"], $"{path}.surname", errors),
            Suffix: Text(raw["suffix"], $"{path}.suffix", errors, limit: 20));
    }

    /// <summary>An <see cref="Address"/>; absent parts stay null.</summary>
    public static Address ParseAddress(JsonNode? value, string path, IDictionary<string, string> errors)
    {
        var raw = Mapping(value, path, errors);
        return new Address(
            Line1: Text(raw["line1"], $"{path}.line1", errors),
            Line2: Text(raw["line2"], $"{path}.line2", errors),
            City: Text(raw["city"], $"{path}.city", errors),
            State: Text(raw["state"], $"{path}.state", errors, limit: 40),
            PostalCode: Text(raw["postal_code"], $"{path}.postal_code", errors, limit: 12),
            County: Text(raw["county"], $"{path}.county", errors, limit: 64),
            Raw: Text(raw["raw"], $"{path}.raw", errors, limit: 500));
    }

    private static bool TryParseDecimal(string text, out decimal parsed) =>
        decimal.TryParse(
            text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out parsed);
}
