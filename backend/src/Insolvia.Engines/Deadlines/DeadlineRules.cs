using System.Text.Json.Nodes;
using Insolvia.Engines.Regulatory;
using static Insolvia.Engines.Regulatory.ReleaseParsing;

namespace Insolvia.Engines.Deadlines;

// The deadline rule table: loader and resolution for `code/deadline-rules`
// (from core/deadline_rules.py, issue 14.6 / #358). Which deadlines, from which
// anchor, counted how, in which chapters, is law — effective-dated data, each
// rule naming and quoting its authority.

/// <summary>One rule of the table, as the loader validated it.</summary>
public sealed record DeadlineRule(
    string RuleId,
    string Title,
    string Citation,
    string QuotedText,
    string Description,
    IReadOnlyList<int> Chapters,
    string Kind,
    string Anchor,
    int CountDays,
    string Computation,
    Verification Verification,
    IReadOnlyList<Source> Sources,
    int? WindowStartDays = null,
    string? NotLaterThanAnchor = null,
    string? UntilAnchor = null);

/// <summary>One release of <c>code/deadline-rules</c>.</summary>
public sealed record RulesRelease(string SeriesId, DateOnly EffectiveDate, int Sequence, string SourceUrl, string Notes, IReadOnlyList<DeadlineRule> Rules) : IRelease
{
    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);

    /// <summary>The rule with this id; <see cref="EngineKeyException"/> when there is none.</summary>
    public DeadlineRule Rule(string ruleId) =>
        Rules.FirstOrDefault(rule => rule.RuleId == ruleId)
        ?? throw new EngineKeyException($"{ReleaseId} has no rule {Py.Repr(ruleId)}");
}

/// <summary>The deadline-rule registry (Python's <c>core/deadline_rules.py</c>).</summary>
public static class DeadlineRules
{
    /// <summary>The series id.</summary>
    public const string SeriesId = "code/deadline-rules";

    /// <summary>The dates a rule counts from.</summary>
    public static readonly IReadOnlyList<string> Anchors = ["petition", "meeting_341"];

    /// <summary>What a rule produces.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["deadline", "window", "meeting"];

    /// <summary>How a rule counts.</summary>
    public static readonly IReadOnlyList<string> Computations = ["rule_9006a", "calendar"];

    /// <summary>The chapters this product files.</summary>
    public static readonly IReadOnlyList<int> Chapters = [7, 11, 12, 13];

    private static readonly Lazy<IReadOnlyList<RulesRelease>> Shipped = new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed releases, oldest first.</summary>
    public static IReadOnlyList<RulesRelease> All => Shipped.Value;

    /// <summary>Every release under a registry root, oldest first.</summary>
    public static IReadOnlyList<RulesRelease> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var loaded = root.ChildDirectories("code/deadline-rules")
            .Select(name => LoadRelease(root, $"code/deadline-rules/{name}", name))
            .OrderBy(release => release.EffectiveDate)
            .ThenBy(release => release.Sequence)
            .ToList();
        if (loaded.Count == 0)
        {
            throw Fail(SeriesId, "series has no releases");
        }

