using System.Text.Json;
using Insolvia.Core.Cases;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Cases;

// The case partition of the DynamoDB case table (META, ASSIGNEE#, STATUS# —
// now LIFECYCLE#), one table per item kind. A case belongs to a firm, and
// every child table carries (firm_id, case_id) with a COMPOSITE foreign key
// onto cases(firm_id, id): a child row cannot point at another firm's case.
// The children cascade: no port deletes a case (deletion is a soft stamp), so
// a hard delete is the purge CLI's, and a purged case leaves nothing behind.

/// <summary>A case (<c>META</c>).</summary>
internal sealed class CaseRow
{
    public const string Table = "cases";
    public const string PrimaryKey = "pk_cases";

    /// <summary>The (firm_id, id) key every child's composite foreign key names.</summary>
    public const string FirmKey = "ak_cases_firm_id_id";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string CreatedBy { get; set; } = "";

    /// <summary>The CURRENT chapter; a lifecycle write conditions on it.</summary>
    public int Chapter { get; set; }

    public string District { get; set; } = "";

    public CaseStatus Status { get; set; }

    /// <summary>The listing order (with <see cref="Id"/>).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? Court { get; set; }

    public string? Division { get; set; }

    /// <summary>The packet's pins, series → revision — a document, read whole.</summary>
    public JsonDocument? FormRevisions { get; set; }

    public string? ConstantsSetId { get; set; }

    public ExemptionSet? ExemptionSet { get; set; }

    public DateOnly? FiledAt { get; set; }

    public DateOnly? Meeting341At { get; set; }

    public string? CaseNumber { get; set; }

    /// <summary>The notice matcher's key (<see cref="CaseNumbers.Key"/>), derived on every write.</summary>
    public string? CaseNumberKey { get; set; }

    public string? Judge { get; set; }

    public string? Trustee { get; set; }

    public string? OfficeFileNumber { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? ArchivedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    /// <summary>
    /// The <c>recorded_at</c> of the case's latest lifecycle event, null
    /// before the first: the history's half of the version a lifecycle write
    /// is conditioned on, held on the row the write locks so the condition is
    /// one <c>WHERE</c>. Written only with an event; never by a plain update.
    /// </summary>
    public DateTimeOffset? LifecycleRecordedAt { get; set; }
}

/// <summary>One person linked to one case (<c>ASSIGNEE#</c>) — the by-assignee index.</summary>
internal sealed class CaseAssignmentRow
{
    public const string Table = "case_assignments";

    public string CaseId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string FirmId { get; set; } = "";

    public DateTimeOffset CaseCreatedAt { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public string AssignedBy { get; set; } = "";
}

/// <summary>
/// One lifecycle event (ADR 0027; <c>LIFECYCLE#&lt;recordedAt&gt;</c>). The key
/// is (case, recorded_at): two events of one case at one instant is a key the
/// insert refuses — the same-instant rule, structural. Append-only: no role
/// may update or delete a row (the grant table).
/// </summary>
internal sealed class LifecycleEventRow
{
    public const string Table = "case_lifecycle_events";
    public const string PrimaryKey = "pk_case_lifecycle_events";

    public string CaseId { get; set; } = "";

    public DateTimeOffset RecordedAt { get; set; }

    public string FirmId { get; set; } = "";

    public LifecycleEventKind Kind { get; set; }

    /// <summary>The court's date — never converted to UTC.</summary>
    public DateOnly? EffectiveOn { get; set; }

    public string RecordedBy { get; set; } = "";

    public LifecycleProvenance Provenance { get; set; }

    public string? CandidateId { get; set; }

    public CaseStatus? FromStatus { get; set; }

    public CaseStatus? ToStatus { get; set; }

    public int? Chapter { get; set; }

    public string? FilingId { get; set; }

    public int? FromChapter { get; set; }

    public int? ToChapter { get; set; }

