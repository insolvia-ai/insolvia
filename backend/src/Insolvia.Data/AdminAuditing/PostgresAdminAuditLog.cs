using Insolvia.Core.AdminAudit;
using Insolvia.Core.Ports;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.AdminAuditing;

/// <summary>
/// One staff mutation (Python's <c>event_item</c>; the DynamoDB table was
/// <c>PK FIRM#&lt;id&gt;</c>, <c>SK &lt;recordedAt&gt;#&lt;eventId&gt;</c>, which
/// the index on <c>(firm_id, recorded_at, event_id)</c> keeps: "everything
/// that happened to this firm", in time order).
/// </summary>
/// <remarks>
/// The foreign key onto <c>firms</c> is RESTRICT, like every firm key: no
/// port deletes a firm, and every event is written after the firm it names
/// exists (provisioning records after the firm is created). Were a firm ever
/// deleted, its audit trail would refuse the delete rather than vanish with
/// it — the opposite of the access log, which keys no subject at all because
/// a deleted case's reads must outlive the case.
/// </remarks>
internal sealed class AdminAuditRow
{
    public const string Table = "admin_audit";
    public const string PrimaryKey = "pk_admin_audit";

    public string EventId { get; set; } = "";

    public string FirmId { get; set; } = "";

    public AdminAction Action { get; set; }

    /// <summary>The staff caller's Google <c>sub</c>.</summary>
    public string Principal { get; set; } = "";

    public string PrincipalEmail { get; set; } = "";

    public string Detail { get; set; } = "";

    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class AdminAuditRowConfiguration : IEntityTypeConfiguration<AdminAuditRow>
{
    public void Configure(EntityTypeBuilder<AdminAuditRow> builder)
    {
        builder.ToTable(AdminAuditRow.Table, table =>
            table.HasCheckConstraint("ck_admin_audit_action", WireEnums.CheckIn<AdminAction>("action")));
        builder.HasKey(row => row.EventId).HasName(AdminAuditRow.PrimaryKey);
        builder.HasIndex(row => new { row.FirmId, row.RecordedAt, row.EventId });
        builder.HasOne<FirmRow>().WithMany()
            .HasForeignKey(row => row.FirmId)
            .HasConstraintName(FirmRow.ForeignKeyFrom(AdminAuditRow.Table))
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.Action).HasConversion<WireEnumConverter<AdminAction>>();
    }
}

/// <summary>
/// The Postgres <see cref="IAdminAuditLog"/> (Python's <c>DynamoDbAuditLog</c>):
/// one <c>INSERT</c>, the only statement it makes, with no <c>RETURNING</c> —
/// the admin role holds <c>INSERT</c> on this table and nothing else
/// (<c>GrantTable</c>), as its IAM grant was <c>PutItem</c> alone, so it can
/// append an entry and never read, amend or delete one.
/// </summary>
public sealed class PostgresAdminAuditLog : IAdminAuditLog
{
    private readonly InsolviaDbContext _db;

    /// <summary>A log over <paramref name="db"/> (request-scoped).</summary>
    public PostgresAdminAuditLog(InsolviaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Python's <c>PutItem</c> was unconditional (the key ends in a fresh
    /// uuid); here the event id is the primary key, so a replayed event is
    /// refused rather than overwriting the row — this table never has
    /// overwrite semantics.
    /// </remarks>
    public Task RecordAsync(AdminEvent adminEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminEvent);
        return _db.InsertOnceAsync(
            new AdminAuditRow
            {
                EventId = adminEvent.EventId,
                FirmId = adminEvent.FirmId,
                Action = adminEvent.Action,
                Principal = adminEvent.Principal,
                PrincipalEmail = adminEvent.PrincipalEmail,
                Detail = adminEvent.Detail,
                RecordedAt = adminEvent.RecordedAt,
            },
            AdminAuditRow.PrimaryKey,
            () => new InvalidOperationException($"admin event {adminEvent.EventId} was already recorded"),
            cancellationToken);
    }

    /// <summary>A stored row as the event it recorded — for tests; the service has no read path.</summary>
    internal static AdminEvent ToDomain(AdminAuditRow row) =>
        new(row.FirmId, row.Action, row.Principal, row.PrincipalEmail, row.Detail, row.RecordedAt, row.EventId);
}
