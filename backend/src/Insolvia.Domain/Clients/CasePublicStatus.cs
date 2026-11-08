using Insolvia.Core.Cases;

namespace Insolvia.Core.Clients;

/// <summary>
/// What a CLIENT may read of their case: its chapter and its stage — and
/// nothing else (ADR 0023 decision 4; from <c>clients.py</c>). A PROJECTION,
/// not a <see cref="Case"/>: the store method that produces it takes the
/// <see cref="Access.ClientAccessor"/> and returns this type, so no portal route
/// holds a case to leak a district, a date or a pin from.
/// <see cref="Stage"/> is the lifecycle status, not the overview's computed spine.
/// </summary>
public sealed record CasePublicStatus(int Chapter, CaseStatus Stage);

/// <summary>Building the client's projection.</summary>
public static class CasePublicStatuses
{
    /// <summary>
    /// The projection of <paramref name="stored"/>, or null unless it belongs to
    /// <paramref name="firmId"/> — the binding's firm — and is not deleted. A
    /// mismatch is corruption, and corruption answers "no case" rather than
    /// another firm's chapter (Python's <c>public_status_from_case_item</c>).
    /// </summary>
    public static CasePublicStatus? From(Case stored, string firmId)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return stored.FirmId != firmId || stored.Deleted ? null : new CasePublicStatus(stored.Chapter, stored.Status);
    }
}
