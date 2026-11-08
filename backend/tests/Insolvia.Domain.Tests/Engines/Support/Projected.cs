using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.Claims;
using Insolvia.Engines.Projections;

namespace Insolvia.Engines.Tests.Support;

/// <summary>The helpers the translated test_form_projections.py cases share.</summary>
internal static class Projected
{
    public static FieldValues Project(string series, CaseFile caseFile) => FormProjections.Project(FormTemplates.Latest(series), caseFile);

    /// <summary>One printed row's fill for a repeated field, by the spec's row order (Python's <c>row</c>).</summary>
    public static FieldEntry Row(FieldValues values, string series, string fieldId, int index)
    {
        var spec = FormTemplates.Latest(series).Field(fieldId);
        var entry = values[fieldId];
        if (spec.PdfNames.Count == 1)
        {
            Assert.Equal(0, index);
            return entry;
        }

        return Assert.IsType<RowFills>(entry)[spec.PdfNames[index]];
    }

    public static bool HasRow(FieldValues values, string series, string fieldId, int index) =>
        values.TryGetValue(fieldId, out var entry) && entry is RowFills rows && rows.ContainsKey(FormTemplates.Latest(series).Field(fieldId).PdfNames[index]);

    public static TextFill Text(string value) => new(value);

    public static OptionFill Option(string export) => new(export);

    public static CaseFile WithAsset(this CaseFile caseFile, string assetId, Func<AssetBody, AssetBody> change) =>
        caseFile with { Assets = [.. caseFile.Assets.Select(pair => pair.Id == assetId ? (pair.Id, change(pair.Body)) : pair)] };

    public static CaseFile WithClaim(this CaseFile caseFile, string claimId, Func<ClaimBody, ClaimBody> change) =>
        caseFile with { Claims = [.. caseFile.Claims.Select(pair => pair.Id == claimId ? (pair.Id, change(pair.Body)) : pair)] };

    public static ValueCollection<T> Values<T>(params T[] items) => new(items);

    public static FormProjectionException Refused(string series, CaseFile caseFile) =>
        Assert.Throws<FormProjectionException>(() => Project(series, caseFile));
}
