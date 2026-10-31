using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Courts;
using Insolvia.Core.Creditors;

namespace Insolvia.Engines.CreditorMatrix;

/// <summary>
/// One district family's matrix shape, as numbers rather than branches.
/// <see cref="PadToLines"/> is S.D. Tex.'s fixed six-line blocks;
/// <see cref="NameLineChars"/> and <see cref="CommaAfterCity"/> W.D. Tex.'s and
/// N.D. Fla.'s knobs; <see cref="CaseNumberHeaderWhenSeparate"/> E.D. Tex.'s
/// separately-filed header.
/// </summary>
public sealed record MatrixFormat(
    int MaxLineChars = 40,
    int MaxCreditorLines = 5,
    int BlankLinesBetween = 1,
    int? PadToLines = null,
    int? NameLineChars = null,
    bool CommaAfterCity = false,
    bool CaseNumberHeaderWhenSeparate = false);

/// <summary>One reason one creditor cannot go on the matrix as recorded; <see cref="Field"/> is the body path the fix belongs to.</summary>
public sealed record MatrixProblem(string? CreditorId, string Field, string Message);

/// <summary>A generation outcome: the exact file text only when there are no problems.</summary>
public sealed record GeneratedMatrix(string? Content, int CreditorCount, int DuplicatesOmitted, IReadOnlyList<MatrixProblem> Problems);

/// <summary>
/// Creditor matrix generation (from <c>core/creditor_matrix.py</c>, issue
/// #94) — the court's mailing list. District variance is data in the court
/// registry; violations are reported, never repaired; the one merge is
/// blocks that would print identically.
/// </summary>
public static partial class CreditorMatrices
{
    /// <summary>The upload name.</summary>
    public const string MatrixFileName = "creditor-matrix.txt";

    /// <summary>The format every launch district's instructions accept.</summary>
    public static readonly MatrixFormat CommonFormat = new();

    /// <summary>USPS state and possession abbreviations (Publication 28, Appendix B).</summary>
    public static readonly IReadOnlySet<string> UsStateAbbreviations = new HashSet<string>(StringComparer.Ordinal)
    {
        "AL", "AK", "AS", "AZ", "AR", "CA", "CO", "CT", "DE", "DC",
        "FL", "FM", "GA", "GU", "HI", "ID", "IL", "IN", "IA", "KS",
        "KY", "LA", "ME", "MH", "MD", "MA", "MI", "MN", "MS", "MO",
        "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "MP",
        "OH", "OK", "OR", "PA", "PW", "PR", "RI", "SC", "SD", "TN",
        "TX", "UT", "VT", "VI", "VA", "WA", "WV", "WI", "WY",
        "AA", "AE", "AP",
    };

    // E.D. Tex. prints exactly two blank lines under the case number.
    private const int CaseNumberBlankLines = 2;

    /// <summary>The format a case's court wants, from the registry's verified matrix facts, the common format otherwise.</summary>
    public static MatrixFormat FormatForCourt(string? courtCode, CourtRegistry? registry = null)
    {
        if (courtCode is null)
        {
            return CommonFormat;
        }

        var record = (registry ?? CourtRegistry.Shipped).District(courtCode);
        if (record is null)
        {
            return CommonFormat;
        }

        var rules = record.Matrix;
        static int Number(Fact<long?> fact, int fallback) => fact.Verified && fact.Value is { } value ? (int)value : fallback;
        static bool Flag(Fact<bool?> fact, bool fallback) => fact.Verified && fact.Value is { } value ? value : fallback;

        return new MatrixFormat(
            MaxLineChars: Number(rules.MaxLineChars, CommonFormat.MaxLineChars),
            MaxCreditorLines: Number(rules.MaxCreditorLines, CommonFormat.MaxCreditorLines),
            BlankLinesBetween: Number(rules.BlankLinesBetween, CommonFormat.BlankLinesBetween),
            PadToLines: rules.PadToLines.Verified ? (int?)rules.PadToLines.Value : null,
            NameLineChars: rules.NameLineChars.Verified ? (int?)rules.NameLineChars.Value : null,
            CommaAfterCity: Flag(rules.CommaAfterCity, CommonFormat.CommaAfterCity),
            CaseNumberHeaderWhenSeparate: Flag(rules.CaseNumberHeaderWhenSeparate, CommonFormat.CaseNumberHeaderWhenSeparate));
    }

