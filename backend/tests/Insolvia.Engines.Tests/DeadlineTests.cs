using System.Text.Json.Nodes;
using Insolvia.Core.Cases;
using Insolvia.Engines.Deadlines;
using Insolvia.Engines.Regulatory;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_legal_holidays.py: Rule
// 9006(a) day counting worked by hand against a calendar, and the loader.
public sealed class LegalHolidayTests
{
    // 2026's calendar, the year the tests count in. 2026-09-24 is a Thursday.
    private static readonly HolidayRelease Holidays = LegalHolidays.Resolve(new DateOnly(2026, 1, 1));

    [Fact]
    public void The_committed_series_loads_and_names_itself()
    {
        Assert.Equal(["code/legal-holidays@2021-06-17"], LegalHolidays.All.Select(release => release.ReleaseId));
        Assert.Equal(11, Holidays.Holidays.Count);
    }

    [Fact]
    public void The_observed_holidays_of_2026()
    {
        // 5 U.S.C. § 6103(a), 2026; July 4 falls on a Saturday → Friday the 3rd.
        Assert.Equal(
            new HashSet<DateOnly>
            {
                new(2026, 1, 1), new(2026, 1, 19), new(2026, 2, 16), new(2026, 5, 25), new(2026, 6, 19), new(2026, 7, 3),
                new(2026, 9, 7), new(2026, 10, 12), new(2026, 11, 11), new(2026, 11, 26), new(2026, 12, 25),
            },
            Holidays.ObservedIn(2026).ToHashSet());
    }

    [Fact]
    public void The_observed_holidays_of_2027()
    {
        // July 4 is a Sunday → Monday; Christmas a Saturday → Friday 24th; Juneteenth a Saturday → Friday 18th.
        Assert.Equal(
            new HashSet<DateOnly>
            {
                new(2027, 1, 1), new(2027, 1, 18), new(2027, 2, 15), new(2027, 5, 31), new(2027, 6, 18), new(2027, 7, 5),
                new(2027, 9, 6), new(2027, 10, 11), new(2027, 11, 11), new(2027, 11, 25), new(2027, 12, 24),
            },
            Holidays.ObservedIn(2027).ToHashSet());
    }

    // ── Rule 9006(a)(1) known answers ──

    [Fact]
    public void The_trigger_day_is_excluded_and_every_day_counts() =>
        Assert.Equal(new DateOnly(2026, 3, 16), Holidays.AddDays(new DateOnly(2026, 3, 2), 14));

    [Fact]
    public void A_last_day_on_a_saturday_rolls_to_monday() =>
        Assert.Equal(new DateOnly(2026, 3, 16), Holidays.AddDays(new DateOnly(2026, 2, 28), 14));

    [Fact]
    public void A_last_day_on_a_sunday_rolls_to_monday() =>
        Assert.Equal(new DateOnly(2026, 3, 16), Holidays.AddDays(new DateOnly(2026, 3, 1), 14));

    [Fact]
    public void A_last_day_on_a_legal_holiday_rolls_past_it() =>
        Assert.Equal(new DateOnly(2026, 9, 8), Holidays.AddDays(new DateOnly(2026, 8, 7), 30));

    [Fact]
    public void A_holiday_that_lands_on_a_weekday_rolls_once() =>
        Assert.Equal(new DateOnly(2026, 12, 28), Holidays.AddDays(new DateOnly(2026, 12, 11), 14));

    [Fact]
    public void An_observed_holiday_counts_as_the_holiday() =>
        Assert.Equal(new DateOnly(2026, 7, 6), Holidays.AddDays(new DateOnly(2026, 5, 4), 60));

    [Fact]
    public void Intermediate_holidays_still_count() =>
        Assert.Equal(new DateOnly(2027, 1, 11), Holidays.AddDays(new DateOnly(2026, 11, 2), 70));

    [Fact]
    public void A_zero_day_period_is_the_trigger_itself() =>
        Assert.Equal(new DateOnly(2026, 7, 4), Holidays.AddDays(new DateOnly(2026, 7, 4), 0));

