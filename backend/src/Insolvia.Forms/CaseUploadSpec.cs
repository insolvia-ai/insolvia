using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Engines;

namespace Insolvia.Forms;

// The Case Upload specification as data, its loader and the validator that
// holds a Debtor.txt to it — the first half of services/api's
// core/case_upload.py (ADR 0024 build PR 9). The spec is
// Regulatory/filing/case-upload/<release>/spec.json, embedded byte for byte
// from services/api's copy (frozen, ADR 0028); CaseUploadSpecTests pins every
// embedded file to it by sha256 until 18.23 deletes the Python.

/// <summary>One field of one record, as the AO's table lists it (Python's <c>FieldSpec</c>).</summary>
/// <param name="Number">The 1-based field number.</param>
/// <param name="Name">The spec's name for it.</param>
/// <param name="Required">One of <see cref="CaseUploadSpecs.Requirements"/>.</param>
/// <param name="Width">The Field Length, or null when the spec states none.</param>
/// <param name="Type">One of <see cref="CaseUploadSpecs.Types"/>.</param>
/// <param name="Values">The permitted values (literal, code, letters).</param>
/// <param name="MaxCount">For letters: how many at most.</param>
/// <param name="Max">For an integer: the largest value.</param>
public sealed record CaseUploadFieldSpec(int Number, string Name, string Required, int? Width, string Type, IReadOnlyList<string> Values, int? MaxCount = null, int? Max = null);

/// <summary>One record type: <c>stat</c>, <c>debt</c> or <c>alas</c> (Python's <c>RecordSpec</c>).</summary>
/// <param name="Kind">The record type.</param>
/// <param name="Name">The spec's name for it.</param>
/// <param name="TrailingDelimiter">Whether a line of it ends with the delimiter (the spec's samples).</param>
/// <param name="Fields">Every field, numbered from 1.</param>
public sealed record CaseUploadRecordSpec(string Kind, string Name, bool TrailingDelimiter, IReadOnlyList<CaseUploadFieldSpec> Fields)
{
    /// <summary>The field numbered <paramref name="number"/> (1-based).</summary>
    public CaseUploadFieldSpec Field(int number) => Fields[number - 1];
}

/// <summary>One release of the specification (Python's <c>CaseUploadSpec</c>).</summary>
/// <param name="ReleaseId"><c>filing/case-upload@2022-03-01</c>.</param>
/// <param name="EffectiveDate">The day it applies from.</param>
/// <param name="SourceUrl">The AO's document.</param>
/// <param name="FileName"><c>Debtor.txt</c>.</param>
/// <param name="Delimiter"><c>|</c>.</param>
/// <param name="RecordSeparator"><c>\n</c>.</param>
/// <param name="AcceptedStatFieldCounts">The statistics-record field counts the NextGen releases accept.</param>
/// <param name="Records">The three record types, by kind.</param>
public sealed record CaseUploadSpec(
    string ReleaseId,
    DateOnly EffectiveDate,
    string SourceUrl,
    string FileName,
    string Delimiter,
    string RecordSeparator,
    IReadOnlyList<int> AcceptedStatFieldCounts,
    IReadOnlyDictionary<string, CaseUploadRecordSpec> Records);

/// <summary>
/// One thing wrong with the file or with what it would be built from
/// (Python's <c>CaseUploadProblem</c>). <see cref="Line"/> is the record's
/// 1-based line, <see cref="Field"/> its 1-based field number (0 for a
/// whole-record or whole-file problem). The message names the record, the
/// field and the fault — NEVER the field's content: the debtor record carries
/// the full SSN, and a problem is shown, logged and stored.
/// </summary>
/// <param name="Line">The line, or 0.</param>
/// <param name="Record">The record type, or empty for the file.</param>
/// <param name="Field">The field number, or 0.</param>
/// <param name="Code">The fault's code.</param>
/// <param name="Message">What is wrong, naming no value.</param>
public sealed record CaseUploadProblem(int Line, string Record, int Field, string Code, string Message);

