using Insolvia.Data.Vault;

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
    /// The seeder's role (18.20): a load runs with these grants alone, taken
    /// by logging in AS this role (an IAM token deployed, MigratorSettings) —
    /// never by SET ROLE on the master, which would make the master an rds_iam
    /// member and lock its password out. Schema
    /// changes, role management and the pre-client purge run as the RDS
    /// master user itself, which alone can create roles.
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
    /// A column-level <c>SELECT</c>: the role may read these columns and no
    /// other (<c>GRANT SELECT (a, b) ON …</c>). Empty for none. Cannot be
    /// combined with a table-level <see cref="TablePrivileges.Select"/>, which
    /// would make it meaningless; the migrator refuses the pair.
    /// </summary>
    /// <remarks>
    /// Columns a statement's <c>WHERE</c> names need <c>SELECT</c> too, so a
    /// role that deletes by key must be able to read the key columns.
    /// </remarks>
    public IReadOnlyList<string> SelectColumns { get; init; } = [];

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
    private const TablePrivileges SelectInsert = TablePrivileges.Select | TablePrivileges.Insert;

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

        // Firm users: the admin service provisions a firm's first admin (and
        // only ever adds and reads — no admin route edits or removes a user);
        // the API's firm administration adds, edits and detaches staff.
        new("firm_users", ServiceRoles.Admin, SelectInsert),
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

        // ── Case stores (18.11) ─────────────────────────────────────
        // Derived from which Python service composed which store over the
        // case table (services/*/entrypoints) and what it called: the API
        // does everything a route does; the pipeline WORKER reads the case
        // file it assembles or extracts from, pins the case, advances jobs,
        // writes packets and proposes candidates; the MCP server reads and
        // proposes (ADR 0016: candidates are its only write); the FILING
        // worker reads what it files, consumes the approval, writes its
        // filing record and files the case. The admin service touches no
        // case table (its seeder is the migrator role's, 18.20). No delete
        // where no port deletes: a case is soft-deleted, a debtor replaced.
        new("cases", ServiceRoles.Api, ReadInsertUpdate),
        new("cases", ServiceRoles.Worker, ReadUpdate),
        new("cases", ServiceRoles.Mcp, TablePrivileges.Select),
        new("cases", ServiceRoles.Filing, ReadUpdate),

        // Who may see a case: the API links and unlinks; the MCP server's
        // case read decides access with it.
        new("case_assignments", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("case_assignments", ServiceRoles.Mcp, TablePrivileges.Select),

        // The lifecycle history is APPEND-ONLY for its writers (ADR 0027):
        // an event is never rewritten or removed. The worker reads the
        // petition chapter for the packet gate; the MCP server reads it.
        TableGrant.AppendOnly("case_lifecycle_events", ServiceRoles.Api),
        TableGrant.AppendOnly("case_lifecycle_events", ServiceRoles.Filing),
        new("case_lifecycle_events", ServiceRoles.Worker, TablePrivileges.Select),
        new("case_lifecycle_events", ServiceRoles.Mcp, TablePrivileges.Select),

        // The case file: the API writes it; every other service reads it.
        new("debtors", ServiceRoles.Api, ReadInsertUpdate),
        new("debtors", ServiceRoles.Worker, TablePrivileges.Select),
        new("debtors", ServiceRoles.Mcp, TablePrivileges.Select),
        new("debtors", ServiceRoles.Filing, TablePrivileges.Select),
        new("case_entities", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("case_entities", ServiceRoles.Worker, TablePrivileges.Select),
        new("case_entities", ServiceRoles.Mcp, TablePrivileges.Select),
        new("case_entities", ServiceRoles.Filing, TablePrivileges.Select),

        // Documents: the API uploads, confirms and deletes; the worker
        // reads what it extracts from; the filing worker stores its receipt.
        new("documents", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("documents", ServiceRoles.Worker, TablePrivileges.Select),
        new("documents", ServiceRoles.Mcp, TablePrivileges.Select),
        new("documents", ServiceRoles.Filing, TablePrivileges.Select | TablePrivileges.Insert),

        // Candidates: proposed by extraction (worker) and MCP harnesses,
        // reviewed by staff (API), withdrawn by the harness (MCP's CAS).
        new("candidates", ServiceRoles.Api, ReadInsertUpdate),
        new("candidates", ServiceRoles.Worker, TablePrivileges.Select | TablePrivileges.Insert),
        new("candidates", ServiceRoles.Mcp, ReadInsertUpdate),

        // The API's own case work.
        new("case_tasks", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("document_requests", ServiceRoles.Api, TablePrivileges.ReadWrite),

        // Sealed tax ids: the API seals (an upsert, never a delete); the
        // worker and the filing worker open them for B121.
        new("tax_ids", ServiceRoles.Api, ReadInsertUpdate),
        new("tax_ids", ServiceRoles.Worker, TablePrivileges.Select),
        new("tax_ids", ServiceRoles.Filing, TablePrivileges.Select),

        // The pipeline: the API accepts a job, the worker advances it and
        // writes the packet the API then serves.
        new("jobs", ServiceRoles.Api, ReadInsertUpdate),
        new("jobs", ServiceRoles.Worker, ReadUpdate),
        new("packets", ServiceRoles.Api, TablePrivileges.Select),
        new("packets", ServiceRoles.Worker, TablePrivileges.Select | TablePrivileges.Insert),
        new("packets", ServiceRoles.Filing, TablePrivileges.Select),

        // Electronic filing (ADR 0024): the API approves (and voids) and
        // resolves a hand-back; the filing worker claims and advances the
        // record and consumes the approval. Nobody deletes either.
        new("filing_approvals", ServiceRoles.Api, ReadInsertUpdate),
        new("filing_approvals", ServiceRoles.Filing, ReadUpdate),
        new("current_filing_approvals", ServiceRoles.Api, ReadInsertUpdate),
        new("current_filing_approvals", ServiceRoles.Filing, TablePrivileges.Select),
        new("filings", ServiceRoles.Api, ReadUpdate),
        new("filings", ServiceRoles.Filing, ReadInsertUpdate),

        // The calendar and its feed tokens: the API's alone.
        new("calendar_events", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("calendar_tokens", ServiceRoles.Api, TablePrivileges.ReadWrite),

        // ── Sealed data (18.12; ADR 0024, ADR 0028 § Data) ──────────
        // The vault, isolated in two layers: KMS (only the filing worker may
        // Decrypt under the vault key) and these grants. The API seals and
        // stores (INSERT), revokes (DELETE) and reads the credential's
        // METADATA for the status view, the enrolment's duplicate check and
        // the approval's credential choice — a column-level SELECT that names
        // no envelope column, so the API role cannot read a ciphertext at all.
        // The filing worker reads everything and writes nothing here.
        new(FilingCredentialColumns.Table, ServiceRoles.Api, TablePrivileges.Insert | TablePrivileges.Delete)
        {
            SelectColumns = FilingCredentialColumns.Metadata,
        },
        new(FilingCredentialColumns.Table, ServiceRoles.Filing, TablePrivileges.Select),

        // Signed authorizations: the API signs (a history row, the pointer
        // inserted or swapped, the replaced row marked superseded) and
        // withdraws (the pointer deleted, the row marked withdrawn). History
        // is never deleted. The filing worker's open re-checks the current one.
        new("filing_authorizations", ServiceRoles.Api, ReadInsertUpdate),
        new("filing_authorizations", ServiceRoles.Filing, TablePrivileges.Select),
        new("current_filing_authorizations", ServiceRoles.Api, TablePrivileges.ReadWrite),
        new("current_filing_authorizations", ServiceRoles.Filing, TablePrivileges.Select),

        // The admin audit (ADR 0011): every staff mutation appends a row.
        // INSERT alone — not even SELECT: the admin service can append an
        // entry and never read, amend or delete one (the Python role's grant
        // was PutItem alone). No other role touches it.
        new("admin_audit", ServiceRoles.Admin, TablePrivileges.Insert),

        // ── The seeder (18.20) ──────────────────────────────────────
        // The migrator Lambda seeds dev and staging AS THIS ROLE (SET ROLE on
        // the master's login), so a load can do what seeds/README.md says it
        // does and nothing more: read what exists, insert what is missing,
        // never delete. UPDATE only where a store's write needs it: a pending
        // document confirmed, a tax id sealed (an upsert), a portal binding
        // written (an upsert, the claims locked FOR UPDATE), and the client
        // row a case links locked FOR SHARE — Postgres demands UPDATE for any
        // row lock. Migrations, roles and the purge stay the master's.
        new("firms", ServiceRoles.Migrator, SelectInsert),
        new("firm_users", ServiceRoles.Migrator, SelectInsert),
        new("firm_clients", ServiceRoles.Migrator, ReadInsertUpdate),
        new("cases", ServiceRoles.Migrator, SelectInsert),
        new("case_assignments", ServiceRoles.Migrator, SelectInsert),
        new("debtors", ServiceRoles.Migrator, SelectInsert),
        new("case_entities", ServiceRoles.Migrator, SelectInsert),
        new("documents", ServiceRoles.Migrator, ReadInsertUpdate),
        new("tax_ids", ServiceRoles.Migrator, ReadInsertUpdate),
        new("client_bindings", ServiceRoles.Migrator, ReadInsertUpdate),
        new("case_client_bindings", ServiceRoles.Migrator, ReadInsertUpdate),
        new("case_client_role_claims", ServiceRoles.Migrator, ReadInsertUpdate),

        // The access log: append-only for every service that writes it, and
        // no UPDATE, DELETE or TRUNCATE for anyone (ADR 0028 § Data).
        TableGrant.AppendOnly("access_log", ServiceRoles.Api),
        TableGrant.AppendOnly("access_log", ServiceRoles.Mcp),
        TableGrant.AppendOnly("access_log", ServiceRoles.Admin),
        TableGrant.AppendOnly("access_log", ServiceRoles.Worker),

        // The filing worker writes the vault's `credential.open` rows (before
        // it decrypts), the consume, the hand-back and the submit — append
        // only, like every other writer (#530).
        TableGrant.AppendOnly("access_log", ServiceRoles.Filing),
    ];
}