        return loaded;
    }

    /// <summary>One release directory (Python's <c>load_release</c>).</summary>
    public static RulesRelease LoadRelease(RegistryTree root, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        var where = $"{SeriesId}/{name}";
        var (effective, sequence) = LegalHolidays.CodeDirectoryName(name, where);
        var manifest = LoadCodeJson(root, $"{directory}/manifest.json", where);
        var source = ManifestSource(manifest, SeriesId, effective, sequence, where);

        var payload = LoadCodeJson(root, $"{directory}/rules.json", where);
        if (payload["rules"] is not JsonArray rawRules || rawRules.Count == 0)
        {
            throw Fail(where, "payload rules missing or empty");
        }

        var rules = rawRules.Select(raw => ParseRule(raw, where)).ToList();
        if (rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            throw Fail(where, "duplicate rule ids");
        }

        return new RulesRelease(SeriesId, effective, sequence, StrField(source, "url", where), StrField(manifest, "notes", where), rules);
    }

    /// <summary>The release in force on <paramref name="asOf"/> — the petition date.</summary>
    public static RulesRelease Resolve(DateOnly asOf)
    {
        var applicable = All.Where(release => release.EffectiveDate <= asOf).ToList();
        if (applicable.Count == 0)
        {
            var earliest = All.Min(release => release.EffectiveDate);
            throw new EngineLookupException(
                $"no release of {SeriesId} is effective on or before {Py.Iso(asOf)} (series begins {Py.Iso(earliest)})");
        }

        return Releases.MaxByKey(applicable);
    }

    /// <summary>Newest.</summary>
    public static RulesRelease Latest() => All[^1];

    private static int Count(JsonNode? value, string where, string key, int minimum)
    {
        if (!Core.Json.TryGetWholeNumber(value, out var number) || number < minimum)
        {
            throw Fail(where, $"{key} must be a whole number >= {minimum}");
        }

        return (int)number;
    }

    private static string? OptionalAnchor(JsonNode? value, string where, string key)
    {
        if (value is null)
        {
            return null;
        }

        var text = Core.Json.AsString(value);
        if (text is null || !Anchors.Contains(text))
        {
            throw Fail(where, $"{key} must be one of {string.Join(", ", Anchors)}");
        }

        return text;
    }

    private static DeadlineRule ParseRule(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "a rule is not an object");
        }

        var ruleId = StrField(raw, "rule_id", where);
        where = $"{where}:{ruleId}";

        if (raw["chapters"] is not JsonArray chaptersRaw || chaptersRaw.Count == 0)
        {
            throw Fail(where, "chapters missing or empty");
        }

        var chapters = new List<int>();
        foreach (var chapter in chaptersRaw)
        {
            if (!Core.Json.TryGetWholeNumber(chapter, out var number))
            {
                throw Fail(where, "chapters must be numbers");
            }

            if (!Chapters.Contains((int)number))
            {
                throw Fail(where, $"chapter {number} is not one this product files");
            }

            chapters.Add((int)number);
        }

        var kind = StrField(raw, "kind", where);
        if (!Kinds.Contains(kind))
        {
            throw Fail(where, $"kind must be one of {string.Join(", ", Kinds)}");
        }

        var anchor = StrField(raw, "anchor", where);
        if (!Anchors.Contains(anchor))
        {
            throw Fail(where, $"anchor must be one of {string.Join(", ", Anchors)}");
        }

        var computation = StrField(raw, "computation", where);
        if (!Computations.Contains(computation))
        {
            throw Fail(where, $"computation must be one of {string.Join(", ", Computations)}");
        }

        var countDays = Count(raw["count_days"], where, "count_days", 0);
        var windowStart = raw["window_start_days"];
        int? windowStartDays;
        if (kind == "window")
        {
            if (windowStart is null)
            {
                throw Fail(where, "a window needs window_start_days");
            }

            windowStartDays = Count(windowStart, where, "window_start_days", 0);
            if (windowStartDays > countDays)
            {
                throw Fail(where, "a window cannot start after it ends");
            }
        }
        else
        {
            if (windowStart is not null)
            {
                throw Fail(where, "window_start_days only belongs on a window");
            }

            windowStartDays = null;
        }

        if (kind == "deadline" && countDays == 0)
        {
            throw Fail(where, "a deadline needs a positive count");
        }

        if (kind == "meeting" && (countDays != 0 || anchor != "meeting_341"))
        {
            throw Fail(where, "a meeting is the meeting_341 anchor itself");
        }

        var verificationRaw = StrField(raw, "verification", where);
        if (!Core.Wire.TryParse<Verification>(verificationRaw, out var verification))
        {
            throw Fail(where, $"verification {Py.Repr(verificationRaw)}");
        }

        if (verification == Verification.Unverified)
        {
            throw Fail(where, "a deadline rule may never be unverified");
        }

        return new DeadlineRule(
            RuleId: ruleId,
            Title: StrField(raw, "title", where),
            Citation: StrField(raw, "citation", where),
            QuotedText: StrField(raw, "quoted_text", where),
            Description: StrField(raw, "description", where),
            Chapters: chapters,
            Kind: kind,
            Anchor: anchor,
            CountDays: countDays,
            Computation: computation,
            Verification: verification,
            Sources: CodeSources(raw["sources"], where, "sources missing or empty — a rule must say where it was read"),
            WindowStartDays: windowStartDays,
            NotLaterThanAnchor: OptionalAnchor(raw["not_later_than_anchor"], where, "not_later_than_anchor"),
            UntilAnchor: OptionalAnchor(raw["until_anchor"], where, "until_anchor"));
    }
}
