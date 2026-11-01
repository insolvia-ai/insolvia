using System.Globalization;
using System.Text.RegularExpressions;

namespace Insolvia.Core.Cases;

/// <summary>
/// The identifying parts of a court case number (from <c>case_numbers.py</c>).
/// <see cref="Office"/> is null only when the text left it off.
/// </summary>
/// <param name="Office">The office digit(s) before the colon.</param>
/// <param name="Year">Two digits.</param>
/// <param name="Type">Lower case; <c>bk</c> when the text left it off.</param>
/// <param name="Sequence">At least five digits, zero-padded.</param>
/// <param name="Judge">The judge's initials, upper case, when printed.</param>
public sealed record CaseNumber(string? Office, string Year, string Type, string Sequence, string? Judge = null)
{
    /// <summary>The number in CM/ECF's own form, without the judge — what the app shows and the record stores.</summary>
    public string Canonical => $"{(Office is not null ? Office + ":" : string.Empty)}{Year}-{Type}-{Sequence}";

    /// <summary>The stored match key. Refuses a number without its office: a key must name the whole case.</summary>
    public string Key(string court)
    {
        ArgumentNullException.ThrowIfNull(court);
        if (Office is null)
        {
            throw new InvalidOperationException("a case number key needs the office");
        }

        return $"{TextRules.Lower(TextRules.Strip(court))}:{Canonical}";
    }

    /// <summary>Whether two printed numbers name the same case within one court — a missing office matches any.</summary>
    public bool Matches(CaseNumber other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Year == other.Year
            && Type == other.Type
            && Sequence == other.Sequence
            && (Office is null || other.Office is null || Office == other.Office);
    }
}

/// <summary>Reading CM/ECF case numbers — the notice matcher's key (ADR 0024 PR 8, for #369).</summary>
public static partial class CaseNumbers
{
    /// <summary>The case type a number without one is read as.</summary>
    public const string DefaultType = "bk";

    /// <summary>The parts of <paramref name="text"/>, or null when it is not a CM/ECF case number.</summary>
    public static CaseNumber? Parse(string text, bool requireOffice = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var office = match.Groups["office"];
        if (!office.Success && requireOffice)
        {
            return null;
        }

        var year = match.Groups["year"].Value;
        var judge = match.Groups["judge"].Success ? match.Groups["judge"].Value
            : match.Groups["judgeParen"].Success ? match.Groups["judgeParen"].Value
            : null;
        return new CaseNumber(
            Office: office.Success ? int.Parse(office.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null,
            Year: year[^2..],
            Type: TextRules.Lower(match.Groups["type"].Success ? match.Groups["type"].Value : DefaultType),
            Sequence: int.Parse(match.Groups["sequence"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture).PadLeft(5, '0'),
            Judge: string.IsNullOrEmpty(judge) ? null : judge.ToUpperInvariant());
    }

    /// <summary><c>caseNumberKey</c> for a case: null when it has no court, no number, or one this cannot read whole.</summary>
    public static string? Key(string? court, string? caseNumber)
    {
        if (string.IsNullOrEmpty(court) || string.IsNullOrEmpty(caseNumber))
        {
            return null;
        }

        return Parse(caseNumber, requireOffice: true)?.Key(court);
    }

    /// <summary>
    /// The petition date from the court's own timestamp of the filing — the
    /// CALENDAR DATE THE COURT PRINTED, never converted to UTC. Null when the
    /// text does not start with a date.
    /// </summary>
    public static string? PetitionDate(string filedAt)
    {
        ArgumentNullException.ThrowIfNull(filedAt);
        var match = FormDate().Match(TextRules.Strip(filedAt));
        return match.Success ? match.Groups[1].Value : null;
    }

    // Office: one or two digits. Year: two or four. Type: two or three
    // letters. Sequence: one to six digits. Judge: letters, after a dash or
    // in parentheses. \s is ASCII-only in .NET's ECMAScript mode; Python's
    // re treats Unicode whitespace too, so the default mode is kept.
    [GeneratedRegex(@"^\s*(?:(?<office>[0-9]{1,2})\s*:\s*)?(?<year>[0-9]{4}|[0-9]{2})-(?:(?<type>[A-Za-z]{2,3})-)?(?<sequence>[0-9]{1,6})(?:\s*-\s*(?<judge>[A-Za-z]{1,6})|\s*\((?<judgeParen>[A-Za-z]{1,6})\))?\s*$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})(?:$|[T ])")]
    private static partial Regex FormDate();
}
