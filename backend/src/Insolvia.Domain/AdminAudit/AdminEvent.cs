using System.Text.Json.Serialization;

namespace Insolvia.Core.AdminAudit;

// The provisioning record — who did what to which firm (ADR 0011; from
// services/admin's core/audit.py, #178/#212). The execution role is the only
// principal the database ever sees, so it can say "the admin service wrote a
// firm" and never which staff member: the verified staff identity exists only
// inside the request, which makes the admin service the only thing that can
// write it down. Append-only and write-only: the port has no read, and the
// admin role holds INSERT alone on its table — an audit log its own subject
// can read back, rewrite or erase is not evidence.

/// <summary>
/// What happened (Python's <c>ACTIONS</c>). Coarse on purpose — that an
/// administrative act occurred and by whom, not a diff; the firm row itself
/// carries the state.
/// </summary>
public enum AdminAction
{
    /// <summary>A firm and its first administrator were provisioned.</summary>
    [JsonStringEnumMemberName("firm.provision")]
    FirmProvision,

    /// <summary>A firm was suspended.</summary>
    [JsonStringEnumMemberName("firm.suspend")]
    FirmSuspend,

    /// <summary>A suspended firm was reactivated.</summary>
    [JsonStringEnumMemberName("firm.reactivate")]
    FirmReactivate,

    /// <summary>A firm user's invitation was sent again.</summary>
    [JsonStringEnumMemberName("invite.resend")]
    InviteResend,
}

/// <summary>One staff mutation (Python's <c>AdminEvent</c>).</summary>
/// <param name="FirmId">The firm the act was done to.</param>
/// <param name="Action">What was done.</param>
/// <param name="Principal">The staff caller's Google <c>sub</c> — the stable identity.</param>
/// <param name="PrincipalEmail">The staff caller's verified email — what a human reads.</param>
/// <param name="Detail">What the action named where the firm alone cannot answer (the firm's name at provision, the address an invite went to); empty otherwise.</param>
/// <param name="RecordedAt">When, UTC, to the millisecond.</param>
/// <param name="EventId">A fresh uuid, so two same-millisecond events never collide.</param>
public sealed record AdminEvent(
    string FirmId,
    AdminAction Action,
    string Principal,
    string PrincipalEmail,
    string Detail,
    DateTimeOffset RecordedAt,
    string EventId);

/// <summary>The audit rules.</summary>
public static class AdminEvents
{
    /// <summary>
    /// Python's <c>record_event</c>: stamps the time (milliseconds, UTC) and
    /// a fresh event id. An action outside <see cref="AdminAction"/> is refused.
    /// </summary>
    public static AdminEvent Record(
        string firmId,
        AdminAction action,
        string principal,
        string principalEmail,
        string detail = "",
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(firmId);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(principalEmail);
        ArgumentNullException.ThrowIfNull(detail);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "unknown admin action");
        }

        return new AdminEvent(firmId, action, principal, principalEmail, detail, Clock.NowMillis(clock), Guid.NewGuid().ToString());
    }
}