/// <summary>
/// The file cannot be built or does not validate (Python's
/// <c>CaseUploadError</c>). Carries the problems; its message is their
/// messages, which name no value.
/// </summary>
public sealed class CaseUploadException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public CaseUploadException(IReadOnlyList<CaseUploadProblem> problems)
        : base(string.Join("; ", (problems ?? []).Select(problem => problem.Message)))
    {
        Problems = [.. problems ?? []];
    }

    /// <summary>Creates the exception with no problems.</summary>
    public CaseUploadException()
        : this([])
    {
    }

    /// <summary>Creates the exception with one message and no problems.</summary>
    public CaseUploadException(string message)
        : base(message)
    {
        Problems = [];
    }

    /// <summary>Creates the exception with one message, its cause, and no problems.</summary>
    public CaseUploadException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    /// <summary>Every problem.</summary>
    public IReadOnlyList<CaseUploadProblem> Problems { get; }
}

/// <summary>The specification's releases, and the validator (Python's <c>load_spec</c>, <c>resolve_spec</c>, <c>validate_debtor_txt</c>).</summary>
public static partial class CaseUploadSpecs
{
    /// <summary>The series id.</summary>
    public const string SeriesId = "filing/case-upload";

    /// <summary>The spec's Required column, as the spec file names it.</summary>
    public static readonly IReadOnlyList<string> Requirements = ["always", "voluntary", "court", "no", "ignored"];

    /// <summary>The field types the spec file uses.</summary>
    public static readonly IReadOnlyList<string> Types =
        ["literal", "ignored", "unused", "code", "letters", "money", "signed_money", "integer", "ssn", "ein", "case_number", "office_code", "county_code", "naics", "text"];

    /// <summary>Each record type's field count.</summary>
    public static readonly IReadOnlyList<(string Kind, int Count)> RecordFieldCounts = [("stat", 80), ("debt", 21), ("alas", 8)];

    private const string ResourcePrefix = "Insolvia.Forms.Regulatory/";
    private const string SeriesPath = "filing/case-upload/";

    private static readonly string[] PartyRoles = ["db", "jdb"];

    private static readonly Lazy<IReadOnlyList<CaseUploadSpec>> Shipped = new(LoadShipped);

    private static readonly Dictionary<string, Regex> Patterns = new(StringComparer.Ordinal)
    {
        ["ssn"] = SsnPattern(),
        ["ein"] = EinPattern(),
        ["case_number"] = CaseNumberPattern(),
        ["office_code"] = OfficeCodePattern(),
        ["county_code"] = CountyCodePattern(),
        ["naics"] = NaicsPattern(),
    };

    /// <summary>Every shipped release, oldest first.</summary>
    public static IReadOnlyList<CaseUploadSpec> Releases => Shipped.Value;

    /// <summary>The release in force on <paramref name="asOf"/>; <see cref="EngineLookupException"/> when none is.</summary>
    public static CaseUploadSpec Resolve(DateOnly asOf)
    {
        var applicable = Releases.Where(release => release.EffectiveDate <= asOf).ToList();
        return applicable.Count == 0
            ? throw new EngineLookupException($"no release of {SeriesId} is effective on {Py.Iso(asOf)}")
            : applicable[^1];
    }