    [Fact]
    public void Court_days_exclude_weekends_and_holidays()
    {
        Assert.True(Holidays.IsCourtDay(new DateOnly(2026, 9, 24)));
        Assert.False(Holidays.IsCourtDay(new DateOnly(2026, 9, 26)));
        Assert.False(Holidays.IsCourtDay(new DateOnly(2026, 11, 26)));
    }

    // ── The loader refuses what it should ──

    [Fact]
    public void A_well_formed_release_loads() =>
        Assert.Equal("code/legal-holidays@2021-06-17", LegalHolidays.LoadRegistry(Tree("""[{"holiday_id": "x", "name": "X", "citation": "c", "rule": {"month": 1, "day": 1}}]"""))[0].ReleaseId);

    [Theory]
    [InlineData("""{"month": 13, "day": 1}""")]
    [InlineData("""{"month": 1}""")]
    [InlineData("""{"month": 1, "day": 1, "weekday": "monday", "ordinal": 1}""")]
    [InlineData("""{"month": 1, "weekday": "someday", "ordinal": 1}""")]
    [InlineData("""{"month": 1, "weekday": "monday", "ordinal": 6}""")]
    public void A_malformed_date_rule_is_refused(string rule) =>
        Assert.Contains("malformed release", Assert.Throws<MalformedReleaseException>(() => LegalHolidays.LoadRegistry(Tree($$"""[{"holiday_id": "x", "name": "X", "citation": "c", "rule": {{rule}}}]"""))).Message, StringComparison.Ordinal);

    [Fact]
    public void A_manifest_that_disagrees_with_its_path_is_refused() =>
        Assert.Contains(
            "disagrees with the path",
            Assert.Throws<MalformedReleaseException>(() => LegalHolidays.LoadRegistry(Tree("""[{"holiday_id": "x", "name": "X", "citation": "c", "rule": {"month": 1, "day": 1}}]""", """{"effective_date": "2020-01-01"}"""))).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Resolution_refuses_a_date_before_the_series() =>
        Assert.Throws<EngineLookupException>(() => LegalHolidays.Resolve(new DateOnly(2020, 1, 1)));

    private static RegistryTree Tree(string holidays, string? manifestOverrides = null)
    {
        var manifest = Trees.Merge("""{"series_id": "code/legal-holidays", "effective_date": "2021-06-17", "sequence": 1, "source": {"url": "https://example.test/6103", "published": null}, "notes": "test"}""", manifestOverrides);
        var payload = new JsonObject
        {
            ["holidays"] = JsonNode.Parse(holidays),
            ["observance"] = JsonNode.Parse("""{"saturday": {"shift": "friday_before", "citation": "c"}, "sunday": {"shift": "monday_after", "citation": "c"}}"""),
            ["sources"] = JsonNode.Parse("""[{"title": "t", "url": "https://example.test", "accessed": "2026-01-01"}]"""),
        };
        return new RegistryTree(new Dictionary<string, string>
        {
            ["code/legal-holidays/2021-06-17/manifest.json"] = manifest.ToJsonString(),
            ["code/legal-holidays/2021-06-17/holidays.json"] = payload.ToJsonString(),
        });
    }
}

// Translated from services/api/tests/unit/test_deadline_rules.py: the
// committed rules pinned to the text of the rules, and the loader.
public sealed class DeadlineRuleTests
{
    public static TheoryData<string> RuleIds()
    {
        var data = new TheoryData<string>();
        foreach (var rule in DeadlineRules.All.SelectMany(release => release.Rules))
        {
            data.Add(rule.RuleId);
        }

        return data;
    }

    [Fact]
    public void The_committed_series_loads_and_names_itself() =>
        Assert.Equal(["code/deadline-rules@2024-12-01"], DeadlineRules.All.Select(release => release.ReleaseId));

