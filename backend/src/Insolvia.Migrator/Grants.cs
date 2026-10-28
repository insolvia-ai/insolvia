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
/// </remarks>
public static class GrantTable
{
    // Least privilege per table: a role holds DELETE only where a port
    // deletes, and INSERT only where its service creates.
    private const TablePrivileges ReadInsertUpdate = TablePrivileges.Select | TablePrivileges.Insert | TablePrivileges.Update;
    private const TablePrivileges ReadUpdate = TablePrivileges.Select | TablePrivileges.Update;

    /// <summary>The grants the migrator applies.</summary>
    public static IReadOnlyList<TableGrant> All { get; } =
    [
        // ── Firm stores (18.10) ─────────────────────────────────────
        // Firms are provisioned by the admin service; the API reads its
        // tenant and edits the firm's own record (PATCH /v1/firm), and never
        // creates or deletes one. The MCP server reads who the caller is.
        new("firms", ServiceRoles.Admin, ReadInsertUpdate),
        new("firms", ServiceRoles.Api, ReadUpdate),
        new("firms", ServiceRoles.Mcp, TablePrivileges.Select),

        // Firm users: the admin service provisions a firm's first admin, the
        // API's firm administration adds, edits and detaches staff.
        new("firm_users", ServiceRoles.Admin, TablePrivileges.ReadWrite),
        new("firm_users", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("firm_users", ServiceRoles.Mcp, TablePrivileges.Select),

        // The client directory: no delete exists (archiving is a status write).
        new("firm_clients", ServiceRoles.Api, ReadInsertUpdate),
        new("firm_clients", ServiceRoles.Mcp, TablePrivileges.Select),

        // The firm's own configuration: reset is a delete.
        new("library_creditors", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("firm_questionnaires", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("firm_document_checklists", ServiceRoles.Api, TablePrivileges.ReadWrite),

        // Portal bindings: rows are rewritten, never deleted (a revoked
        // binding is the record of access); a revocation deletes its claims.
        new("client_bindings", ServiceRoles.Api, ReadInsertUpdate),
        new("case_client_bindings", ServiceRoles.Api, ReadInsertUpdate),
        new("case_client_role_claims", ServiceRoles.Api, TablePrivileges.ReadWrite),

        // The marketing waitlist (#525): the API only ever adds a submission —
        // POST /v1/waitlist is public, so its role cannot read the list back,
        // change a row or remove one.
        new("waitlist_entries", ServiceRoles.Api, TablePrivileges.Insert),

        // The access log: append-only for every service that writes it, and
        // no UPDATE, DELETE or TRUNCATE for anyone (ADR 0028 § Data).
        TableGrant.AppendOnly("access_log", ServiceRoles.Api),
        TableGrant.AppendOnly("access_log", ServiceRoles.Mcp),
        TableGrant.AppendOnly("access_log", ServiceRoles.Admin),
        TableGrant.AppendOnly("access_log", ServiceRoles.Worker),
    ];
}