    /// <summary>
    /// One release directory: its manifest and its spec, checked whole — a
    /// field count that is not the spec's (80 / 21 / 8), a gap in the
    /// numbering, an unknown type or requirement fails the load.
    /// </summary>
    public static CaseUploadSpec Load(string where, string manifestJson, string specJson)
    {
        var manifest = JsonNode.Parse(manifestJson)?.AsObject() ?? throw Fail(where, "manifest is not an object");
        var raw = JsonNode.Parse(specJson)?.AsObject() ?? throw Fail(where, "spec is not an object");
        if (Text(manifest["series_id"]) != SeriesId)
        {
            throw Fail(where, "manifest names another series");
        }

        if (raw["records"] is not JsonObject rawRecords
            || !rawRecords.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(RecordFieldCounts.Select(r => r.Kind).Order(StringComparer.Ordinal)))
        {
            throw Fail(where, "records must be exactly stat, debt and alas");
        }

        var records = new Dictionary<string, CaseUploadRecordSpec>(StringComparer.Ordinal);
        foreach (var (kind, count) in RecordFieldCounts)
        {
            var record = rawRecords[kind] as JsonObject ?? throw Fail(where, $"{kind} must have {count} fields");
            if (record["fields"] is not JsonArray rawFields || rawFields.Count != count)
            {
                throw Fail(where, $"{kind} must have {count} fields");
            }

            if (rawFields[0] is not JsonObject first || first["values"] is not JsonArray firstValues || firstValues.Count != 1 || Text(firstValues[0]) != kind)
            {
                throw Fail(where, $"{kind} field 1 must be the literal {Py.Repr(kind)}");
            }

            records[kind] = new CaseUploadRecordSpec(
                kind,
                record["name"] is JsonValue name && Text(name) is { } named ? named : kind,
                record["trailing_delimiter"] is JsonValue trailing && trailing.TryGetValue<bool>(out var on) && on,
                [.. rawFields.Select((field, index) => FieldSpec(field, $"{where}/{kind}", index + 1))]);
        }

        var effective = DateOnly.ParseExact(Text(manifest["effective_date"]) ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sequence = manifest["sequence"] is JsonValue s && s.TryGetValue<int>(out var n) ? n : 1;
        var releaseId = $"{SeriesId}@{Py.Iso(effective)}" + (sequence == 1 ? string.Empty : $"+{sequence}");
        if (raw["accepted_stat_field_counts"] is not JsonArray counts || !counts.Any(c => Int(c) == 80))
        {
            throw Fail(where, "accepted_stat_field_counts must include 80");
        }

        return new CaseUploadSpec(
            releaseId,
            effective,
            Text(manifest["source"]?["url"]) ?? string.Empty,
            Text(raw["file_name"]) ?? string.Empty,
            Text(raw["delimiter"]) ?? string.Empty,
            Text(raw["record_separator"]) ?? string.Empty,
            [.. counts.Select(c => Int(c) ?? throw Fail(where, "accepted_stat_field_counts must be integers"))],
            records);
    }

    /// <summary>The embedded files, by their path under the regulatory folder (the pin test compares them to the Python copies).</summary>
    internal static IReadOnlyDictionary<string, byte[]> EmbeddedFiles()
    {
        var assembly = typeof(CaseUploadSpecs).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix + SeriesPath, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files[name[ResourcePrefix.Length..]] = buffer.ToArray();
        }

        return files;
    }

    private static List<CaseUploadSpec> LoadShipped()
    {
        var files = EmbeddedFiles();
        var releases = new List<CaseUploadSpec>();
        foreach (var directory in files.Keys.Select(path => path[..path.LastIndexOf('/')]).Distinct(StringComparer.Ordinal))
        {
            string Read(string file) => files.TryGetValue($"{directory}/{file}", out var bytes)
                ? Encoding.UTF8.GetString(bytes)
                : throw Fail(directory, $"{file} is missing");
            releases.Add(Load(directory[(directory.LastIndexOf('/') + 1)..], Read("manifest.json"), Read("spec.json")));
        }

        return [.. releases.OrderBy(release => release.EffectiveDate)];
    }

