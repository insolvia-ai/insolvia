using System.Text.Json.Nodes;
using Insolvia.Core.Assets;
using Insolvia.Core.Claims;
using Insolvia.Engines.Liens;
using Insolvia.Engines.Projections;
using Insolvia.Engines.Tests.Support;

namespace Insolvia.Engines.Tests;

// Translated from services/api/tests/unit/test_liens.py: the lien arithmetic,
// each case a file of just assets and claims, every subtraction visible by eye.
public sealed class LienTests
{
    private static readonly (string, AssetBody) House = ("asset-house", Build.Body(AssetRules.Kind, """{"description": "12 Byron Court", "value_entire": "300000.00"}"""));
    private static readonly (string, AssetBody) Car = ("asset-car", Build.Body(AssetRules.Kind, """{"description": "2016 sedan", "value_entire": "9000.00", "value_portion_owned": "4500.00"}"""));

    // ── Which claims take part ──

    [Fact]
    public void Only_secured_claims_take_part()
    {
        var figures = Figures(
            Claim("c-unsecured", """{"claim_class": "nonpriority_unsecured", "amount": "1.00"}"""),
            Claim("c-unclassed", """{"asset_id": "asset-car", "amount": "1.00"}"""),
            Secured("c-secured", """{"amount": "1.00"}"""));
        Assert.Equal(["c-secured"], figures.Claims.Select(claim => claim.ClaimId));
        Assert.Empty(figures.Assets);
    }

    [Fact]
    public void The_figures_keep_the_claims_creation_order() =>
        Assert.Equal(["c-2", "c-1"], Figures(Secured("c-2", """{"amount": "2.00"}"""), Secured("c-1", """{"amount": "1.00"}""")).Claims.Select(claim => claim.ClaimId));

    // ── The collateral value: override, asset, unknown ──

