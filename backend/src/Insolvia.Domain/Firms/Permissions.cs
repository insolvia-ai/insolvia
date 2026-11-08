namespace Insolvia.Core.Firms;

/// <summary>
/// Per-feature permissions and the admin invariant (from <c>firms.py</c>).
/// The fail-closed rules live here and nowhere else.
/// </summary>
public static class Permissions
{
    /// <summary>
    /// What a new user of <paramref name="role"/> gets before an admin touches
    /// anything. ATTORNEY AND PARALEGAL ARE IDENTICAL — a statement, not an
    /// oversight. Staff is narrower: documents and tasks, the case record
    /// read-only. No role ever gets <see cref="Feature.FirmAdministration"/>,
    /// <see cref="Feature.ClientPortal"/> or <see cref="Feature.ElectronicFiling"/>
    /// by default.
    /// </summary>
    public static ValueDictionary<Feature, PermissionLevel> DefaultFor(FirmRole role)
    {
        var map = role == FirmRole.Staff
            ? new Dictionary<Feature, PermissionLevel>
            {
                [Feature.Cases] = PermissionLevel.ViewOnly,
                [Feature.Intake] = PermissionLevel.ViewOnly,
                [Feature.Documents] = PermissionLevel.AddEdit,
                [Feature.ExtractionReview] = PermissionLevel.Hidden,
                [Feature.CreditorLibrary] = PermissionLevel.ViewOnly,
                [Feature.Notes] = PermissionLevel.Hidden,
                [Feature.Events] = PermissionLevel.ViewOnly,
                [Feature.Tasks] = PermissionLevel.AddEdit,
                [Feature.Clients] = PermissionLevel.ViewOnly,
                [Feature.ClientPortal] = PermissionLevel.Hidden,
                [Feature.ElectronicFiling] = PermissionLevel.Hidden,
                [Feature.FirmAdministration] = PermissionLevel.Hidden,
            }
            : new Dictionary<Feature, PermissionLevel>
            {
                [Feature.Cases] = PermissionLevel.AddEdit,
                [Feature.Intake] = PermissionLevel.AddEdit,
                [Feature.Documents] = PermissionLevel.AddEdit,
                [Feature.ExtractionReview] = PermissionLevel.AddEdit,
                [Feature.CreditorLibrary] = PermissionLevel.AddEdit,
                [Feature.Notes] = PermissionLevel.AddEdit,
                [Feature.Events] = PermissionLevel.AddEdit,
                [Feature.Tasks] = PermissionLevel.AddEdit,
                [Feature.Clients] = PermissionLevel.AddEdit,
                [Feature.ClientPortal] = PermissionLevel.Hidden,
                [Feature.ElectronicFiling] = PermissionLevel.Hidden,
                [Feature.FirmAdministration] = PermissionLevel.Hidden,
            };
        return map.ToValueDictionary();
    }

    /// <summary>
    /// This user's level for <paramref name="feature"/> — THE ONLY WAY TO READ
    /// THE MAP. A feature missing from the map is hidden; a non-active user
    /// has nothing; an active admin has <see cref="PermissionLevel.AddEdit"/>
    /// on everything.
    /// </summary>
    /// <remarks>
    /// An UNKNOWN level (a row from a newer version) also ranks as
    /// hidden here. A <see cref="PermissionLevel"/> cannot hold an unknown
    /// value, so that rule moves to the boundary that parses a stored or sent
    /// map: an unrecognised wire level never becomes a member.
    /// </remarks>
    public static PermissionLevel PermissionFor(FirmUser user, Feature feature)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Status != FirmUserStatus.Active)
        {
            return PermissionLevel.Hidden;
        }

        if (user.IsAdmin)
        {
            return PermissionLevel.AddEdit;
        }

        return user.Permissions.TryGetValue(feature, out var level) && Enum.IsDefined(level) ? level : PermissionLevel.Hidden;
    }

    /// <summary>Whether this user may do something needing <paramref name="required"/> on <paramref name="feature"/>.</summary>
    public static bool Permits(FirmUser user, Feature feature, PermissionLevel required)
    {
        if (!Enum.IsDefined(required))
        {
            throw new ValidationException($"unknown permission level: {required}");
        }

        return PermissionFor(user, feature) >= required;
    }

    /// <summary>An admin who is also active.</summary>
    public static bool IsActiveAdmin(FirmUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsAdmin && user.Status == FirmUserStatus.Active;
    }

    /// <summary>
    /// Whether this edit leaves the firm with nobody who can administer it —
    /// the one irrecoverable mistake (self-signup is off). Takes the WHOLE
    /// staff list: <paramref name="changed"/> is the post-edit record,
    /// <paramref name="removed"/> a subject that will no longer be there.
    /// </summary>
    public static bool WouldLeaveNoAdmin(IEnumerable<FirmUser> users, FirmUser? changed = null, string? removed = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        var remaining = users
            .Where(user => user.Subject != removed && (changed is null || user.Subject != changed.Subject))
            .ToList();
        if (changed is not null && changed.Subject != removed)
        {
            remaining.Add(changed);
        }

        return !remaining.Any(IsActiveAdmin);
    }
}
