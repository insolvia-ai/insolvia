using System.Text.Json.Serialization;
using Insolvia.Domain.Engines;
using Insolvia.Domain.Engines.Liens;

namespace Insolvia.Api.Analysis;

/// <summary>
/// The derived collateral figures: each secured claim's collateral and its
/// secured and unsecured portions, and what each asset carries. Money is a
/// decimal string to the cent.
/// </summary>
/// <param name="Claims">One entry per secured claim.</param>
/// <param name="Assets">One entry per asset with a linked secured claim.</param>
public sealed record LienView(IReadOnlyList<ClaimLienView> Claims, IReadOnlyList<AssetLienView> Assets)
{
    /// <summary>The view of <paramref name="figures"/>.</summary>
    /// <param name="figures">The engine's figures.</param>
    /// <returns>The view.</returns>
    public static LienView Of(LienFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return new([.. figures.Claims.Select(ClaimLienView.Of)], [.. figures.Assets.Select(asset => new AssetLienView(asset.AssetId, asset.ClaimIds, Money(asset.SecuredTotal)))]);
    }

    // The engine's rounding: ROUND_HALF_EVEN to the cent.
    internal static string Money(decimal value) => Exact.Money(value, Rounding.HalfEven);

    internal static string? Money(decimal? value) => value is { } present ? Money(present) : null;
}

/// <summary>One secured claim's collateral, resolved, and its two portions; a figure not derivable yet is absent.</summary>
/// <param name="ClaimId">The claim record.</param>
/// <param name="SeniorLiens">The claims ahead of it on the same collateral.</param>
/// <param name="UnsecuredSource">How the unsecured portion was reached, or why it was not.</param>
/// <param name="AssetId">The collateral's asset record; absent when unlinked.</param>
/// <param name="LienPosition">Its position among the asset's liens; absent when not entered.</param>
/// <param name="Amount">The claim amount; absent when not entered.</param>
/// <param name="CollateralDescription">The collateral, as described.</param>
/// <param name="CollateralValue">The collateral's value; absent when not known.</param>
/// <param name="SecuredAmount">The portion the collateral covers.</param>
/// <param name="UnsecuredAmount">The portion it does not.</param>
public sealed record ClaimLienView(
    string ClaimId,
    string SeniorLiens,
    string UnsecuredSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AssetId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LienPosition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollateralDescription,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollateralValue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SecuredAmount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnsecuredAmount)
{
    /// <summary>The view of <paramref name="lien"/>.</summary>
    /// <param name="lien">The claim's figures.</param>
    /// <returns>The view.</returns>
    public static ClaimLienView Of(ClaimLien lien)
    {
        ArgumentNullException.ThrowIfNull(lien);
        return new(
            lien.ClaimId,
            LienView.Money(lien.SeniorLiens),
            lien.UnsecuredSource,
            lien.AssetId,
            lien.LienPosition,
            LienView.Money(lien.Amount),
            lien.CollateralDescription,
            LienView.Money(lien.CollateralValue),
            LienView.Money(lien.SecuredAmount),
            LienView.Money(lien.UnsecuredAmount));
    }
}

/// <summary>What one asset carries.</summary>
/// <param name="AssetId">The asset record.</param>
/// <param name="ClaimIds">Its linked secured claims.</param>
/// <param name="SecuredTotal">The sum of their amounts.</param>
public sealed record AssetLienView(string AssetId, IReadOnlyList<string> ClaimIds, string SecuredTotal);
