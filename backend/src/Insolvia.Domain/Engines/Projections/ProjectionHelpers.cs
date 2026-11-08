using System.Globalization;
using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;

namespace Insolvia.Engines.Projections;

/// <summary>
/// What every per-form projection shares (from <c>form_projections/shared.py</c>):
/// the formatting and landing helpers. Absent facts leave blank boxes,
/// present facts that cannot land are problems, and enum values map by
/// meaning through each revision's own export tables.
/// </summary>
public static class ProjectionHelpers
{
    /// <summary>The printed form of a money figure: thousands-grouped, two decimals, no currency sign.</summary>
    public static string FormatMoney(decimal value) => Exact.Grouped(value);

    /// <summary>A court date as the printed forms spell it: MM/DD/YYYY.</summary>
    public static string FormatDate(DateOnly value) => value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

    /// <summary>An absent figure is zero (Python's <c>amount(None)</c>).</summary>
    public static decimal Amount(decimal? value) => value ?? 0m;

    /// <summary>A person's name joined into one line, or null when empty.</summary>
    public static string? FullName(PersonName? name)
    {
        if (name is null)
        {
            return null;
        }

        var joined = string.Join(' ', new[] { name.Given, name.Middle, name.Surname, name.Suffix }.Where(part => !string.IsNullOrEmpty(part)));
        return joined.Length > 0 ? joined : null;
    }

    /// <summary>A debtor's name joined into one line, or null.</summary>
    public static string? FullName(Debtor? debtor) => debtor is null ? null : FullName(debtor.Body.Name);

    /// <summary>
    /// Split a narrative across a form's fixed printed lines by character
    /// count; text that cannot fit is a problem, never a truncation.
    /// </summary>
    public static List<string> WrapLines(string value, int width, int lines, string where, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return Wrap(value, lines, where, problems, candidate => TextRules.Length(candidate) <= width);
    }

