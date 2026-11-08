using System.Text.RegularExpressions;

namespace Insolvia.Core.Firms;

/// <summary>
/// Construction and the state transitions of firms and firm users (from
/// <c>firms.py</c>). Timestamps are UTC, truncated to milliseconds, as stored
/// rows already hold them.
/// </summary>
public static partial class FirmLifecycle
{
    /// <summary>
    /// A new, active firm. Provenance is optional: the admin service passes
    /// the staff caller's subject and email (#212); the seeder passes nothing,
    /// which is the truth about a fixture.
    /// </summary>
    public static Firm CreateFirm(FirmDraft draft, string? createdBy = null, string? createdByEmail = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.NowMillis(clock);
        return new Firm(
            Id: Guid.NewGuid().ToString(),
            Name: draft.Name,
            Status: FirmStatus.Active,
            CreatedAt: now,
            UpdatedAt: now,
            CreatedBy: createdBy,
            CreatedByEmail: createdByEmail);
    }

    /// <summary>
    /// The ONLY legal write to a firm's status (#212): suspend and reactivate.
    /// Idempotent — re-suspending refreshes <c>UpdatedAt</c> and nothing else,
    /// because the admin portal cannot tell whether its first request landed.
    /// </summary>
    public static Firm SetFirmStatus(Firm firm, FirmStatus status, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(firm);
        if (!Enum.IsDefined(status))
        {
            throw new ValidationException("Firm status must be one of " + string.Join(", ", Wire.Names<FirmStatus>()) + ".");
        }

        return firm with { Status = status, UpdatedAt = Clock.NowMillis(clock) };
    }

    /// <summary>
    /// A validated firm PATCH applied. Touches nothing the changes do not
    /// name — provenance survives a rename, and status cannot appear at all.
    /// </summary>
    public static Firm ApplyFirmChanges(Firm firm, FirmChanges changes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(firm);
        ArgumentNullException.ThrowIfNull(changes);
        var updated = firm with { UpdatedAt = Clock.NowMillis(clock) };
        if (changes.Name is not null)
        {
            updated = updated with { Name = changes.Name };
        }

        if (changes.DefaultCourt is not null)
        {
            updated = updated with { DefaultCourt = changes.DefaultCourt.Value };
        }

        if (changes.DefaultChapter is not null)
        {
            updated = updated with { DefaultChapter = changes.DefaultChapter.Value };
        }

        if (changes.Letterhead is not null)
        {
            updated = updated with { Letterhead = changes.Letterhead.Value };
        }

        return updated;
    }

    /// <summary>
    /// Attach a validated draft to a firm and a Cognito identity. Both keys
    /// come from the server — <paramref name="firmId"/> from the admin's
    /// accessor, <paramref name="subject"/> from the pool — and the subject
    /// must be a Cognito <c>sub</c> (it flows into keys).
    /// </summary>
    public static FirmUser CreateFirmUser(FirmUserDraft draft, string firmId, string subject, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(subject);
        if (!CognitoSubject().IsMatch(subject))
        {
            throw new ValidationException("a firm user's subject must be a Cognito sub");
        }

        var now = Clock.NowMillis(clock);
        return new FirmUser(
            FirmId: firmId,
            Subject: subject,
            Email: draft.Email,
            FirstName: draft.FirstName,
            LastName: draft.LastName,
            Role: draft.Role,
            IsAdmin: draft.IsAdmin,
            AccessAllCases: draft.AccessAllCases,
            Permissions: draft.Permissions,
            Status: FirmUserStatus.Active,
            CreatedAt: now,
            UpdatedAt: now);
    }

    /// <summary>
    /// The supplied changes applied. CHANGING A ROLE DOES NOT RESET THE
    /// PERMISSION MAP — roles supply defaults at creation and nothing after.
    /// The two name halves apply independently.
    /// </summary>
    public static FirmUser ApplyUserChanges(FirmUser user, FirmUserChanges changes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(changes);
        return user with
        {
            FirstName = changes.FirstName ?? user.FirstName,
            LastName = changes.LastName ?? user.LastName,
            Role = changes.Role ?? user.Role,
            IsAdmin = changes.IsAdmin ?? user.IsAdmin,
            AccessAllCases = changes.AccessAllCases ?? user.AccessAllCases,
            Permissions = changes.Permissions ?? user.Permissions,
            Status = changes.Status ?? user.Status,
            SignatureBlock = changes.SignatureBlock is null ? user.SignatureBlock : changes.SignatureBlock.Value,
            UpdatedAt = Clock.NowMillis(clock),
        };
    }

    // A Cognito `sub` is a lower-case uuid. Validated, not trusted: a value
    // carrying `#` would make a key that collides with another spelling.
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex CognitoSubject();
}