    [Fact]
    public void The_restyled_rules_carry_the_counts_the_text_states()
    {
        var release = DeadlineRules.Resolve(new DateOnly(2026, 1, 1));
        var expected = new Dictionary<string, (string Anchor, int Count, int[] Chapters)>
        {
            ["frbp-1007c1-schedules"] = ("petition", 14, [7, 11, 12, 13]),
            ["frbp-1007c3-counseling-certificate"] = ("petition", 14, [7, 11, 12, 13]),
            ["frbp-3015b-chapter-13-plan"] = ("petition", 14, [13]),
            ["frbp-2003a1a-341-window"] = ("petition", 40, [7, 11]),
            ["frbp-2003a1b-341-window"] = ("petition", 35, [12]),
            ["frbp-2003a1c-341-window"] = ("petition", 50, [13]),
            ["usc-341a-meeting"] = ("meeting_341", 0, [7, 11, 12, 13]),
            ["usc-521a2a-statement-of-intention"] = ("petition", 30, [7]),
            ["usc-521a2b-perform-intention"] = ("meeting_341", 30, [7]),
            ["usc-1326a1-first-plan-payment"] = ("petition", 30, [13]),
            ["frbp-4004a-discharge-objection"] = ("meeting_341", 60, [7, 13]),
            ["frbp-4007c-dischargeability-complaint"] = ("meeting_341", 60, [7, 11, 12, 13]),
            ["frbp-4008a-reaffirmation"] = ("meeting_341", 60, [7]),
            ["frbp-1007c4-financial-management-certificate"] = ("meeting_341", 60, [7]),
            ["frbp-3002c-proof-of-claim"] = ("petition", 70, [7, 12, 13]),
            ["frbp-3002c1-governmental-proof-of-claim"] = ("petition", 180, [7, 12, 13]),
        };
        Assert.Equal(expected.Keys.ToHashSet(), release.Rules.Select(rule => rule.RuleId).ToHashSet());
        foreach (var (ruleId, (anchor, count, chapters)) in expected)
        {
            var rule = release.Rule(ruleId);
            Assert.Equal(anchor, rule.Anchor);
            Assert.Equal(count, rule.CountDays);
            Assert.Equal(chapters, rule.Chapters);
        }
    }