    /// <summary>
    /// The matrix for one case's creditor list, or every reason there is not
    /// one — never both, never a partial file. CRLF line endings, a final
    /// newline, entries sorted case-insensitively by name.
    /// </summary>
    public static GeneratedMatrix Generate(IReadOnlyList<(string Id, CreditorBody Body)> creditors, MatrixFormat? format = null, string? caseNumber = null)
    {
        ArgumentNullException.ThrowIfNull(creditors);
        var fmt = format ?? CommonFormat;
        var problems = new List<MatrixProblem>();
        if (creditors.Count == 0)
        {
            problems.Add(new MatrixProblem(null, "creditors", "The case has no creditors — a matrix must list every creditor before it can be filed."));
        }

        var header = fmt.CaseNumberHeaderWhenSeparate ? caseNumber : null;
        if (header is not null)
        {
            problems.AddRange(CaseNumberProblems(header, fmt));
        }

        var printable = new List<string[]>();
        foreach (var (id, body) in creditors)
        {
            var creditorProblems = CreditorProblems(id, body);
            if (creditorProblems.Count > 0)
            {
                problems.AddRange(creditorProblems);
                continue;
            }

            var lineProblems = LineProblems(id, body, fmt);
            if (lineProblems.Count > 0)
            {
                problems.AddRange(lineProblems);
                continue;
            }

            printable.Add([.. BlockLines(body, fmt).Select(line => line.Line)]);
        }

        if (problems.Count > 0)
        {
            return new GeneratedMatrix(null, 0, 0, problems);
        }

        printable.Sort((left, right) =>
        {
            var byName = string.CompareOrdinal(TextRules.Casefold(left[0]), TextRules.Casefold(right[0]));
            return byName != 0 ? byName : CompareBlocks(left, right);
        });
        var blocks = new List<string[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var block in printable)
        {
            var key = string.Join("\n", block.Select(TextRules.Casefold));
            if (!seen.Add(key))
            {
                duplicates++;
                continue;
            }

            blocks.Add(block);
        }

        var rendered = new List<string>();
        foreach (var block in blocks)
        {
            var lines = block.ToList();
            if (fmt.PadToLines is { } pad)
            {
                lines.AddRange(Enumerable.Repeat(string.Empty, Math.Max(0, pad - lines.Count)));
            }

            rendered.Add(string.Join("\r\n", lines));
        }

        var separator = string.Concat(Enumerable.Repeat("\r\n", fmt.BlankLinesBetween + 1));
        var content = string.Join(separator, rendered) + "\r\n";
        if (header is not null)
        {
            content = header + string.Concat(Enumerable.Repeat("\r\n", CaseNumberBlankLines + 1)) + content;
        }

        return new GeneratedMatrix(content, blocks.Count, duplicates, []);
    }

    /// <summary>The API representation; <c>content</c> is omitted, not null, when there are problems.</summary>
    public static JsonObject Json(GeneratedMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var body = new JsonObject
        {
            ["fileName"] = MatrixFileName,
            ["creditorCount"] = matrix.CreditorCount,
            ["duplicatesOmitted"] = matrix.DuplicatesOmitted,
            ["problems"] = new JsonArray([.. matrix.Problems.Select(problem =>
            {
                var item = new JsonObject();
                if (problem.CreditorId is not null)
                {
                    item["creditorId"] = problem.CreditorId;
                }

                item["field"] = problem.Field;
                item["message"] = problem.Message;
                return (JsonNode)item;
            })]),
        };
        if (matrix.Content is not null)
        {
            body["content"] = matrix.Content;
        }

        return body;
    }

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex ZipPattern();