    [Fact]
    public void Without_collateral_the_portions_are_unknown_not_zero()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "1000.00"}""")).Claims);
        Assert.Null(lien.CollateralValue);
        Assert.Null(lien.UnsecuredAmount);
        Assert.Null(lien.SecuredAmount);
        Assert.Equal("derived", lien.UnsecuredSource);
    }

    [Fact]
    public void A_linked_asset_supplies_the_collateral_value_and_description()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "12000.00", "asset_id": "asset-car"}""")).Claims);
        Assert.Equal(9000.00m, lien.CollateralValue);
        Assert.Equal("2016 sedan", lien.CollateralDescription);
        Assert.Equal(3000.00m, lien.UnsecuredAmount);
        Assert.Equal(9000.00m, lien.SecuredAmount);
    }

    [Fact]
    public void The_portion_owned_stands_in_when_the_entire_value_is_absent()
    {
        var cash = ("asset-cash", Build.Body(AssetRules.Kind, """{"value_portion_owned": "150.00"}"""));
        var lien = Assert.Single(Figures([cash], Secured("c", """{"amount": "200.00", "asset_id": "asset-cash"}""")).Claims);
        Assert.Equal(150.00m, lien.CollateralValue);
        Assert.Equal(50.00m, lien.UnsecuredAmount);
    }

    [Fact]
    public void A_typed_collateral_value_and_description_win_over_the_assets()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "12000.00", "asset_id": "asset-car", "collateral_value": "4500.00", "collateral_description": "Debtor 1's half of the sedan"}""")).Claims);
        Assert.Equal(4500.00m, lien.CollateralValue);
        Assert.Equal("Debtor 1's half of the sedan", lien.CollateralDescription);
        Assert.Equal(7500.00m, lien.UnsecuredAmount);
    }

    [Fact]
    public void A_dangling_asset_id_reads_as_no_asset()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "500.00", "asset_id": "asset-gone"}""")).Claims);
        Assert.Null(lien.CollateralValue);
        Assert.Null(lien.UnsecuredAmount);
    }

    [Fact]
    public void Collateral_worth_more_than_the_claim_leaves_no_deficiency()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "195000.00", "asset_id": "asset-house"}""")).Claims);
        Assert.Equal(0.00m, lien.UnsecuredAmount);
        Assert.Equal(195000.00m, lien.SecuredAmount);
    }

    // ── Senior liens ──

    [Fact]
    public void A_chain_of_three_liens_on_one_asset()
    {
        var figures = Figures(
            Secured("first", """{"amount": "250000.00", "asset_id": "asset-house", "lien_position": 1}"""),
            Secured("second", """{"amount": "80000.00", "asset_id": "asset-house", "lien_position": 2}"""),
            Secured("third", """{"amount": "20000.00", "asset_id": "asset-house", "lien_position": 3}"""));
        var (first, second, third) = (figures.Claims[0], figures.Claims[1], figures.Claims[2]);
        Assert.Equal((0.00m, 0.00m), (first.SeniorLiens, first.UnsecuredAmount!.Value));
        Assert.Equal(250000.00m, first.SecuredAmount);
        Assert.Equal((250000.00m, 30000.00m), (second.SeniorLiens, second.UnsecuredAmount!.Value));
        Assert.Equal(50000.00m, second.SecuredAmount);
        Assert.Equal((330000.00m, 20000.00m), (third.SeniorLiens, third.UnsecuredAmount!.Value));
        Assert.Equal(0.00m, third.SecuredAmount);
        var house = Assert.Single(figures.Assets);
        Assert.Equal(["first", "second", "third"], house.ClaimIds);
        Assert.Equal(350000.00m, house.SecuredTotal);
    }

    [Fact]
    public void Reordering_the_positions_moves_the_deficiency()
    {
        var figures = Figures(
            Secured("big", """{"amount": "250000.00", "asset_id": "asset-house", "lien_position": 2}"""),
            Secured("small", """{"amount": "80000.00", "asset_id": "asset-house", "lien_position": 1}"""));
        Assert.Equal(0.00m, figures.Claims[1].UnsecuredAmount);
        Assert.Equal(80000.00m, figures.Claims[0].SeniorLiens);
        Assert.Equal(30000.00m, figures.Claims[0].UnsecuredAmount);
    }

    [Fact]
    public void A_claim_without_a_position_has_no_known_seniors_and_is_nobodys_senior()
    {
        var figures = Figures(
            Secured("placed", """{"amount": "250000.00", "asset_id": "asset-house", "lien_position": 1}"""),
            Secured("unplaced", """{"amount": "80000.00", "asset_id": "asset-house"}"""),
            Secured("junior", """{"amount": "10000.00", "asset_id": "asset-house", "lien_position": 2}"""));
        var (placed, unplaced, junior) = (figures.Claims[0], figures.Claims[1], figures.Claims[2]);
        Assert.Equal(0.00m, unplaced.SeniorLiens);
        Assert.Equal(0.00m, unplaced.UnsecuredAmount);
        Assert.Equal(250000.00m, junior.SeniorLiens);
        Assert.Equal(0.00m, placed.SeniorLiens);
        Assert.Equal(340000.00m, figures.Asset("asset-house")!.SecuredTotal);
    }

    [Fact]
    public void Equal_positions_are_neither_senior_nor_junior_to_each_other()
    {
        var figures = Figures(
            Secured("a", """{"amount": "200000.00", "asset_id": "asset-house", "lien_position": 1}"""),
            Secured("b", """{"amount": "200000.00", "asset_id": "asset-house", "lien_position": 1}"""));
        Assert.Equal(0.00m, figures.Claims[0].SeniorLiens);
        Assert.Equal(0.00m, figures.Claims[1].SeniorLiens);
    }

    [Fact]
    public void Liens_on_another_asset_are_not_senior()
    {
        var figures = Figures(
            Secured("house", """{"amount": "250000.00", "asset_id": "asset-house", "lien_position": 1}"""),
            Secured("car", """{"amount": "7000.00", "asset_id": "asset-car", "lien_position": 2}"""));
        Assert.Equal(0.00m, figures.Claims[1].SeniorLiens);
        Assert.Equal(0.00m, figures.Claims[1].UnsecuredAmount);
    }

    [Fact]
    public void A_senior_lien_with_no_amount_counts_as_nothing()
    {
        var figures = Figures(
            Secured("first", """{"asset_id": "asset-house", "lien_position": 1}"""),
            Secured("second", """{"amount": "80000.00", "asset_id": "asset-house", "lien_position": 2}"""));
        Assert.Null(figures.Claims[0].UnsecuredAmount);
        Assert.Equal(0.00m, figures.Claims[1].SeniorLiens);
    }

    // ── The manual override ──

    [Fact]
    public void A_manual_override_replaces_the_arithmetic_and_says_so()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "12000.00", "asset_id": "asset-car", "unsecured_amount_override": "5000.00"}""")).Claims);
        Assert.Equal(5000.00m, lien.UnsecuredAmount);
        Assert.Equal("manual", lien.UnsecuredSource);
        Assert.Equal(7000.00m, lien.SecuredAmount);
        Assert.Equal(9000.00m, lien.CollateralValue);
    }

    [Fact]
    public void A_manual_override_works_with_no_collateral_at_all()
    {
        var lien = Assert.Single(Figures(Secured("c", """{"amount": "1000.00", "unsecured_amount_override": "1000.00"}""")).Claims);
        Assert.Equal(1000.00m, lien.UnsecuredAmount);
        Assert.Equal(0.00m, lien.SecuredAmount);
    }

    [Fact]
    public void An_override_above_the_claim_floors_the_secured_portion_at_zero() =>
        Assert.Equal(0.00m, Assert.Single(Figures(Secured("c", """{"amount": "1000.00", "unsecured_amount_override": "1500.00"}""")).Claims).SecuredAmount);

    // ── Per asset ──

    [Fact]
    public void An_asset_without_liens_is_absent_rather_than_zero()
    {
        var figures = Figures(Secured("c", """{"amount": "1.00", "asset_id": "asset-car"}"""));
        Assert.Equal(["asset-car"], figures.Assets.Select(asset => asset.AssetId));
        Assert.Null(figures.Asset("asset-house"));
    }

    [Fact]
    public void The_assets_secured_total_is_the_sum_of_the_amounts_owed_against_it() =>
        Assert.Equal(12000.00m, Figures(
            Secured("a", """{"amount": "7000.00", "asset_id": "asset-car", "lien_position": 1}"""),
            Secured("b", """{"amount": "5000.00", "asset_id": "asset-car", "lien_position": 2}""")).Asset("asset-car")!.SecuredTotal);

    // ── The wire shape ──

    [Fact]
    public void Json_carries_money_as_two_place_strings_and_omits_the_unknown()
    {
        var json = LienEngine.Json(Figures(Secured("c", """{"amount": "1000.00"}""")));
        Assert.True(JsonNode.DeepEquals(Build.Json("""{"claimId": "c", "seniorLiens": "0.00", "unsecuredSource": "derived", "amount": "1000.00"}"""), Assert.Single(json["claims"]!.AsArray())));
        Assert.Empty(json["assets"]!.AsArray());
    }

    [Fact]
    public void Json_reports_the_full_row_when_everything_is_known()
    {
        var figures = Figures(Secured("c", """{"amount": "12000.00", "asset_id": "asset-car", "lien_position": 1, "unsecured_amount_override": "5000"}"""));
        Assert.True(JsonNode.DeepEquals(
            Build.Json("""{"claimId": "c", "seniorLiens": "0.00", "unsecuredSource": "manual", "assetId": "asset-car", "lienPosition": 1, "amount": "12000.00", "collateralDescription": "2016 sedan", "collateralValue": "9000.00", "securedAmount": "7000.00", "unsecuredAmount": "5000.00"}"""),
            LienEngine.ClaimJson(figures.Claims[0])));
        Assert.True(JsonNode.DeepEquals(Build.Json("""{"a": [{"assetId": "asset-car", "claimIds": ["c"], "securedTotal": "12000.00"}]}""")["a"], LienEngine.Json(figures)["assets"]));
    }

    [Theory]
    [InlineData("0", "0.00")]
    [InlineData("0.5", "0.50")]
    [InlineData("1234.5", "1234.50")]
    public void Every_figure_is_quantised_to_the_cent(string amount, string printed)
    {
        var lien = Assert.Single(Figures(Secured("c", $$"""{"amount": "{{amount}}", "collateral_value": "0"}""")).Claims);
        Assert.Equal(printed, LienEngine.ClaimJson(lien)["unsecuredAmount"]!.GetValue<string>());
    }

    private static (string, ClaimBody) Claim(string id, string json) => (id, Build.Body(ClaimRules.Kind, json));

    private static (string, ClaimBody) Secured(string id, string json) => Claim(id, Trees.Merge(json, """{"claim_class": "secured"}""").ToJsonString());

    private static LienFigures Figures(params (string, ClaimBody)[] claims) => Figures([House, Car], claims);

    private static LienFigures Figures((string, AssetBody)[] assets, params (string, ClaimBody)[] claims) =>
        LienEngine.Derive(new CaseFile(Build.Case()) { Assets = assets, Claims = claims });
}
