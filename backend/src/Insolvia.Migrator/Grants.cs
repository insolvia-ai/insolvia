namespace Insolvia.Migrator;

/// <summary>
/// The Postgres roles, one per deployed service (ADR 0028 § Data: "Database
/// authentication is IAM, per service"). Each Lambda's IAM role may
/// <c>rds-db:connect</c> as exactly one of these.
/// </summary>
public static class ServiceRoles
{
    /// <summary>The API lambdalith.</summary>
    public const string Api = "api";

    /// <summary>The job worker.</summary>
    public const string Worker = "worker";

    /// <summary>The staff admin service.</summary>
    public const string Admin = "admin";

    /// <summary>The MCP server.</summary>
    public const string Mcp = "mcp";

    /// <summary>The filing worker.</summary>
    public const string Filing = "filing";

    /// <summary>
    /// The migrator's own login, reserved for the seeder (18.20). Schema
    /// changes and role management run as the RDS master user, which alone
    /// can create roles; this role holds no table privilege until the grant
    /// table gives it one.
    /// </summary>
    public const string Migrator = "migrator";

    /// <summary>Every role the migrator creates, in a stable order.</summary>
    public static IReadOnlyList<string> All { get; } = [Api, Worker, Admin, Mcp, Filing, Migrator];
}

/// <summary>Table privileges a role can hold.</summary>
[Flags]
public enum TablePrivileges
{
    /// <summary>None: the role cannot touch the table.</summary>
    None = 0,

    /// <summary><c>SELECT</c>.</summary>
    Select = 1,

    /// <summary><c>INSERT</c>.</summary>
    Insert = 2,

    /// <summary><c>UPDATE</c>.</summary>
    Update = 4,

    /// <summary><c>DELETE</c>.</summary>
    Delete = 8,

    /// <summary><c>TRUNCATE</c>. No application role should ever need it.</summary>
    Truncate = 16,

    /// <summary>An append-only log: rows are written once and read, never changed or removed.</summary>
    AppendOnly = Select | Insert,

    /// <summary>The usual store: read and write rows, never empty the table.</summary>
    ReadWrite = Select | Insert | Update | Delete,
}

/// <summary>One role's privileges on one table in the <c>insolvia</c> schema.</summary>
/// <param name="Table">The table's name (snake_case, unqualified).</param>
/// <param name="Role">One of <see cref="ServiceRoles"/>.</param>
/// <param name="Privileges">Exactly what the role holds: anything not listed is revoked.</param>
public sealed record TableGrant(string Table, string Role, TablePrivileges Privileges)
{
    /// <summary>
    /// <c>INSERT</c> and <c>SELECT</c>, and nothing else: <c>UPDATE</c>,
    /// <c>DELETE</c> and <c>TRUNCATE</c> are revoked, so an application role
    /// cannot rewrite or erase history (the case access log, the admin
    /// audit — ADR 0028 § Data, "Append-only logs").
    /// </summary>
    public static TableGrant AppendOnly(string table, string role) => new(table, role, TablePrivileges.AppendOnly);
}

/// <summary>
/// Who may do what to which table: the declarative half of the database's
/// access control. The migrator makes the database match this list on every
/// run — a grant missing here is revoked there, and a table no entry names is
/// reachable by no application role.
/// </summary>
/// <remarks>
/// <para>
/// A store that adds a table adds its rows here in the same PR, e.g.
/// <c>new("case_note", ServiceRoles.Api, TablePrivileges.ReadWrite)</c> or
/// <c>TableGrant.AppendOnly("case_access_log", ServiceRoles.Api)</c>. Naming
/// a table that does not exist fails the migrator, so a typo cannot leave a
/// table silently ungranted.
/// </para>
/// <para>Empty until the first domain table (18.10).</para>
/// </remarks>
public static class GrantTable
{
    /// <summary>The grants the migrator applies.</summary>
    public static IReadOnlyList<TableGrant> All { get; } = [];
}