    // Python's tuple comparison of two blocks of lines: line by line, then length.
    private static int CompareBlocks(string[] left, string[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var compared = string.CompareOrdinal(left[index], right[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool PrintableAscii(string value) => value.All(character => character is >= ' ' and <= '~');

    private static List<MatrixProblem> CreditorProblems(string id, CreditorBody body)
    {
        var address = body.Address;
        var problems = new List<MatrixProblem>();
        void Problem(string field, string message) => problems.Add(new MatrixProblem(id, field, message));

        if (body.Name is null)
        {
            Problem("name", "A creditor needs a name to appear on the matrix.");
        }

        if (address.Line1 is null && address.City is null && address.State is null && address.PostalCode is null)
        {
            if (address.Raw is not null)
            {
                Problem("address", "Only an unstructured address is on file — enter the street, city, state and ZIP before generating the matrix.");
            }
            else
            {
                Problem("address", "A creditor needs a mailing address.");
            }
        }
        else
        {
            if (address.Line1 is null)
            {
                Problem("address.line1", "A street address or PO box is required.");
            }

            if (address.City is null)
            {
                Problem("address.city", "A city is required.");
            }

            if (address.State is null)
            {
                Problem("address.state", "A state is required.");
            }
            else if (!UsStateAbbreviations.Contains(address.State))
            {
                Problem("address.state", "Must be a two-letter USPS state abbreviation in capitals, like \"FL\".");
            }

            if (address.PostalCode is null)
            {
                Problem("address.postal_code", "A ZIP code is required.");
            }
            else if (!ZipPattern().IsMatch(address.PostalCode))
            {
                Problem("address.postal_code", "Must be a five-digit ZIP (\"33301\") or hyphenated nine-digit ZIP (\"33301-1234\").");
            }
        }

        return problems;
    }

    private static List<(string Field, string Line)> BlockLines(CreditorBody body, MatrixFormat fmt)
    {
        var address = body.Address;
        var lines = new List<(string, string)> { ("name", body.Name ?? string.Empty) };
        if (address.Line1 is not null)
        {
            lines.Add(("address.line1", address.Line1));
        }

        if (address.Line2 is not null)
        {
            lines.Add(("address.line2", address.Line2));
        }

        var separator = fmt.CommaAfterCity ? ", " : " ";
        lines.Add(("address", $"{address.City}{separator}{address.State} {address.PostalCode}"));
        return lines;
    }

    private static List<MatrixProblem> LineProblems(string id, CreditorBody body, MatrixFormat fmt)
    {
        var problems = new List<MatrixProblem>();
        var lines = BlockLines(body, fmt);
        foreach (var (field, line) in lines)
        {
            var limit = field == "name" && fmt.NameLineChars is { } wider ? wider : fmt.MaxLineChars;
            if (TextRules.Length(line) > limit)
            {
                problems.Add(new MatrixProblem(id, field, $"Exceeds {limit} characters — the courts reject longer matrix lines, so shorten it (abbreviate, or move detail to the second address line)."));
            }

            if (!PrintableAscii(line))
            {
                problems.Add(new MatrixProblem(id, field, "Contains characters outside plain ASCII — the courts' scanners cannot read accents or symbols, so respell it with ordinary letters."));
            }
        }

        if (lines.Count > fmt.MaxCreditorLines)
        {
            problems.Add(new MatrixProblem(id, "address", $"Prints as more than {fmt.MaxCreditorLines} lines — the courts cap a creditor at {fmt.MaxCreditorLines}."));
        }

        return problems;
    }

    private static List<MatrixProblem> CaseNumberProblems(string caseNumber, MatrixFormat fmt)
    {
        var problems = new List<MatrixProblem>();
        if (caseNumber.Trim().Length == 0)
        {
            problems.Add(new MatrixProblem(null, "case_number", "The case number for the matrix header is blank."));
        }
        else if (TextRules.Length(caseNumber) > fmt.MaxLineChars || !PrintableAscii(caseNumber))
        {
            problems.Add(new MatrixProblem(null, "case_number", $"The case number must be plain ASCII of at most {fmt.MaxLineChars} characters to print as the matrix's first line."));
        }

        return problems;
    }
}
