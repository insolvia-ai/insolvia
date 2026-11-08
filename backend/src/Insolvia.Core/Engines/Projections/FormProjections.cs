using Insolvia.Engines.Projections.Forms;

namespace Insolvia.Engines.Projections;

/// <summary>
/// The projection registry (from <c>form_projections/__init__.py</c>): case
/// data in, fill-engine values out — per form, per revision. Projections are
/// registered per release; a revision bump brings its own mapping, and
/// <see cref="Project"/> refuses a release nothing was written for.
/// </summary>
public static class FormProjections
{
    /// <summary>The mapping for each (series, pin) release.</summary>
    public static readonly IReadOnlyDictionary<(string SeriesId, string Pin), Func<FormRelease, CaseFile, FieldValues>> Projections =
        new Dictionary<(string, string), Func<FormRelease, CaseFile, FieldValues>>
        {
            [("form/b101", "2024-06-22")] = B101.Project,
            [("form/b106ab", "2015-12-01")] = B106AB.Project,
            [("form/b106c", "2025-04-01")] = B106C.Project,
            [("form/b106d", "2015-12-01")] = B106D.Project,
            [("form/b106dec", "2015-12-01")] = B106Dec.Project,
            [("form/b106ef", "2015-12-01")] = B106EF.Project,
            [("form/b106g", "2015-12-01")] = B106G.Project,
            [("form/b106h", "2015-12-01")] = B106H.Project,
            [("form/b106i", "2015-12-01")] = B106I.Project,
            [("form/b106j", "2015-12-01")] = B106J.Project,
            [("form/b106j2", "2015-12-01")] = B106J2.Project,
            [("form/b106sum", "2015-12-01")] = B106Sum.Project,
            [("form/b107", "2025-04-01")] = B107.Project,
            [("form/b108", "2015-12-01")] = B108.Project,
            [("form/b113", "2017-12-01")] = B113.Project,
            [("form/b121", "2015-12-01")] = B121.Project,
            [("form/b122a1", "2019-12-01")] = B122A1.Project,
            [("form/b122a2", "2025-04-01")] = B122A2.Project,
            [("form/b122c1", "2019-10-01")] = B122C1.Project,
            [("form/b122c2", "2025-04-01")] = B122C2.Project,
            [("form/b2010", "2020-12-01")] = B2010.Project,
            [("form/b2030", "2025-12-01")] = B2030.Project,
        };

    /// <summary>The values for one form release from one case's facts; <see cref="EngineKeyException"/> when no mapping is written for it.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!Projections.TryGetValue((release.SeriesId, release.Pin), out var projector))
        {
            throw new EngineKeyException($"no projection is written for {release.ReleaseId}; a revision bump is a template change with its own mapping and goldens");
        }

        return projector(release, caseFile);
    }
}
