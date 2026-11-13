using Insolvia.Data.Waitlist;
using Insolvia.Domain;

namespace Insolvia.Api.Waitlist;

/// <summary>A waitlist record's identity (services/api core/waitlist.py).</summary>
public static class WaitlistSubmissions
{
    /// <summary>
    /// Stamp a submission with a uuid4 and the UTC instant to the
    /// millisecond — the marketing implementation's <c>toISOString()</c>.
    /// </summary>
    /// <param name="submission">The submission.</param>
    /// <param name="clock">The clock; the system clock when null.</param>
    /// <returns>The record.</returns>
    public static WaitlistRecord Create(WaitlistSubmission submission, TimeProvider? clock = null) =>
        new(Guid.NewGuid().ToString(), Clock.NowMillis(clock), submission);
}