    /// <summary>
    /// <see cref="WrapLines"/> for a flat release's overlay rows: the measure
    /// is Helvetica's width in points at the box's size (the engine default
    /// when the box carries none).
    /// </summary>
    public static List<string> WrapWidth(string value, double points, int lines, string where, List<string> problems, double? size = null)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return Wrap(value, lines, where, problems, candidate => (size is null ? Helvetica.Width(candidate) : Helvetica.Width(candidate, size.Value)) <= points);
    }

    /// <summary>Select a radio's export by the stored vocabulary's position in the spec's option list.</summary>
    public static OptionFill ByOrder<TEnum>(FormRelease release, string fieldId, TEnum value)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(release);
        var index = Wire.Members<TEnum>().ToList().IndexOf(value);
        return new OptionFill(release.Field(fieldId).Options[index].Value);
    }

    /// <summary>Select the export whose spec <c>maps_to_value</c> names the stored member, among the states this widget declares.</summary>
    public static OptionFill CanonicalOption(FormRelease release, string fieldId, string pdfName, string canonical)
    {
        ArgumentNullException.ThrowIfNull(release);
        var spec = release.Field(fieldId);
        var states = release.Widgets[pdfName].States;
        var matches = spec.Options.Where(option => option.MapsToValue == canonical).Select(option => option.Value).Where(states.Contains).ToList();
        if (matches.Count != 1)
        {
            throw new EngineKeyException($"{fieldId} -> {Exact.Repr(pdfName)}: no single export maps to {Exact.Repr(canonical)}");
        }

        return new OptionFill(matches[0]);
    }

    /// <summary>The No/Yes pairs, tolerant of the PDF's spellings and of the lone opaque <c>On</c> export.</summary>
    public static OptionFill YesNo(FormRelease release, string fieldId, bool answer)
    {
        ArgumentNullException.ThrowIfNull(release);
        var states = release.Field(fieldId).Options.Select(option => option.Value).ToList();
        var wanted = answer ? "yes" : "no";
        var named = states.Where(state => Classify(state) == wanted).ToList();
        if (named.Count == 1)
        {
            return new OptionFill(named[0]);
        }

        var other = answer ? "no" : "yes";
        var opaque = states.Where(state => Classify(state) is null).ToList();
        if (named.Count == 0 && opaque.Count == 1 && states.Any(state => Classify(state) == other))
        {
            return new OptionFill(opaque[0]);
        }

        throw new EngineKeyException($"{fieldId} has no {wanted} option");
    }

    /// <summary>Land an address on a street/city/state/zip box set, skipping blanks.</summary>
    public static void AddressFills(FieldValues values, Address? address, string street, string? street2, string city, string state, string zipCode, string? county = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        void Put(string? fieldId, string? value)
        {
            if (fieldId is not null && !string.IsNullOrEmpty(value))
            {
                values[fieldId] = new TextFill(value);
            }
        }

        Put(street, address?.Line1);
        Put(street2, address?.Line2);
        Put(city, address?.City);
        Put(state, address?.State);
        Put(zipCode, address?.PostalCode);
        Put(county, address?.County);
    }

    /// <summary>The case's claims of the given classes, in creation order.</summary>
    public static List<ClaimBody> ClaimsOf(CaseFile caseFile, params ClaimClass[] classes)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return [.. caseFile.Claims.Select(pair => pair.Body).Where(body => body.ClaimClass is { } claimClass && classes.Contains(claimClass))];
    }

    /// <summary>A text fill for a present value, a blank box for an absent one.</summary>
    public static TextFill? TextOrNone(string? value) => string.IsNullOrEmpty(value) ? null : new TextFill(value);

    /// <summary>Fill a repeated field's printed rows in order; overflow is a problem.</summary>
    public static void Rows(FormRelease release, FieldValues values, string fieldId, IEnumerable<string?> rowValues, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rowValues);
        ArgumentNullException.ThrowIfNull(problems);
        var spec = release.Field(fieldId);
        var fills = new List<(string Name, FieldFill Fill)>();
        var index = 0;
        foreach (var value in rowValues)
        {
            if (value is not null)
            {
                if (index >= spec.PdfNames.Count)
                {
                    problems.Add($"{fieldId}: row {index + 1} does not exist — the form prints {spec.PdfNames.Count} rows");
                }
                else
                {
                    fills.Add((spec.PdfNames[index], new TextFill(value)));
                }
            }

            index++;
        }

        if (fills.Count == 0)
        {
            return;
        }

        if (spec.PdfNames.Count > 1)
        {
            var rows = new RowFills();
            foreach (var (name, fill) in fills)
            {
                rows[name] = fill;
            }

            values[fieldId] = rows;
        }
        else
        {
            values[fieldId] = fills.First(pair => pair.Name == spec.PdfNames[0]).Fill;
        }
    }

    /// <summary>Land one instance of a repeated field on its printed row; past the printed set is a problem, a null fill a blank box.</summary>
    public static void RowFill(FormRelease release, FieldValues values, string fieldId, int index, FieldFill? fill, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(problems);
        if (fill is null)
        {
            return;
        }

        var spec = release.Field(fieldId);
        if (index >= spec.PdfNames.Count)
        {
            problems.Add($"{fieldId}: row {index + 1} does not exist — the form prints {spec.PdfNames.Count} rows");
            return;
        }

        if (spec.PdfNames.Count == 1)
        {
            values[fieldId] = fill;
            return;
        }

        values.Rows(fieldId)[spec.PdfNames[index]] = fill;
    }

    /// <summary>Python's <c>sorted(problems)</c> for the error a mapping raises.</summary>
    internal static List<string> Sorted(IEnumerable<string> problems) => [.. problems.Order(StringComparer.Ordinal)];

    /// <summary>The caption every schedule prints: the district and both debtors' names.</summary>
    internal static void Caption(CaseFile caseFile, FieldValues values)
    {
        values["caption.district"] = new TextFill(caseFile.Case.District);
        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "caption.debtor1_name"), (FilingRole.Debtor2, "caption.debtor2_name") })
        {
            if (FullName(caseFile.Debtor(role)) is { } name)
            {
                values[fieldId] = new TextFill(name);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The rule compares the lower-cased export.")]
    private static string? Classify(string state)
    {
        var lowered = state.ToLowerInvariant();
        if (lowered.StartsWith("yes", StringComparison.Ordinal))
        {
            return "yes";
        }

        return lowered.StartsWith("no", StringComparison.Ordinal) ? "no" : null;
    }

    private static List<string> Wrap(string value, int lines, string where, List<string> problems, Func<string, bool> fits)
    {
        var rows = new List<string>();
        var current = string.Empty;
        foreach (var word in TextRules.SplitWhitespace(value))
        {
            var candidate = $"{current} {word}".Trim();
            if (fits(candidate) || current.Length == 0)
            {
                current = candidate;
            }
            else
            {
                rows.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
        {
            rows.Add(current);
        }

        if (rows.Count > lines)
        {
            problems.Add($"{where}: text needs {rows.Count} lines; the form prints {lines}");
            return rows[..lines];
        }

        return rows;
    }
}
