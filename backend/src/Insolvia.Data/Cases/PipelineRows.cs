using System.Text.Json;
using Insolvia.Data.Firms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Cases;

// The tables of the API-side items in the case partition — the job pipeline
// (JOB#, PACKET#) and the calendar (EVENT#, and CALTOKEN# under FIRM#). Their
// domain lives in services/api today and has no port in Core yet, so these
// are tables without a store: the columns are the Python items', and each
// store arrives with its routes (18.13–18.15), conditions in its WHERE as
// noted on each row.

/// <summary>
/// A pipeline job (<c>JOB#</c>). The worker's transitions are a compare-and-swap
/// on <see cref="Status"/> (<c>UPDATE … WHERE status = :read</c>).
/// </summary>
internal sealed class JobRow
{
    public const string Table = "jobs";

    public const string PrimaryKey = "pk_jobs";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string Kind { get; set; } = "";

    /// <summary><c>queued</c>, <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "";

    public string CreatedBy { get; set; } = "";

    public int Attempts { get; set; }

    /// <summary>The list order (with <see cref="Id"/>).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? DocumentId { get; set; }

    /// <summary>The output options a packet job was accepted with — validated, canonical.</summary>
    public JsonDocument? Options { get; set; }

    /// <summary><c>{category, message}</c>.</summary>
    public JsonDocument? Failure { get; set; }

    public JsonDocument? Result { get; set; }
}

/// <summary>
/// An assembled packet's metadata (<c>PACKET#</c>) — the bytes are in the
/// bucket. Written by the worker in ONE transaction with the pinned case,
/// conditioned on the case's <c>updated_at</c> as read and (unless an
/// amendment) on it not being filed.
/// </summary>
internal sealed class PacketRow
{
    public const string Table = "packets";

    public const string PrimaryKey = "pk_packets";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string JobId { get; set; } = "";

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long ByteSize { get; set; }

    public string Sha256 { get; set; } = "";

    public string StorageRef { get; set; } = "";

    public JsonDocument FormRevisions { get; set; } = JsonDocument.Parse("{}");

    public string ConstantsSetId { get; set; } = "";

    public int CreditorCount { get; set; }

    public string CreatedBy { get; set; } = "";

    /// <summary>The list order, newest first (with <see cref="Id"/>).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public JsonDocument? Options { get; set; }

    public JsonDocument Parts { get; set; } = JsonDocument.Parse("[]");
}

/// <summary>
/// A calendar event (<c>EVENT#</c>), a case's (<see cref="CaseId"/> set) or the
/// firm's own (null). The firm's calendar is one range query on
/// (<see cref="FirmId"/>, <see cref="SortAt"/>). An all-day event's days are
/// court-style dates; a timed one's are instants — each pair is set only for
/// its kind.
/// </summary>
internal sealed class CalendarEventRow
{
    public const string Table = "calendar_events";

    public string FirmId { get; set; } = "";

    public string Id { get; set; } = "";

    /// <summary>Null for a firm event; its composite foreign key is skipped then (MATCH SIMPLE).</summary>
    public string? CaseId { get; set; }

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    public bool AllDay { get; set; }

    public DateOnly? StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Python's <c>sort_instant</c>: the start, or an all-day event's first midnight UTC. Derived on write.</summary>
    public DateTimeOffset SortAt { get; set; }

    public string? Location { get; set; }

    public string[] Attendees { get; set; } = [];

    /// <summary>The deadline rule that generated it, if one did.</summary>
    public string? RuleId { get; set; }

    public string? RuleCitation { get; set; }

    public bool Dismissed { get; set; }

    public string CreatedBy { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A person's calendar-feed token (<c>FIRM#/CALTOKEN#</c>): one per person, replaced on rotation. Only the hash.</summary>
internal sealed class CalendarTokenRow
{
    public const string Table = "calendar_tokens";

    public string FirmId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string SecretHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class JobRowConfiguration : IEntityTypeConfiguration<JobRow>
{
    public void Configure(EntityTypeBuilder<JobRow> builder)
    {
        builder.ToTable(JobRow.Table, table =>
            table.HasCheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'failed')"));
        builder.HasKey(row => new { row.CaseId, row.Id });
        CaseChild.BelongsToCase(builder, JobRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class PacketRowConfiguration : IEntityTypeConfiguration<PacketRow>
{
    public void Configure(EntityTypeBuilder<PacketRow> builder)
    {
        builder.ToTable(PacketRow.Table);
        builder.HasKey(row => new { row.CaseId, row.Id });
        CaseChild.BelongsToCase(builder, PacketRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class CalendarEventRowConfiguration : IEntityTypeConfiguration<CalendarEventRow>
{
    public void Configure(EntityTypeBuilder<CalendarEventRow> builder)
    {
        builder.ToTable(CalendarEventRow.Table, table => table.HasCheckConstraint(
            "ck_calendar_events_days",
            "(all_day AND starts_on IS NOT NULL AND ends_on IS NOT NULL AND starts_at IS NULL AND ends_at IS NULL)"
            + " OR (NOT all_day AND starts_at IS NOT NULL AND ends_at IS NOT NULL AND starts_on IS NULL AND ends_on IS NULL)"));
        builder.HasKey(row => new { row.FirmId, row.Id });
        builder.HasIndex(row => new { row.FirmId, row.SortAt });
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(row => row.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(CalendarEventRow.Table)).OnDelete(DeleteBehavior.Restrict);
        CaseChild.BelongsToCase(builder, CalendarEventRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class CalendarTokenRowConfiguration : IEntityTypeConfiguration<CalendarTokenRow>
{
    public void Configure(EntityTypeBuilder<CalendarTokenRow> builder)
    {
        builder.ToTable(CalendarTokenRow.Table);
        builder.HasKey(row => new { row.FirmId, row.Subject });
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(row => row.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(CalendarTokenRow.Table)).OnDelete(DeleteBehavior.Restrict);
    }
}
