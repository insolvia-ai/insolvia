namespace Insolvia.Engines.Regulatory;

/// <summary>What every registry release carries for resolution (effective-dating.md).</summary>
public interface IRelease
{
    /// <summary>The series, like <c>ust/irs-local-standards</c>.</summary>
    string SeriesId { get; }

    /// <summary>The date the release takes force.</summary>
    DateOnly EffectiveDate { get; }

    /// <summary>1, or higher for a correction effective the same day.</summary>
    int Sequence { get; }

    /// <summary><c>series@date[+sequence]</c>.</summary>
    string ReleaseId { get; }
}

/// <summary>The resolution mechanics every registry consumer shares.</summary>
public static class Releases
{
    /// <summary><c>series@date</c>, with <c>+sequence</c> past the first.</summary>
    public static string Id(string seriesId, DateOnly effective, int sequence)
    {
        var baseId = $"{seriesId}@{Exact.Iso(effective)}";
        return sequence == 1 ? baseId : $"{baseId}+{sequence}";
    }

    /// <summary>The pin a case records: <c>date[+sequence]</c>.</summary>
    public static string Pin(DateOnly effective, int sequence) =>
        sequence == 1 ? Exact.Iso(effective) : $"{Exact.Iso(effective)}+{sequence}";

    /// <summary>
    /// The release with the greatest effective date on or before
    /// <paramref name="asOf"/>, ties to the highest sequence; refuses a date
    /// before the series begins with the registry's own message.
    /// </summary>
    public static TRelease Pick<TRelease>(IReadOnlyList<TRelease> candidates, DateOnly asOf, string refusal)
        where TRelease : IRelease
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var applicable = candidates.Where(release => release.EffectiveDate <= asOf).ToList();
        if (applicable.Count == 0)
        {
            var earliest = candidates.Min(release => release.EffectiveDate);
            throw new EngineLookupException(
                $"no release of {candidates[0].SeriesId} is effective on or "
                + $"before {Exact.Iso(asOf)} (series begins {Exact.Iso(earliest)}); {refusal}");
        }

        return MaxByKey(applicable);
    }

    /// <summary>Python's <c>max(applicable, key=(effective_date, sequence))</c>: the first of equal maxima.</summary>
    internal static TRelease MaxByKey<TRelease>(IReadOnlyList<TRelease> applicable)
        where TRelease : IRelease
    {
        var best = applicable[0];
        foreach (var release in applicable.Skip(1))
        {
            if (release.EffectiveDate > best.EffectiveDate
                || (release.EffectiveDate == best.EffectiveDate && release.Sequence > best.Sequence))
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>A series' releases sorted oldest first, refusing duplicate ids.</summary>
    internal static IReadOnlyList<TRelease> Ordered<TRelease>(List<TRelease> loaded, string seriesId)
        where TRelease : IRelease
    {
        var ordered = loaded
            .OrderBy(release => release.EffectiveDate)
            .ThenBy(release => release.Sequence)
            .ToList();
        if (ordered.Select(release => release.ReleaseId).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
        {
            throw ReleaseParsing.Fail(seriesId, "duplicate release ids");
        }

        return ordered;
    }
}
