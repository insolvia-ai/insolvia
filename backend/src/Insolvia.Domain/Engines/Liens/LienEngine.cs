using System.Text.Json.Nodes;
using Insolvia.Core.Assets;
using Insolvia.Core.Claims;
using Insolvia.Engines.Projections;

namespace Insolvia.Engines.Liens;

// The lien arithmetic (from core/liens.py, issue #345): what each secured claim
// is secured by, how much of it the collateral covers, and what each asset
// carries. Per secured claim: collateral value is the typed override, else the
// asset's entire value, else its portion owned; senior liens are the other
// secured claims on the same asset with a lower position; the unsecured
// portion is the typed override or max(amount - max(collateral - seniors, 0), 0).
//
// Rounding: `_money` is `value.quantize(_CENT)` — the context default,
// ROUND_HALF_EVEN; every input is already cent-exact, so it only fixes the scale.

/// <summary>One secured claim's collateral, resolved, and its two portions. Null portions are "not derivable yet".</summary>
public sealed record ClaimLien(
    string ClaimId,
    string? AssetId,
    int? LienPosition,
    decimal? Amount,
    string? CollateralDescription,
    decimal? CollateralValue,
    decimal SeniorLiens,
    decimal? SecuredAmount,
    decimal? UnsecuredAmount,
    string UnsecuredSource);

/// <summary>What one asset carries: its linked secured claims and the sum of their amounts.</summary>
public sealed record AssetLiens(string AssetId, IReadOnlyList<string> ClaimIds, decimal SecuredTotal);

/// <summary>Every secured claim's figures, and every asset that carries a lien.</summary>
public sealed record LienFigures(IReadOnlyList<ClaimLien> Claims, IReadOnlyList<AssetLiens> Assets)
{
    /// <summary>The claim's figures, or null.</summary>
    public ClaimLien? Claim(string claimId) => Claims.FirstOrDefault(claim => claim.ClaimId == claimId);

    /// <summary>The asset's figures, or null when no secured claim names it.</summary>
    public AssetLiens? Asset(string assetId) => Assets.FirstOrDefault(asset => asset.AssetId == assetId);
}

/// <summary>The lien arithmetic (Python's <c>core/liens.py</c>).</summary>
public static class LienEngine
{
    private const decimal Zero = 0.00m;

    /// <summary>The figures for every secured claim of the case, and every asset one of them names.</summary>
    public static LienFigures Derive(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        var secured = caseFile.Claims.Where(pair => pair.Body.ClaimClass == ClaimClass.Secured).ToList();

        var claims = new List<ClaimLien>();
        foreach (var (claimId, claim) in secured)
        {
            var asset = caseFile.Asset(claim.AssetId);
            var collateralValue = CollateralValue(claim, asset);
            var description = claim.CollateralDescription;
            if (description is null && asset is not null)
            {
                description = asset.Description;
            }

            var senior = SeniorLiens(claim, secured, claimId);
            decimal? claimAmount = claim.Amount is { } amount ? Money(amount) : null;

            decimal? unsecured;
            string source;
            if (claim.UnsecuredAmountOverride is { } typed)
            {
                unsecured = Money(typed);
                source = "manual";
            }
            else if (claimAmount is null || collateralValue is null)
            {
                unsecured = null;
                source = "derived";
            }
            else
            {
                var available = Exact.Max(collateralValue.Value - senior, Zero);
                unsecured = Money(Exact.Max(claimAmount.Value - available, Zero));
                source = "derived";
            }

            decimal? securedAmount = claimAmount is { } a && unsecured is { } u ? Money(Exact.Max(a - u, Zero)) : null;
            claims.Add(new ClaimLien(claimId, claim.AssetId, claim.LienPosition, claimAmount, description, collateralValue, senior, securedAmount, unsecured, source));
        }

        var assets = new List<AssetLiens>();
        foreach (var (assetId, _) in caseFile.Assets)
        {
            var linked = secured.Where(pair => pair.Body.AssetId == assetId).ToList();
            if (linked.Count == 0)
            {
                continue;
            }

            assets.Add(new AssetLiens(assetId, [.. linked.Select(pair => pair.Id)], Money(linked.Aggregate(Zero, (sum, pair) => sum + (pair.Body.Amount ?? 0m)))));
        }

        return new LienFigures(claims, assets);
    }

    /// <summary>The wire shape of one claim's figures: camelCase, money as strings, absent members absent.</summary>
    public static JsonObject ClaimJson(ClaimLien lien)
    {
        ArgumentNullException.ThrowIfNull(lien);
        var body = new JsonObject
        {
            ["claimId"] = lien.ClaimId,
            ["seniorLiens"] = MoneyJson(lien.SeniorLiens),
            ["unsecuredSource"] = lien.UnsecuredSource,
        };
        if (lien.AssetId is not null)
        {
            body["assetId"] = lien.AssetId;
        }

        if (lien.LienPosition is { } position)
        {
            body["lienPosition"] = position;
        }

        if (lien.Amount is { } amount)
        {
            body["amount"] = MoneyJson(amount);
        }

        if (lien.CollateralDescription is not null)
        {
            body["collateralDescription"] = lien.CollateralDescription;
        }

        if (lien.CollateralValue is { } collateral)
        {
            body["collateralValue"] = MoneyJson(collateral);
        }

        if (lien.SecuredAmount is { } securedAmount)
        {
            body["securedAmount"] = MoneyJson(securedAmount);
        }

        if (lien.UnsecuredAmount is { } unsecuredAmount)
        {
            body["unsecuredAmount"] = MoneyJson(unsecuredAmount);
        }

        return body;
    }

    /// <summary>The wire shape of one asset's figures.</summary>
    public static JsonObject AssetJson(AssetLiens liens)
    {
        ArgumentNullException.ThrowIfNull(liens);
        return new JsonObject
        {
            ["assetId"] = liens.AssetId,
            ["claimIds"] = new JsonArray([.. liens.ClaimIds.Select(id => (JsonNode)id)]),
            ["securedTotal"] = MoneyJson(liens.SecuredTotal),
        };
    }

    /// <summary>The whole route body.</summary>
    public static JsonObject Json(LienFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return new JsonObject
        {
            ["claims"] = new JsonArray([.. figures.Claims.Select(claim => (JsonNode)ClaimJson(claim))]),
            ["assets"] = new JsonArray([.. figures.Assets.Select(asset => (JsonNode)AssetJson(asset))]),
        };
    }

    private static decimal Money(decimal value) => Exact.Quantize(value, Rounding.HalfEven);

    private static string MoneyJson(decimal value) => Exact.F(Money(value));

    private static decimal? CollateralValue(ClaimBody claim, AssetBody? asset)
    {
        if (claim.CollateralValue is { } typed)
        {
            return Money(typed);
        }

        if (asset is null)
        {
            return null;
        }

        foreach (var candidate in new[] { asset.ValueEntire, asset.ValuePortionOwned })
        {
            if (candidate is { } value)
            {
                return Money(value);
            }
        }

        return null;
    }

    private static decimal SeniorLiens(ClaimBody claim, List<(string Id, ClaimBody Body)> siblings, string ownId)
    {
        if (claim.AssetId is null || claim.LienPosition is not { } position)
        {
            return Zero;
        }

        var total = Zero;
        foreach (var (siblingId, sibling) in siblings)
        {
            if (siblingId == ownId || sibling.AssetId != claim.AssetId)
            {
                continue;
            }

            if (sibling.LienPosition is not { } siblingPosition || siblingPosition >= position)
            {
                continue;
            }

            total += sibling.Amount ?? 0m;
        }

        return Money(total);
    }
}