    public string? PlanVersionId { get; set; }
}

internal sealed class CaseRowConfiguration : IEntityTypeConfiguration<CaseRow>
{
    public void Configure(EntityTypeBuilder<CaseRow> builder)
    {
        builder.ToTable(CaseRow.Table, table =>
        {
            table.HasCheckConstraint("ck_cases_status", WireEnums.CheckIn<CaseStatus>("status"));
            table.HasCheckConstraint("ck_cases_exemption_set", "exemption_set IS NULL OR " + WireEnums.CheckIn<ExemptionSet>("exemption_set"));
            table.HasCheckConstraint("ck_cases_court", "(court IS NULL) = (division IS NULL)");
            table.HasCheckConstraint("ck_cases_archived", "(archived_at IS NULL) = (archived_by IS NULL)");
            table.HasCheckConstraint("ck_cases_deleted", "(deleted_at IS NULL) = (deleted_by IS NULL)");
        });
        builder.HasKey(row => row.Id).HasName(CaseRow.PrimaryKey);
        builder.HasAlternateKey(row => new { row.FirmId, row.Id }).HasName(CaseRow.FirmKey);

        // The by-firm listing, newest first; the notice matcher's lookup.
        builder.HasIndex(row => new { row.FirmId, row.CreatedAt, row.Id }).IsDescending(false, true, true);
        builder.HasIndex(row => row.CaseNumberKey);
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(row => row.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(CaseRow.Table)).OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<CaseStatus>>();
        builder.Property(row => row.ExemptionSet).HasConversion<WireEnumConverter<ExemptionSet>>();
    }
}

internal sealed class CaseAssignmentRowConfiguration : IEntityTypeConfiguration<CaseAssignmentRow>
{
    public void Configure(EntityTypeBuilder<CaseAssignmentRow> builder)
    {
        builder.ToTable(CaseAssignmentRow.Table);
        builder.HasKey(row => new { row.CaseId, row.Subject });

        // The by-assignee listing: one person's cases, newest first.
        builder.HasIndex(row => new { row.Subject, row.CaseCreatedAt });
        CaseChild.BelongsToCase(builder, CaseAssignmentRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class LifecycleEventRowConfiguration : IEntityTypeConfiguration<LifecycleEventRow>
{
    public void Configure(EntityTypeBuilder<LifecycleEventRow> builder)
    {
        // The kind decides which members an event carries — the writer's
        // shape, restated as constraints so a row no writer made is refused.
        builder.ToTable(LifecycleEventRow.Table, table =>
        {
            table.HasCheckConstraint("ck_case_lifecycle_events_kind", WireEnums.CheckIn<LifecycleEventKind>("kind"));
            table.HasCheckConstraint("ck_case_lifecycle_events_provenance", WireEnums.CheckIn<LifecycleProvenance>("provenance"));
            table.HasCheckConstraint(
                "ck_case_lifecycle_events_status",
                "(kind = 'status') = (from_status IS NOT NULL AND to_status IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_case_lifecycle_events_statuses",
                "(from_status IS NULL OR " + WireEnums.CheckIn<CaseStatus>("from_status") + ") AND (to_status IS NULL OR " + WireEnums.CheckIn<CaseStatus>("to_status") + ")");
            table.HasCheckConstraint(
                "ck_case_lifecycle_events_conversion",
                "(kind = 'conversion') = (from_chapter IS NOT NULL AND to_chapter IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_case_lifecycle_events_court_date",
                "kind = 'status' OR effective_on IS NOT NULL");
            table.HasCheckConstraint(
                "ck_case_lifecycle_events_candidate",
                "(provenance = 'court_notice') = (candidate_id IS NOT NULL)");
        });
        builder.HasKey(row => new { row.CaseId, row.RecordedAt }).HasName(LifecycleEventRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, LifecycleEventRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.Kind).HasConversion<WireEnumConverter<LifecycleEventKind>>();
        builder.Property(row => row.Provenance).HasConversion<WireEnumConverter<LifecycleProvenance>>();
        builder.Property(row => row.FromStatus).HasConversion<WireEnumConverter<CaseStatus>>();
        builder.Property(row => row.ToStatus).HasConversion<WireEnumConverter<CaseStatus>>();
    }
}

/// <summary>What every child of a case shares: the composite foreign key onto the case, cascading.</summary>
internal static class CaseChild
{
    /// <summary>The name of <paramref name="table"/>'s foreign key onto <c>cases</c>.</summary>
    public static string ForeignKeyFrom(string table) => $"fk_{table}_case";

    /// <summary>
    /// <c>FOREIGN KEY (firm_id, case_id) REFERENCES cases (firm_id, id) ON DELETE
    /// CASCADE</c>, plus the (firm_id, case_id) index the cascade and the
    /// firm-scoped reads use.
    /// </summary>
    public static void BelongsToCase<TRow>(EntityTypeBuilder<TRow> builder, string table, System.Linq.Expressions.Expression<Func<TRow, object?>> firmAndCase)
        where TRow : class
    {
        builder.HasIndex(firmAndCase);
        builder.HasOne<CaseRow>().WithMany()
            .HasForeignKey(firmAndCase)
            .HasPrincipalKey(row => new { row.FirmId, row.Id })
            .HasConstraintName(ForeignKeyFrom(table))
            .OnDelete(DeleteBehavior.Cascade);
    }
}
