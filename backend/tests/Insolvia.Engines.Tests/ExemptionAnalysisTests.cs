using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Fields;
using Insolvia.Core.Petitions;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Exemptions;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_exemption_analysis.py: the
// Schedule C workbench's arithmetic over hand-built inputs, every figure a
// registry figure or a chosen amount.
public sealed class ExemptionAnalysisTests
{
    private static readonly DateOnly Today = new(2026, 9, 16);
    private static readonly (string, AssetBody) House = ("asset-house", Build.Body(AssetRules.Kind, """{"category": "real_property", "description": "12 Byron Court", "value_entire": "300000.00", "value_portion_owned": "300000.00"}"""));
    private static readonly (string, AssetBody) Car = ("asset-car", Build.Body(AssetRules.Kind, """{"category": "vehicle", "description": "2016 sedan", "value_entire": "9000.00"}"""));
    private static readonly (string, ClaimBody) Mortgage = ("claim-mortgage", Build.Body(ClaimRules.Kind, """{"claim_class": "secured", "amount": "250000.00", "asset_id": "asset-house", "lien_position": 1}"""));

    // ── Dates ──

    [Fact]
    public void The_expected_filing_date_is_the_resolution_date_when_typed() =>
        Assert.Equal((new DateOnly(2026, 12, 1), "expected_filing_date"), ExemptionAnalyzer.ResolutionDate(Build.Body(PetitionRules.Petition, """{"expected_filing_date": "2026-12-01"}"""), Today));

    [Fact]
    public void A_floating_case_resolves_as_of_today()
    {
        Assert.Equal((Today, "today"), ExemptionAnalyzer.ResolutionDate(null, Today));
        Assert.Equal((Today, "today"), ExemptionAnalyzer.ResolutionDate(Build.Body(PetitionRules.Petition, "{}"), Today));
    }

    [Fact]
    public void Years_before_clamps_a_leap_day()
    {
        Assert.Equal(new DateOnly(2023, 2, 28), ExemptionAnalyzer.YearsBefore(new DateOnly(2028, 2, 29), 5));
        Assert.Equal(new DateOnly(2016, 9, 16), ExemptionAnalyzer.YearsBefore(new DateOnly(2026, 9, 16), 10));
    }

    [Fact]
    public void The_lookbacks_are_calendar_dates_from_the_filing_date()
    {
        var lookbacks = ExemptionAnalyzer.ComputeLookbacks(Today);
        Assert.Equal(new DateOnly(2016, 9, 16), lookbacks.Section522o);
        Assert.Equal(new DateOnly(2023, 5, 20), lookbacks.Section522p);
        Assert.Equal(new DateOnly(2021, 9, 16), lookbacks.Section522q);
        Assert.Equal(new DateOnly(2024, 9, 16), lookbacks.DomicilePeriodStart);
    }

    // ── The election ──

    [Fact]
    public void An_opt_out_state_forces_the_state_scheme_without_an_election()
    {
        var result = Analysis("FL");
        Assert.True(result.Election.OptedOut);
        Assert.Equal("Fla. Stat. § 222.20", result.Election.OptOutCitation);
        Assert.Equal("state_and_federal_nonbankruptcy", result.Election.Effective);
        Assert.Equal(["state_and_federal_nonbankruptcy"], result.Election.Options.Select(o => o.Value));
        Assert.DoesNotContain(result.Problems, problem => problem.Contains("Choose", StringComparison.Ordinal));
        Assert.Subset(result.Entries.Select(row => row.Entry.EntryId).ToHashSet(), new HashSet<string> { "fl-homestead", "fl-vehicle" });
    }

    [Fact]
    public void A_federal_election_in_an_opt_out_state_is_overridden_and_reported()
    {
        var result = Analysis("FL", ExemptionSet.Federal);
        Assert.Equal("federal", result.Election.Stored);
        Assert.Equal("state_and_federal_nonbankruptcy", result.Election.Effective);
        Assert.Contains(result.Problems, problem => problem.Contains("opted out", StringComparison.Ordinal));
    }

