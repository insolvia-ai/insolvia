using System.Text.Json.Nodes;
using Insolvia.Engines.Regulatory;
using static Insolvia.Engines.Regulatory.ReleaseParsing;

namespace Insolvia.Engines.Deadlines;

// Legal holidays and Rule 9006(a) day counting (from core/legal_holidays.py,
// issue 14.6 / #358). The § 6103(a) list is data in the registry
// (`code/legal-holidays`), each holiday a date rule with its citation, and the
// two observance shifts carried with theirs. The 9006(a)(6)(B)/(C) days are
// deliberately not here.

/// <summary>
/// One § 6103(a) holiday as a rule over the calendar: a fixed month/day, or
/// the nth (or last, <see cref="Ordinal"/> -1) weekday of a month.
/// <see cref="Weekday"/> counts Python's way: Monday is 0.
/// </summary>
public sealed record HolidayRule(string HolidayId, string Name, string Citation, int Month, int? Day, int? Weekday, int? Ordinal)
{
    /// <summary>The holiday's own date in <paramref name="year"/>.</summary>
    public DateOnly DateIn(int year)
    {
        if (Day is { } day)
        {
            return new DateOnly(year, Month, day);
        }

        if (Weekday is not { } weekday || Ordinal is not { } ordinal)
        {
            throw new EngineValueException($"holiday {HolidayId} has no date rule");
        }

        if (ordinal > 0)
        {
            var first = new DateOnly(year, Month, 1);
            var offset = Mod(weekday - LegalHolidays.MondayFirstWeekday(first), 7);
            return first.AddDays(offset + (7 * (ordinal - 1)));
        }

        var nextMonth = new DateOnly(year + (Month == 12 ? 1 : 0), (Month % 12) + 1, 1);
        var last = nextMonth.AddDays(-1);
        return last.AddDays(-Mod(LegalHolidays.MondayFirstWeekday(last) - weekday, 7));
    }

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}

/// <summary>How a holiday falling on a weekend is observed, with the authority.</summary>
public sealed record Observance(string SaturdayShift, string SaturdayCitation, string SundayShift, string SundayCitation);

/// <summary>One release of <c>code/legal-holidays</c>, and the day arithmetic over it.</summary>
public sealed record HolidayRelease(
    string SeriesId,
    DateOnly EffectiveDate,
    int Sequence,
    string SourceUrl,
    string Notes,
    IReadOnlyList<HolidayRule> Holidays,
    Observance Observance,
    IReadOnlyList<Source> Sources) : IRelease
{
    /// <summary><c>series@date[+sequence]</c>.</summary>
    public string ReleaseId => Releases.Id(SeriesId, EffectiveDate, Sequence);

    /// <summary>Every day that is a legal holiday in <paramref name="year"/>, weekend holidays shifted to their observed day.</summary>
    public IReadOnlySet<DateOnly> ObservedIn(int year)
    {
        var days = new HashSet<DateOnly>();
        foreach (var holiday in Holidays)
        {
            var actual = holiday.DateIn(year);
            days.Add(actual.DayOfWeek switch
            {
                DayOfWeek.Saturday => actual.AddDays(-1),
                DayOfWeek.Sunday => actual.AddDays(1),
                _ => actual,
            });
        }

        return days;
    }

    /// <summary>Whether <paramref name="day"/> is a legal holiday.</summary>
    public bool IsLegalHoliday(DateOnly day) => ObservedIn(day.Year).Contains(day);

    /// <summary>Not a Saturday, a Sunday, or a legal holiday — a day a period may end on.</summary>
    public bool IsCourtDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsLegalHoliday(day);

    /// <summary>
    /// Rule 9006(a)(1): the last day of a <paramref name="days"/>-day period
    /// triggered on <paramref name="trigger"/> — the trigger day excluded, a
    /// last day that is not a court day rolled forward. A zero-day period is
    /// the trigger day itself, unrolled.
    /// </summary>
    public DateOnly AddDays(DateOnly trigger, int days)
    {
        if (days <= 0)
        {
            return trigger;
        }

        var last = trigger.AddDays(days);
        while (!IsCourtDay(last))
        {
            last = last.AddDays(1);
        }

        return last;
    }
}

/// <summary>The legal-holiday registry (Python's <c>core/legal_holidays.py</c>).</summary>
public static class LegalHolidays
{
    /// <summary>The series id.</summary>
    public const string SeriesId = "code/legal-holidays";

    private static readonly string[] WeekdayNames = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    private static readonly Lazy<IReadOnlyList<HolidayRelease>> Shipped = new(() => LoadRegistry(RegistryTree.Shipped));

    /// <summary>The committed releases, oldest first.</summary>
    public static IReadOnlyList<HolidayRelease> All => Shipped.Value;

    /// <summary>Every release under a registry root, oldest first; refuses any malformed release.</summary>
    public static IReadOnlyList<HolidayRelease> LoadRegistry(RegistryTree root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var loaded = root.ChildDirectories("code/legal-holidays")
            .Select(name => LoadRelease(root, $"code/legal-holidays/{name}", name))
            .OrderBy(release => release.EffectiveDate)
            .ThenBy(release => release.Sequence)
            .ToList();
        if (loaded.Count == 0)
        {
            throw Fail(SeriesId, "series has no releases");
        }

        return loaded;
    }

