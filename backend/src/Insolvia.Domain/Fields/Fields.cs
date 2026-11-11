using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Domain.Fields;

// Ported from insolvia_core/fields.py: the scalar and structured field
// parsers every entity shares. SHAPE AND TYPE ONLY, absent values accepted
// everywhere (docs/reference/case-data-model.md's storage-validation
// contract) — intake is progressive, and completeness is the forms engine's
// check. Errors accumulate into a caller-owned dictionary keyed by field
// path, so one request reports every malformed field at once.

/// <summary>Four discrete parts, never one string — the IEPD has no single-string fallback for names.</summary>
public sealed record PersonName(
    [property: Text] string? Given = null,
    [property: Text] string? Middle = null,
    [property: Text] string? Surname = null,
    [property: Text(20)] string? Suffix = null);

/// <summary>
/// Structured parts AND a raw fallback (the IEPD's free-text form, for a
/// creditor block that will not parse). <see cref="County"/> is B101 line 5's box.
/// <see cref="PostalCode"/> is <c>postalCode</c> under every serializer, the
/// snake_case ones included: an address sits in a case collection's camelCase
/// body and in a debtor's, a firm's and a client's snake_case one alike (ADR 0032).
/// </summary>
public sealed record Address(
    [property: Text] string? Line1 = null,
    [property: Text] string? Line2 = null,
    [property: Text] string? City = null,
    [property: Text(40)] string? State = null,
    [property: Text(12), JsonPropertyName("postalCode")] string? PostalCode = null,
    [property: Text(64)] string? County = null,
    [property: Text(500)] string? Raw = null);

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

        var stripped = TextRules.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (TextRules.Length(stripped) > limit)
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

        var stripped = TextRules.Strip(raw);
        if (stripped.Length == 0)
        {
            return null;
        }

        if (TextRules.Length(stripped) > limit)
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

        var stripped = TextRules.Strip(raw);
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
            PostalCode: Text(raw["postalCode"], $"{path}.postalCode", errors, limit: 12),
            County: Text(raw["county"], $"{path}.county", errors, limit: 64),
            Raw: Text(raw["raw"], $"{path}.raw", errors, limit: 500));
    }
}
