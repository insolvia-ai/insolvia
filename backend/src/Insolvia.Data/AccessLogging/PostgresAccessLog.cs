using Insolvia.Core.AccessLog;
using Insolvia.Core.Ports;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.AccessLogging;

/// <summary>
/// One access-log row (Python's <c>access_item</c>). ONE table for every
/// subject — case, client, credential, authorization — keyed by
/// <c>(subject_kind, subject_id)</c> the way the DynamoDB log was partitioned
/// by <c>CASE#&lt;id&gt;</c>/<c>CLIENT#&lt;id&gt;</c>. Deliberately no foreign
/// key to any subject: the log outlives what it is about (a deleted case's
/// reads stay on record, and <c>case.delete</c> is itself a row), so the case
/// tables (18.11) arrive without touching it.
/// </summary>
internal sealed class AccessLogRow
{
    public const string Table = "access_log";
    public const string PrimaryKey = "pk_access_log";

    /// <summary>The event's own id — minted by the domain, so no sequence a writer would need a grant on.</summary>
    public string EventId { get; set; } = "";

    public AccessSubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = "";

    public string Principal { get; set; } = "";

    public AccessAction Action { get; set; }

    public AccessOutcome Outcome { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string? FilingRole { get; set; }

    public string? Purpose { get; set; }

    public string? Roles { get; set; }

    public string? FilingId { get; set; }
}

internal sealed class AccessLogRowConfiguration : IEntityTypeConfiguration<AccessLogRow>
{
    public void Configure(EntityTypeBuilder<AccessLogRow> builder)
    {
        // The action has no CHECK on purpose: the vocabulary grows with
        // every audited feature, and the log must never refuse a row.
        builder.ToTable(AccessLogRow.Table, table =>
        {
            table.HasCheckConstraint("ck_access_log_subject_kind", WireEnums.CheckIn<AccessSubjectKind>("subject_kind"));
            table.HasCheckConstraint("ck_access_log_outcome", WireEnums.CheckIn<AccessOutcome>("outcome"));
        });
        builder.HasKey(row => row.EventId).HasName(AccessLogRow.PrimaryKey);

        // "Who saw this case / client", newest last; and "what did this person see".
        builder.HasIndex(row => new { row.SubjectKind, row.SubjectId, row.RecordedAt });
        builder.HasIndex(row => new { row.Principal, row.RecordedAt });
        builder.Property(row => row.SubjectKind).HasConversion<WireEnumConverter<AccessSubjectKind>>();
        builder.Property(row => row.Action).HasConversion<WireEnumConverter<AccessAction>>();
        builder.Property(row => row.Outcome).HasConversion<WireEnumConverter<AccessOutcome>>();
    }
}

/// <summary>
/// The Postgres <see cref="IAccessLog"/> (Python's <c>DynamoDbAccessLog</c>):
/// one <c>INSERT</c>, the only statement it makes. The table is append-only
/// in the grants too — every service role holds <c>INSERT</c> and
/// <c>SELECT</c> on it and nothing else (<c>GrantTable</c>), so no
/// application can rewrite or erase history.
/// </summary>
public sealed class PostgresAccessLog : IAccessLog
{
    private readonly InsolviaDbContext _db;

    /// <summary>A log over <paramref name="db"/> (request-scoped).</summary>
    public PostgresAccessLog(InsolviaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public Task RecordAsync(AccessEvent accessEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessEvent);
        return _db.InsertOnceAsync(
            new AccessLogRow
            {
                EventId = accessEvent.EventId,
                SubjectKind = accessEvent.SubjectKind,
                SubjectId = accessEvent.SubjectId,
                Principal = accessEvent.Principal,
                Action = accessEvent.Action,
                Outcome = accessEvent.Outcome,
                RecordedAt = accessEvent.RecordedAt,
                FilingRole = accessEvent.FilingRole,
                Purpose = accessEvent.Purpose,
                Roles = accessEvent.Roles,
                FilingId = accessEvent.FilingId,
            },
            AccessLogRow.PrimaryKey,
            () => new InvalidOperationException($"access event {accessEvent.EventId} was already recorded"),
            cancellationToken);
    }

    /// <summary>A stored row as the event it recorded — for tests; the service has no read path.</summary>
    internal static AccessEvent ToDomain(AccessLogRow row) =>
        new(row.SubjectKind, row.SubjectId, row.Principal, row.Action, row.Outcome, row.RecordedAt, row.EventId)
        {
            FilingRole = row.FilingRole,
            Purpose = row.Purpose,
            Roles = row.Roles,
            FilingId = row.FilingId,
        };
}