    private static CaseUploadFieldSpec FieldSpec(JsonNode? node, string where, int expected)
    {
        if (node is not JsonObject raw)
        {
            throw Fail(where, "a field is not an object");
        }

        if (Int(raw["n"]) != expected)
        {
            throw Fail(where, $"field {expected} is numbered {Py.Repr(raw["n"])}");
        }

        var name = Text(raw["name"]);
        var required = Text(raw["required"]);
        var kind = Text(raw["type"]);
        if (string.IsNullOrEmpty(name))
        {
            throw Fail(where, $"field {expected} has no name");
        }

        if (required is null || !Requirements.Contains(required))
        {
            throw Fail(where, $"field {expected} required={Py.Repr(raw["required"])}");
        }

        if (kind is null || !Types.Contains(kind))
        {
            throw Fail(where, $"field {expected} type={Py.Repr(raw["type"])}");
        }

        int? width = null;
        if (raw["width"] is { } rawWidth)
        {
            width = Int(rawWidth) is { } w && w >= 0 ? w : throw Fail(where, $"field {expected} width={Py.Repr(rawWidth)}");
        }

        var values = new List<string>();
        if (raw["values"] is { } rawValues)
        {
            if (rawValues is not JsonArray array || array.Any(v => Text(v) is null))
            {
                throw Fail(where, $"field {expected} values");
            }

            values.AddRange(array.Select(v => Text(v)!));
        }

        if (kind is "literal" or "code" or "letters" && values.Count == 0)
        {
            throw Fail(where, $"field {expected} is a {kind} with no values");
        }

        return new CaseUploadFieldSpec(expected, name, required, width, kind, values, Int(raw["max_count"]), Int(raw["max"]));
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

    private static MalformedReleaseException Fail(string where, string problem) => new($"malformed case-upload spec {where}: {problem}");

    // ── The validator ──

    /// <summary>
    /// Every problem with a Debtor.txt against the encoded spec: the file's
    /// shape (separator, character set, record order and counts), each
    /// record's field count, every field's width, type, code set and
    /// requirement, and the cross-field rules the spec's notes state. Empty
    /// means valid. Bytes must be ASCII.
    /// </summary>
    public static IReadOnlyList<CaseUploadProblem> Validate(byte[] content, CaseUploadSpec spec)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(spec);
        if (content.Any(b => b > 0x7F))
        {
            return [new CaseUploadProblem(0, string.Empty, 0, "charset", "file (line 0) is not ASCII")];
        }

        return Validate(Encoding.ASCII.GetString(content), spec);
    }

    /// <summary>The same checks over text already decoded.</summary>
    public static IReadOnlyList<CaseUploadProblem> Validate(string text, CaseUploadSpec spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<CaseUploadProblem>();

        void Add(int line, string record, int number, string code, string what)
        {
            string message;
            if (number != 0)
            {
                var name = record.Length > 0 ? spec.Records[record].Field(number).Name : string.Empty;
                message = $"{record} record (line {line}) field {number} ({name}) {what}";
            }
            else
            {
                message = $"{(record.Length > 0 ? record : "file")} (line {line}) {what}";
            }

            problems.Add(new CaseUploadProblem(line, record, number, code, message));
        }

        var separator = spec.RecordSeparator;
        if (!text.EndsWith(separator, StringComparison.Ordinal))
        {
            Add(0, string.Empty, 0, "separator", "does not end with the record separator");
        }

        var lines = text.Split(separator).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            Add(0, string.Empty, 0, "empty", "has no records");
            return problems;
        }

        List<string>? statValues = null;
        var roles = new List<string>();
        var seen = new List<string>();
        var aliases = new List<(int Line, string Role)>();
        for (var index = 0; index < lines.Count; index++)
        {
            var number = index + 1;
            var lineText = lines[index];
            if (lineText.Contains('\r', StringComparison.Ordinal) || !Printable(lineText))
            {
                Add(number, string.Empty, 0, "charset", "holds a character outside printable ASCII");
                continue;
            }

            var kind = lineText.Split(spec.Delimiter, 2)[0];
            if (!spec.Records.TryGetValue(kind, out var record))
            {
                Add(number, string.Empty, 0, "record_type", "is not a stat, debt or alas record");
                continue;
            }

            seen.Add(kind);
            var values = SplitRecord(lineText, record, spec.Delimiter);
            if (values is null)
            {
                Add(number, kind, 0, "field_count", $"does not have the {record.Fields.Count} fields of the spec");
                continue;
            }

            for (var f = 0; f < record.Fields.Count; f++)
            {
                CheckField(record.Fields[f], values[f], number, kind, Add);
            }

            if (kind == "stat")
            {
                statValues = values;
            }
            else if (kind == "debt")
            {
                roles.Add(values[1]);
                CheckDebtor(values, number, statValues, Add);
            }
            else
            {
                aliases.Add((number, values[1]));
                if (values[5].Length == 0)
                {
                    Add(number, "alas", 6, "required", "is required");
                }
            }
        }