    [Fact]
    public void The_341_windows_start_at_twenty_one_days()
    {
        var release = DeadlineRules.Resolve(new DateOnly(2026, 1, 1));
        foreach (var ruleId in new[] { "frbp-2003a1a-341-window", "frbp-2003a1b-341-window", "frbp-2003a1c-341-window" })
        {
            var rule = release.Rule(ruleId);
            Assert.Equal("window", rule.Kind);
            Assert.Equal(21, rule.WindowStartDays);
            Assert.Equal("meeting_341", rule.UntilAnchor);
            Assert.Equal("calendar", rule.Computation);
        }
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rule_is_cited_verified_and_dated(string ruleId)
    {
        var rule = DeadlineRules.Latest().Rule(ruleId);
        Assert.False(string.IsNullOrEmpty(rule.Citation));
        Assert.False(string.IsNullOrEmpty(rule.QuotedText));
        Assert.NotEqual(Verification.Unverified, rule.Verification);
        Assert.NotEmpty(rule.Sources);
        foreach (var source in rule.Sources)
        {
            Assert.StartsWith("https://www.law.cornell.edu/", source.Url, StringComparison.Ordinal);
            Assert.True(source.Accessed <= new DateOnly(2026, 12, 31));
        }
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rolled_rule_is_a_period_stated_in_days(string ruleId)
    {
        var rule = DeadlineRules.Latest().Rule(ruleId);
        if (rule.Computation == "rule_9006a")
        {
            Assert.Equal("deadline", rule.Kind);
            Assert.True(rule.CountDays > 0);
        }
    }

    // ── The loader refuses what it should ──

    private const string BaseRule = """{"rule_id": "x", "title": "X", "citation": "c", "quoted_text": "q", "description": "d", "chapters": [7], "kind": "deadline", "anchor": "petition", "count_days": 14, "computation": "rule_9006a", "verification": "primary", "sources": [{"title": "t", "url": "https://x.test", "accessed": "2026-01-01"}]}""";

    [Fact]
    public void A_well_formed_rule_loads() =>
        Assert.Equal(14, DeadlineRules.LoadRegistry(Tree(Trees.Merge(BaseRule, null)))[0].Rule("x").CountDays);

    [Theory]
    [InlineData("""{"verification": "unverified"}""")]
    [InlineData("""{"sources": []}""")]
    [InlineData("""{"chapters": [9]}""")]
    [InlineData("""{"anchor": "confirmation_hearing"}""")]
    [InlineData("""{"kind": "deadline", "count_days": 0}""")]
    [InlineData("""{"kind": "window"}""")]
    [InlineData("""{"kind": "window", "window_start_days": 50, "count_days": 40}""")]
    [InlineData("""{"kind": "meeting", "anchor": "petition", "count_days": 0}""")]
    [InlineData("""{"computation": "business_days"}""")]
    [InlineData("""{"quoted_text": ""}""")]
    public void A_malformed_rule_is_refused(string overrides) =>
        Assert.Contains("malformed release", Assert.Throws<MalformedReleaseException>(() => DeadlineRules.LoadRegistry(Tree(Trees.Merge(BaseRule, overrides)))).Message, StringComparison.Ordinal);

    [Fact]
    public void Duplicate_rule_ids_are_refused() =>
        Assert.Contains("duplicate rule ids", Assert.Throws<MalformedReleaseException>(() => DeadlineRules.LoadRegistry(Tree(Trees.Merge(BaseRule, null), Trees.Merge(BaseRule, null)))).Message, StringComparison.Ordinal);

    [Fact]
    public void Resolution_refuses_a_petition_before_the_series() =>
        Assert.Throws<EngineLookupException>(() => DeadlineRules.Resolve(new DateOnly(2024, 11, 30)));

    private static RegistryTree Tree(params JsonObject[] rules) => new(new Dictionary<string, string>
    {
        ["code/deadline-rules/2024-12-01/manifest.json"] = """{"series_id": "code/deadline-rules", "effective_date": "2024-12-01", "sequence": 1, "source": {"url": "https://x.test", "published": null}, "notes": "test"}""",
        ["code/deadline-rules/2024-12-01/rules.json"] = new JsonObject { ["rules"] = new JsonArray([.. rules]) }.ToJsonString(),
    });
}

// Translated from the deadline-engine half of services/api/tests/unit/test_events.py
// (generation from a case's anchors). The reconcile test waits for the
// events port: it diffs against stored Event records Core does not carry yet.
public sealed class DeadlineEngineTests
{
    private static readonly RulesRelease Rules = DeadlineRules.Resolve(new DateOnly(2026, 1, 1));
    private static readonly HolidayRelease Holidays = LegalHolidays.Resolve(new DateOnly(2026, 1, 1));

    [Fact]
    public void Nothing_is_generated_without_a_filed_date() =>
        Assert.Empty(DeadlineEngine.Generate(Case(), Rules, Holidays));

    [Fact]
    public void A_chapter_7_petition_alone_yields_the_petition_anchored_rules()
    {
        var produced = ByRule(DeadlineEngine.Generate(Case(filedAt: new DateOnly(2026, 3, 2)), Rules, Holidays));
        Assert.Equal(
            new HashSet<string> { "frbp-1007c1-schedules", "frbp-1007c3-counseling-certificate", "frbp-2003a1a-341-window", "usc-521a2a-statement-of-intention", "frbp-3002c-proof-of-claim", "frbp-3002c1-governmental-proof-of-claim" },
            produced.Keys.ToHashSet());
        Assert.Equal((new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 16)), produced["frbp-1007c1-schedules"]);
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1)), produced["usc-521a2a-statement-of-intention"]);
        Assert.Equal((new DateOnly(2026, 5, 11), new DateOnly(2026, 5, 11)), produced["frbp-3002c-proof-of-claim"]);
        Assert.Equal((new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 31)), produced["frbp-3002c1-governmental-proof-of-claim"]);
        Assert.Equal((new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 11)), produced["frbp-2003a1a-341-window"]);
    }

    [Fact]
    public void The_341_date_replaces_the_window_and_unlocks_the_rest()
    {
        var produced = ByRule(DeadlineEngine.Generate(Case(filedAt: new DateOnly(2026, 3, 2), meeting: new DateOnly(2026, 4, 7)), Rules, Holidays));
        Assert.DoesNotContain("frbp-2003a1a-341-window", produced.Keys);
        Assert.Equal((new DateOnly(2026, 4, 7), new DateOnly(2026, 4, 7)), produced["usc-341a-meeting"]);
        foreach (var ruleId in new[] { "frbp-4004a-discharge-objection", "frbp-4007c-dischargeability-complaint", "frbp-4008a-reaffirmation", "frbp-1007c4-financial-management-certificate" })
        {
            Assert.Equal((new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 8)), produced[ruleId]);
        }

        Assert.Equal((new DateOnly(2026, 5, 7), new DateOnly(2026, 5, 7)), produced["usc-521a2b-perform-intention"]);
    }

    [Fact]
    public void The_statement_of_intention_is_capped_by_an_earlier_341_date()
    {
        var produced = ByRule(DeadlineEngine.Generate(Case(filedAt: new DateOnly(2026, 3, 2), meeting: new DateOnly(2026, 3, 25)), Rules, Holidays));
        Assert.Equal((new DateOnly(2026, 3, 25), new DateOnly(2026, 3, 25)), produced["usc-521a2a-statement-of-intention"]);
    }

    [Fact]
    public void Chapter_13_gets_its_own_rules()
    {
        var produced = ByRule(DeadlineEngine.Generate(Case(chapter: 13, filedAt: new DateOnly(2026, 3, 2)), Rules, Holidays));
        Assert.Contains("frbp-3015b-chapter-13-plan", produced.Keys);
        Assert.Contains("usc-1326a1-first-plan-payment", produced.Keys);
        Assert.DoesNotContain("usc-521a2a-statement-of-intention", produced.Keys);
        Assert.Equal((new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 21)), produced["frbp-2003a1c-341-window"]);
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 1)), produced["usc-1326a1-first-plan-payment"]);
    }

    [Fact]
    public void A_deadline_rolls_over_a_legal_holiday()
    {
        var produced = ByRule(DeadlineEngine.Generate(Case(filedAt: new DateOnly(2026, 8, 7)), Rules, Holidays));
        Assert.Equal((new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8)), produced["usc-521a2a-statement-of-intention"]);
    }

    [Fact]
    public void A_counted_deadline_carries_the_holiday_caveat_and_a_window_does_not()
    {
        var produced = DeadlineEngine.Generate(Case(filedAt: new DateOnly(2026, 3, 2)), Rules, Holidays);
        Assert.Contains(DeadlineEngine.HolidayCaveat, produced.Single(d => d.Rule.RuleId == "frbp-1007c1-schedules").Description(), StringComparison.Ordinal);
        Assert.DoesNotContain(DeadlineEngine.HolidayCaveat, produced.Single(d => d.Rule.RuleId == "frbp-2003a1a-341-window").Description(), StringComparison.Ordinal);
    }

    private static Case Case(int chapter = 7, DateOnly? filedAt = null, DateOnly? meeting = null) =>
        new("00000000-0000-4000-8000-0000000000c1", "00000000-0000-4000-8000-00000000f18a", "00000000-0000-4000-8000-00000000a11c", chapter, "NDCA", CaseStatus.Filed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            FiledAt = filedAt,
            Meeting341At = meeting,
        };

    private static Dictionary<string, (DateOnly Start, DateOnly End)> ByRule(IReadOnlyList<GeneratedDeadline> deadlines) =>
        deadlines.ToDictionary(deadline => deadline.Rule.RuleId, deadline => (deadline.Start, deadline.End));
}