    [Fact]
    public void An_election_state_offers_both_and_has_no_default()
    {
        var result = Analysis("TX");
        Assert.False(result.Election.OptedOut);
        Assert.Equal(["state_and_federal_nonbankruptcy", "federal"], result.Election.Options.Select(o => o.Value));
        Assert.Null(result.Election.Effective);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, problem => problem.Contains("Choose the exemption scheme", StringComparison.Ordinal));
    }

    [Fact]
    public void The_federal_election_lists_the_522d_entries()
    {
        var result = Analysis("TX", ExemptionSet.Federal);
        Assert.Equal("federal", result.Election.Effective);
        Assert.Equal(31575.00m, Entry(result, "us-homestead").Limit);
    }

    [Fact]
    public void No_debtor_state_is_a_problem_not_a_guess()
    {
        var result = Analysis(null);
        Assert.Null(result.Election.State);
        Assert.Null(result.Election.OptedOut);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, problem => problem.Contains("no residence state", StringComparison.Ordinal));
    }

    [Fact]
    public void A_state_outside_the_launch_set_is_a_problem_not_a_guess()
    {
        var result = Analysis("CA");
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, problem => problem.Contains("not supported yet", StringComparison.Ordinal));
    }

    [Fact]
    public void A_filing_date_before_the_series_baseline_refuses()
    {
        var result = Analysis("GA", petition: Build.Body(PetitionRules.Petition, """{"expected_filing_date": "2026-06-01"}"""));
        Assert.Equal(new DateOnly(2026, 6, 1), result.AsOf);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Problems, problem => problem.Contains("no release of exemptions/ga", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("federal", "FL", true)]
    [InlineData("federal", "GA", true)]
    [InlineData("federal", "TX", false)]
    [InlineData("state_and_federal_nonbankruptcy", "FL", false)]
    [InlineData("federal", null, false)]
    [InlineData("federal", "CA", false)]
    public void Election_refusal_is_the_registrys_opt_out_rule(string election, string? state, bool refused) =>
        Assert.Equal(refused, ExemptionAnalyzer.ElectionRefusal(election, state, Today) is not null);

    // ── Per statute ──

    [Fact]
    public void Claimed_draws_the_limit_down_and_over_claiming_floors_at_zero()
    {
        var result = Analysis("FL", exemptions: [Claim("e-1", "asset-car", "Fla. Stat. § 222.25(1)", "3000.00"), Claim("e-2", "asset-car", "Fla. Stat. § 222.25(1)", "4000.00")]);
        var vehicle = Entry(result, "fl-vehicle");
        Assert.Equal(5000.00m, vehicle.Limit);
        Assert.Equal(7000.00m, vehicle.Claimed);
        Assert.Equal(0.00m, vehicle.Available);
        var untouched = Entry(result, "fl-personal-property");
        Assert.Equal(0.00m, untouched.Claimed);
        Assert.Equal(1000.00m, untouched.Available);
    }

    [Fact]
    public void An_unlimited_entry_has_no_limit_and_no_available_figure()
    {
        var homestead = Entry(Analysis("FL", exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(1)", fullFmv: true)]), "fl-homestead");
        Assert.True(homestead.Entry.Unlimited);
        Assert.Null(homestead.Limit);
        Assert.Null(homestead.Available);
        Assert.Equal(300000.00m, homestead.Claimed);
    }

    [Fact]
    public void A_full_fmv_claim_under_a_capped_statute_counts_up_to_the_limit()
    {
        var homestead = Entry(Analysis("TX", ExemptionSet.Federal, exemptions: [Claim("e-1", "asset-house", "11 U.S.C. § 522(d)(1)", fullFmv: true)]), "us-homestead");
        Assert.Equal(31575.00m, homestead.Claimed);
        Assert.Equal(0.00m, homestead.Available);
    }

    [Fact]
    public void A_wildcard_absorbs_unused_homestead_up_to_the_carryover_cap()
    {
        var wildcard = Entry(Analysis("GA"), "ga-wildcard");
        Assert.Equal(10000.00m, wildcard.Carryover);
        Assert.Equal(11200.00m, wildcard.Limit);

        wildcard = Entry(Analysis("GA", exemptions: [Claim("e-1", "asset-house", "O.C.G.A. § 44-13-100(a)(1)", "45000.00")]), "ga-wildcard");
        Assert.Equal(5000.00m, wildcard.Carryover);
        Assert.Equal(6200.00m, wildcard.Limit);
    }

    [Fact]
    public void A_joint_case_uses_the_statutes_joint_figure_where_it_names_one()
    {
        Assert.Equal(50000.00m, Entry(Analysis("GA"), "ga-homestead").Limit);
        Assert.Equal(100000.00m, Entry(Analysis("GA", joint: true), "ga-homestead").Limit);
    }

    [Fact]
    public void A_claim_may_cite_any_one_authority_the_entry_lists()
    {
        var result = Analysis("FL", exemptions: [Claim("e-1", "asset-house", "Fla. Stat. §§ 222.01-222.02", "1.00")]);
        Assert.Equal(1.00m, Entry(result, "fl-homestead").Claimed);
        Assert.True(Assert.Single(Asset(result, "asset-house").Claims).KnownStatute);
    }

    [Fact]
    public void A_claim_citing_a_statute_outside_the_scheme_draws_nothing_and_warns()
    {
        var result = Analysis("FL", exemptions: [Claim("e-1", "asset-car", "11 U.S.C. § 522(d)(2)", "1000.00")]);
        Assert.All(result.Entries, row => Assert.Equal(0.00m, row.Claimed));
        Assert.False(Assert.Single(Asset(result, "asset-car").Claims).KnownStatute);
        Assert.Contains(result.Warnings, warning => warning.Contains("11 U.S.C. § 522(d)(2)", StringComparison.Ordinal));
    }

    // ── Per asset ──

    [Fact]
    public void Value_less_liens_is_net_equity_less_claimed_is_unexempt()
    {
        var house = Asset(Analysis("FL", exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(2)", "1000.00")]), "asset-house");
        Assert.Equal(300000.00m, house.CurrentValue);
        Assert.Equal(250000.00m, house.Liens);
        Assert.Equal(50000.00m, house.NetEquity);
        Assert.Equal(1000.00m, house.Claimed);
        Assert.Equal(49000.00m, house.Unexempt);
        var row = Assert.Single(house.Claims);
        Assert.Equal("e-1", row.ExemptionId);
        Assert.Equal(1000.00m, row.Claimed);
    }

    [Fact]
    public void An_asset_with_no_value_reports_unknown_not_zero()
    {
        var bare = ("asset-bare", Build.Body(AssetRules.Kind, """{"category": "cash", "description": "Wallet"}"""));
        var row = Asset(Analysis("FL", assets: [bare], claims: []), "asset-bare");
        Assert.Null(row.CurrentValue);
        Assert.Null(row.NetEquity);
        Assert.Null(row.Unexempt);
        Assert.Equal(0.00m, row.Liens);
    }

    [Fact]
    public void Liens_never_push_net_equity_below_zero()
    {
        var underwater = ("asset-car", Build.Body(AssetRules.Kind, """{"category": "vehicle", "value_entire": "9000.00"}"""));
        var loan = ("claim-loan", Build.Body(ClaimRules.Kind, """{"claim_class": "secured", "amount": "12000.00", "asset_id": "asset-car"}"""));
        Assert.Equal(0.00m, Asset(Analysis("FL", assets: [underwater], claims: [loan]), "asset-car").NetEquity);
    }

    [Fact]
    public void The_suggested_amount_is_the_lesser_of_unexempt_and_available()
    {
        var result = Analysis("FL");
        var house = Asset(result, "asset-house").Suggestions.ToDictionary(pair => pair.EntryId, pair => pair.Amount);
        Assert.Equal(1000.00m, house["fl-personal-property"]);
        Assert.Equal(50000.00m, house["fl-homestead"]);
        Assert.Equal(5000.00m, Asset(result, "asset-car").Suggestions.ToDictionary(pair => pair.EntryId, pair => pair.Amount)["fl-vehicle"]);
    }

    [Fact]
    public void The_1215_day_homestead_cap_binds_a_recently_acquired_homestead()
    {
        var house = Asset(Analysis("FL", claims: [], exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(1)", fullFmv: true, acquired: true)]), "asset-house");
        Assert.Equal(214000.00m, house.HomesteadCap);
        Assert.True(house.CapApplied);
        Assert.Equal(300000.00m, house.Claimed);
        Assert.Equal(86000.00m, house.Unexempt);
    }

    [Fact]
    public void The_cap_is_not_applied_to_a_homestead_held_longer()
    {
        var house = Asset(Analysis("FL", claims: [], exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(1)", fullFmv: true, acquired: false)]), "asset-house");
        Assert.Null(house.HomesteadCap);
        Assert.False(house.CapApplied);
        Assert.Equal(0.00m, house.Unexempt);
    }

    [Fact]
    public void The_cap_is_reported_but_not_applied_under_it()
    {
        var house = Asset(Analysis("FL", exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(1)", "40000.00", acquired: true)]), "asset-house");
        Assert.Equal(214000.00m, house.HomesteadCap);
        Assert.False(house.CapApplied);
        Assert.Equal(10000.00m, house.Unexempt);
    }

    // ── The domicile warning ──

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("debtor_1", "2025-03-01", true)]
    [InlineData("both", null, true)]
    [InlineData("debtor_1", "2024-01-01", false)]
    [InlineData("debtor_2", "2025-03-01", false)]
    public void The_730_day_domicile_rule_warns_when_a_prior_address_overlaps(string? which, string? toDate, bool warned)
    {
        SofaEntryBody[] entries = which is null
            ? []
            : [new SofaEntryBody(SofaEntryType.PriorAddress, new PriorAddress(Wire.TryParse<SofaDebtor>(which, out var debtor) ? debtor : null, new Address(State: "NY"), new DateOnly(2022, 1, 1), Py.ParseIso(toDate)))];
        Assert.Equal(warned, Analysis("FL", sofa: entries).Warnings.Any(warning => warning.Contains("§ 522(b)(3)(A)", StringComparison.Ordinal)));
    }

    // ── The wire shape ──

    [Fact]
    public void The_json_carries_money_as_strings_and_null_for_unknown()
    {
        var bare = ("asset-bare", Build.Body(AssetRules.Kind, """{"category": "cash"}"""));
        var body = ExemptionAnalyzer.Json(Analysis("FL", assets: [House, bare], exemptions: [Claim("e-1", "asset-house", "Fla. Const. art. X, § 4(a)(2)", "1000.00")]));
        Assert.Equal("2026-09-16", body["asOf"]!.GetValue<string>());
        Assert.Equal("today", body["asOfSource"]!.GetValue<string>());
        Assert.Equal("state_and_federal_nonbankruptcy", body["election"]!["effective"]!.GetValue<string>());
        Assert.True(body["election"]!["optedOut"]!.GetValue<bool>());
        Assert.Equal("2023-05-20", body["lookbacks"]!["section522p"]!.GetValue<string>());
        var house = body["assets"]![0]!;
        var unknown = body["assets"]![1]!;
        Assert.Equal("50000.00", house["netEquity"]!.GetValue<string>());
        Assert.Equal("49000.00", house["unexempt"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            Build.Json("""{"exemptionId": "e-1", "statuteCitation": "Fla. Const. art. X, § 4(a)(2)", "amount": "1000.00", "claimsFullFmv": null, "acquiredWithin1215Days": null, "claimed": "1000.00", "knownStatute": true}"""),
            house["claims"]![0]));
        Assert.Equal("49000.00", house["suggestions"]!["fl-homestead"]!.GetValue<string>());
        Assert.Null(unknown["currentValue"]);
        Assert.Null(unknown["unexempt"]);
        var personal = body["entries"]!.AsArray().First(e => e!["entryId"]!.GetValue<string>() == "fl-personal-property")!;
        Assert.Equal("1000.00", personal["limit"]!.GetValue<string>());
        Assert.Equal("1000.00", personal["claimed"]!.GetValue<string>());
        Assert.Equal("0.00", personal["available"]!.GetValue<string>());
        Assert.Contains(body["limits"]!.AsArray(), limit => limit!["limitId"]!.GetValue<string>() == "us-homestead-1215-day-cap");
    }

    private static (string, ExemptionBody) Claim(string id, string assetId, string citation, string? amount = null, bool? fullFmv = null, bool? acquired = null) =>
        (id, new ExemptionBody(assetId, citation, amount is null ? null : Py.Dec(amount), fullFmv, acquired));

    private static ExemptionAnalysis Analysis(
        string? state,
        ExemptionSet? set = null,
        bool joint = false,
        PetitionBody? petition = null,
        (string, AssetBody)[]? assets = null,
        (string, ClaimBody)[]? claims = null,
        (string, ExemptionBody)[]? exemptions = null,
        SofaEntryBody[]? sofa = null)
    {
        var debtors = new List<Debtor>();
        if (state is not null)
        {
            debtors.Add(Build.Debtor(FilingRole.Debtor1, "debtor-debtor_1", state));
            if (joint)
            {
                debtors.Add(Build.Debtor(FilingRole.Debtor2, "debtor-debtor_2", state));
            }
        }

        var inputs = new ExemptionCase(Build.Case() with { ExemptionSet = set })
        {
            Debtors = debtors,
            Petition = petition,
            Assets = assets ?? [House, Car],
            Claims = claims ?? [Mortgage],
            Exemptions = exemptions ?? [],
            SofaEntries = sofa ?? [],
        };
        return ExemptionAnalyzer.Analyse(inputs, Today);
    }

    private static EntryAvailability Entry(ExemptionAnalysis result, string entryId) => result.Entries.First(row => row.Entry.EntryId == entryId);

    private static AssetExemptions Asset(ExemptionAnalysis result, string assetId) => result.Assets.First(row => row.AssetId == assetId);
}