        CheckOrder(seen, Add);
        string[] expectedRoles = [.. PartyRoles.Take(roles.Count)];
        if (!roles.SequenceEqual(expectedRoles, StringComparer.Ordinal))
        {
            Add(0, "debt", 2, "role", "must be db on the first debtor and jdb on the second");
        }

        foreach (var (line, role) in aliases)
        {
            if (!roles.Contains(role, StringComparer.Ordinal))
            {
                Add(line, "alas", 2, "role", "names no debtor record's party role");
            }
        }

        if (statValues is not null)
        {
            CheckStat(statValues, roles.Count, Add);
        }

        return problems;
    }

    /// <summary>Python's <c>^[\x20-\x7e]*$</c>.</summary>
    internal static bool Printable(string text) => text.All(c => c is >= '\x20' and <= '\x7e');

    private static List<string>? SplitRecord(string lineText, CaseUploadRecordSpec record, string delimiter)
    {
        var parts = lineText.Split(delimiter).ToList();
        var count = record.Fields.Count;
        if (record.TrailingDelimiter)
        {
            if (parts.Count != count + 1 || parts[^1].Length != 0)
            {
                return null;
            }

            parts.RemoveAt(parts.Count - 1);
            return parts;
        }

        return parts.Count == count ? parts : null;
    }

    private static void CheckField(CaseUploadFieldSpec spec, string value, int line, string record, Action<int, string, int, string, string> add)
    {
        void Fault(string code, string what) => add(line, record, spec.Number, code, what);

        if (spec.Width is { } width && value.Length > width)
        {
            Fault("width", $"is longer than its {width} characters");
            return;
        }

        if (value.Length == 0)
        {
            return;
        }

        switch (spec.Type)
        {
            case "ignored":
            case "text": // the character set is checked for the whole line
                return;
            case "unused":
                Fault("not_blank", "must be blank");
                return;
            case "literal":
            case "code":
                if (!spec.Values.Contains(value, StringComparer.Ordinal))
                {
                    Fault("code", "is not one of the permitted values");
                }

                return;
            case "letters":
                var letters = value.Select(c => c.ToString()).ToList();
                if (letters.Any(letter => !spec.Values.Contains(letter, StringComparer.Ordinal))
                    || letters.Distinct(StringComparer.Ordinal).Count() != letters.Count
                    || (spec.MaxCount is { } maxCount && letters.Count > maxCount))
                {
                    Fault("code", "is not a permitted combination of values");
                }

                return;
            case "money":
                if (!MoneyPattern().IsMatch(value))
                {
                    Fault("format", "is not an amount 0.00-999999999999.99");
                }

                return;
            case "signed_money":
                if (!SignedMoneyPattern().IsMatch(value))
                {
                    Fault("format", "is not a signed amount with two decimals");
                }

                return;
            case "integer":
                if (!value.All(char.IsAsciiDigit) || (spec.Max is { } max && BigInteger.Parse(value, CultureInfo.InvariantCulture) > max))
                {
                    Fault("format", "is not a whole number in range");
                }

                return;
            default:
                if (!Patterns[spec.Type].IsMatch(value))
                {
                    Fault("format", $"is not in the {spec.Type} format");
                }

                return;
        }
    }

    private static void CheckOrder(List<string> seen, Action<int, string, int, string, string> add)
    {
        if (seen.Count(kind => kind == "stat") != 1 || (seen.Count > 0 && seen[0] != "stat"))
        {
            add(0, "stat", 0, "order", "must be exactly one record, and the first");
        }

        var debts = seen.Count(kind => kind == "debt");
        if (debts is not (1 or 2))
        {
            add(0, "debt", 0, "order", "must be one record per debtor (one or two)");
        }

        static int Rank(string kind) => kind switch { "stat" => 0, "debt" => 1, _ => 2 };
        var ranks = seen.Select(Rank).ToList();
        if (!ranks.SequenceEqual(ranks.Order()))
        {
            add(0, string.Empty, 0, "order", "records must run stat, then debt, then alas");
        }
    }

    private static void CheckDebtor(List<string> values, int line, List<string>? stat, Action<int, string, int, string, string> add)
    {
        var individual = stat is not null && stat[4] == "i";

        // '**' fields: required, but for the two blanks the spec itself allows
        // (spec.json ambiguity `court-configured-required`).
        var required = new List<int> { 5, 10, 11, 14, 15, 16, 17 };
        required.AddRange(individual ? [3, 8] : [9]);
        foreach (var number in required.Order())
        {
            if (values[number - 1].Length == 0)
            {
                add(line, "debt", number, "required", "is required");
            }
        }
    }

    private static void CheckStat(List<string> values, int debtors, Action<int, string, int, string, string> add)
    {
        bool Blank(int number) => values[number - 1].Length == 0;
        void Fault(int number, string code, string what) => add(1, "stat", number, code, what);

        foreach (var number in new[] { 1, 5, 8, 9, 10, 11, 17, 21 })
        {
            if (Blank(number))
            {
                Fault(number, "required", "is required");
            }
        }

        var voluntary = values[10] == "v";
        var chapter = values[7];
        if (voluntary)
        {
            foreach (var number in new[] { 13, 14, 15, 16, 24 })
            {
                if (Blank(number) && chapter != "15")
                {
                    Fault(number, "required", "is required on a voluntary case");
                }
            }
        }

        var nature = values[5];
        if (values[4] == "i")
        {
            if (nature.Length > 1 || (nature.Length > 0 && !"hrsbo".Contains(nature, StringComparison.Ordinal)))
            {
                Fault(6, "rule", "allows one of h, r, s, b, o for an individual");
            }
        }
        else if (nature.Length == 0)
        {
            Fault(6, "required", "is required for a debtor that is not an individual");
        }
        else if (nature.Length == 2 && !nature.Contains('n', StringComparison.Ordinal))
        {
            Fault(6, "rule", "allows two values only when one is n");
        }

        if (values[8] == "w" && !(chapter == "7" && voluntary))
        {
            Fault(9, "rule", "allows w only on a voluntary Chapter 7 case");
        }

        if (chapter is "9" or "12" && values[9] is not ("" or "b"))
        {
            Fault(10, "rule", "must be b on Chapter 9 and 12");
        }

        if (!voluntary && chapter is not ("7" or "11"))
        {
            Fault(8, "rule", "allows only Chapter 7 or 11 on an involuntary case");
        }

        if (chapter != "15" && !Blank(23))
        {
            Fault(23, "rule", "is valid only on Chapter 15");
        }

        if (debtors < 1)
        {
            Fault(0, "order", "has no debtor record");
        }
    }

    [GeneratedRegex(@"^[0-9]{1,12}\.[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyPattern();

    [GeneratedRegex(@"^-?[0-9]{1,12}\.[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SignedMoneyPattern();

    [GeneratedRegex(@"^[0-9]{3}-[0-9]{2}-[0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"^[0-9]{2}-[0-9]{7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EinPattern();

    [GeneratedRegex(@"^[0-9]{2}-[0-9]{5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberPattern();

    [GeneratedRegex(@"^[0-9A-Za-z]\z", RegexOptions.CultureInvariant)]
    private static partial Regex OfficeCodePattern();

    [GeneratedRegex(@"^[0-9]{5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountyCodePattern();

    [GeneratedRegex(@"^[0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NaicsPattern();
}