    /// <summary>The release in force on <paramref name="asOf"/>, refusing a date before the series begins.</summary>
    public static HolidayRelease Resolve(DateOnly asOf) => ResolveIn(All, asOf);

    /// <summary><see cref="Resolve"/> over given releases.</summary>
    public static HolidayRelease ResolveIn(IReadOnlyList<HolidayRelease> releases, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var applicable = releases.Where(release => release.EffectiveDate <= asOf).ToList();
        if (applicable.Count == 0)
        {
            var earliest = releases.Min(release => release.EffectiveDate);
            throw new EngineLookupException(
                $"no release of {SeriesId} is effective on or before {Exact.Iso(asOf)} (series begins {Exact.Iso(earliest)})");
        }

        return Releases.MaxByKey(applicable);
    }

    /// <summary>Python's <c>date.weekday()</c>: Monday 0 through Sunday 6.</summary>
    internal static int MondayFirstWeekday(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    internal static (DateOnly Effective, int Sequence) CodeDirectoryName(string name, string where)
    {
        var plus = name.IndexOf('+', StringComparison.Ordinal);
        var effectiveText = plus < 0 ? name : name[..plus];
        var sequenceText = plus < 0 ? string.Empty : name[(plus + 1)..];
        var effective = Exact.ParseIso(effectiveText) ?? throw Fail(where, "directory name is not <effective_date>[+<sequence>]");
        var sequence = sequenceText.Length > 0 ? int.Parse(sequenceText, System.Globalization.CultureInfo.InvariantCulture) : 1;
        return (effective, sequence);
    }

    private static HolidayRule Holiday(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "a holiday is not an object");
        }

        var holidayId = StrField(raw, "holiday_id", where);
        where = $"{where}:{holidayId}";
        if (raw["rule"] is not JsonObject rule)
        {
            throw Fail(where, "rule missing");
        }

        if (!Core.Json.TryGetWholeNumber(rule["month"], out var month) || month is < 1 or > 12)
        {
            throw Fail(where, "rule.month must be 1-12");
        }

        var day = rule["day"];
        var weekdayName = rule["weekday"];
        var ordinalRaw = rule["ordinal"];
        if (day is not null)
        {
            if (weekdayName is not null || ordinalRaw is not null)
            {
                throw Fail(where, "rule names both a day and a weekday");
            }

            if (!Core.Json.TryGetWholeNumber(day, out var dayNumber) || dayNumber is < 1 or > 31)
            {
                throw Fail(where, "rule.day must be 1-31");
            }

            return new HolidayRule(holidayId, StrField(raw, "name", where), StrField(raw, "citation", where), (int)month, (int)dayNumber, null, null);
        }

        var weekday = Array.IndexOf(WeekdayNames, Core.Json.AsString(weekdayName));
        if (weekday < 0)
        {
            throw Fail(where, "rule.weekday must name a weekday");
        }

        int ordinal;
        if (Core.Json.AsString(ordinalRaw) == "last")
        {
            ordinal = -1;
        }
        else if (Core.Json.TryGetWholeNumber(ordinalRaw, out var number) && number is >= 1 and <= 5)
        {
            ordinal = (int)number;
        }
        else
        {
            throw Fail(where, "rule.ordinal must be 1-5 or 'last'");
        }

        return new HolidayRule(holidayId, StrField(raw, "name", where), StrField(raw, "citation", where), (int)month, null, weekday, ordinal);
    }

    private static Observance ParseObservance(JsonNode? value, string where)
    {
        if (value is not JsonObject raw)
        {
            throw Fail(where, "observance missing");
        }

        if (raw["saturday"] is not JsonObject saturday || raw["sunday"] is not JsonObject sunday)
        {
            throw Fail(where, "observance needs saturday and sunday");
        }

        if (Core.Json.AsString(saturday["shift"]) != "friday_before" || Core.Json.AsString(sunday["shift"]) != "monday_after")
        {
            throw Fail(where, "unsupported observance shift");
        }

        return new Observance("friday_before", StrField(saturday, "citation", where), "monday_after", StrField(sunday, "citation", where));
    }

    private static HolidayRelease LoadRelease(RegistryTree root, string directory, string name)
    {
        var where = $"{SeriesId}/{name}";
        var (effective, sequence) = CodeDirectoryName(name, where);
        var manifest = LoadCodeJson(root, $"{directory}/manifest.json", where);
        var source = ManifestSource(manifest, SeriesId, effective, sequence, where);

        var payload = LoadCodeJson(root, $"{directory}/holidays.json", where);
        if (payload["holidays"] is not JsonArray rawHolidays || rawHolidays.Count == 0)
        {
            throw Fail(where, "payload holidays missing or empty");
        }

        var holidays = rawHolidays.Select(raw => Holiday(raw, where)).ToList();
        if (holidays.Select(holiday => holiday.HolidayId).Distinct(StringComparer.Ordinal).Count() != holidays.Count)
        {
            throw Fail(where, "duplicate holiday ids");
        }

        return new HolidayRelease(
            SeriesId: SeriesId,
            EffectiveDate: effective,
            Sequence: sequence,
            SourceUrl: StrField(source, "url", where),
            Notes: StrField(manifest, "notes", where),
            Holidays: holidays,
            Observance: ParseObservance(payload["observance"], where),
            Sources: CodeSources(payload["sources"], where, "sources missing or empty"));
    }
}
