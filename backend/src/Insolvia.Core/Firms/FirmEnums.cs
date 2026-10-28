using System.Text.Json.Serialization;

namespace Insolvia.Core.Firms;

/// <summary>
/// A job title, and NOT a level of access: it drives the DEFAULT permission
/// map a new user gets and nothing else. What someone can reach is
/// <c>IsAdmin</c>, <c>AccessAllCases</c> and the permission map.
/// </summary>
public enum FirmRole
{
    /// <summary>An attorney.</summary>
    [JsonStringEnumMemberName("attorney")]
    Attorney,

    /// <summary>A paralegal — same defaults as an attorney, on purpose.</summary>
    [JsonStringEnumMemberName("paralegal")]
    Paralegal,

    /// <summary>Staff: documents and tasks, the case record read-only.</summary>
    [JsonStringEnumMemberName("staff")]
    Staff,
}

/// <summary>A firm's status. Only the admin service writes it (<see cref="FirmLifecycle.SetFirmStatus"/>).</summary>
public enum FirmStatus
{
    /// <summary>Serving requests.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Suspended by Insolvia staff.</summary>
    [JsonStringEnumMemberName("suspended")]
    Suspended,
}

/// <summary>A firm user's status. A disabled user has no permissions at all.</summary>
public enum FirmUserStatus
{
    /// <summary>Signed in and working.</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>Kept on the books, reaches nothing.</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

/// <summary>
/// What a permission can be set to, ordered weakest to strongest. THE ORDER IS
/// LOAD-BEARING — <see cref="Permissions.Permits"/> compares the numeric
/// values, so inserting a level in the middle changes every existing check.
/// </summary>
public enum PermissionLevel
{
    /// <summary>Cannot see the feature.</summary>
    [JsonStringEnumMemberName("hidden")]
    Hidden = 0,

    /// <summary>May read.</summary>
    [JsonStringEnumMemberName("view_only")]
    ViewOnly = 1,

    /// <summary>May read and write.</summary>
    [JsonStringEnumMemberName("add_edit")]
    AddEdit = 2,
}

/// <summary>
/// The features a permission map ranks — ours, not MyCase's. A feature
/// missing from a user's map is <see cref="PermissionLevel.Hidden"/>: a new
/// feature is invisible until someone grants it.
/// </summary>
public enum Feature
{
    /// <summary>The case record.</summary>
    [JsonStringEnumMemberName("cases")]
    Cases,

    /// <summary>Schedule intake.</summary>
    [JsonStringEnumMemberName("intake")]
    Intake,

    /// <summary>Uploaded documents.</summary>
    [JsonStringEnumMemberName("documents")]
    Documents,

    /// <summary>Reviewing extracted values.</summary>
    [JsonStringEnumMemberName("extraction_review")]
    ExtractionReview,

    /// <summary>The firm's reusable creditor library (issue 13.9).</summary>
    [JsonStringEnumMemberName("creditor_library")]
    CreditorLibrary,

    /// <summary>Free-text notes (issue 14.5).</summary>
    [JsonStringEnumMemberName("notes")]
    Notes,

    /// <summary>Events and deadlines (issue 14.6).</summary>
    [JsonStringEnumMemberName("events")]
    Events,

    /// <summary>Case tasks (issue 14.4).</summary>
    [JsonStringEnumMemberName("tasks")]
    Tasks,

    /// <summary>The firm's client directory (ADR 0022).</summary>
    [JsonStringEnumMemberName("clients")]
    Clients,

    /// <summary>The client portal (ADR 0023) — hidden for every role by default.</summary>
    [JsonStringEnumMemberName("client_portal")]
    ClientPortal,

    /// <summary>Electronic filing (ADR 0024) — hidden for every role by default.</summary>
    [JsonStringEnumMemberName("electronic_filing")]
    ElectronicFiling,

    /// <summary>Managing the firm — never a role default; that is what <c>IsAdmin</c> is.</summary>
    [JsonStringEnumMemberName("firm_administration")]
    FirmAdministration,
}
