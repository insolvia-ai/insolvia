using System.Text.Json;
using Insolvia.Core.Filings;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Cases;

// The electronic-filing items of the case partition (FILING#, FILING_APPROVAL#
// and the FILING_APPROVAL pointer). The filing credentials and authorizations
// (ADR 0024's vault) are 18.12's, with their own grants.

/// <summary>
/// One filing run (<c>FILING#&lt;id&gt;</c>). Every write conditions on
/// <see cref="State"/> and <see cref="AttemptId"/>, and a resolution on
/// <see cref="Resolution"/> being absent — so those are columns (the
/// resolution's null-ness is the condition; its content is a document).
/// </summary>
internal sealed class FilingRow
{
    public const string Table = "filings";
    public const string PrimaryKey = "pk_filings";

    public string CaseId { get; set; } = "";

    public string FilingId { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string ApprovalId { get; set; } = "";

    public string AttorneyId { get; set; } = "";

    public string CredentialId { get; set; } = "";

    public string Court { get; set; } = "";

    public string Division { get; set; } = "";

    public FilingState State { get; set; }

    public string AttemptId { get; set; } = "";

    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>Epoch seconds, as the lease is reasoned about.</summary>
    public long LeaseExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Every state entered, in order.</summary>
    public JsonDocument History { get; set; } = JsonDocument.Parse("[]");

    public string? Driver { get; set; }

    public JsonDocument? HandBack { get; set; }

    public string? UnknownReason { get; set; }

    public JsonDocument? Confirmation { get; set; }

    public string? ReceiptDocumentId { get; set; }

    public string[] FollowUps { get; set; } = [];

    public JsonDocument? Resolution { get; set; }
}

/// <summary>
/// One filing approval (<c>FILING_APPROVAL#&lt;id&gt;</c>; ADR 0024). Single use is
/// <c>UPDATE … WHERE status = 'pending' AND digest = … AND expires_at &gt; now</c>.
/// No store yet: the approval domain arrives with the filing routes, and
/// these columns are Python's <c>approval_item</c>, one for one.
/// </summary>
internal sealed class FilingApprovalRow
{
    public const string Table = "filing_approvals";

    public string CaseId { get; set; } = "";

    public string ApprovalId { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string FilingId { get; set; } = "";

    public string AttorneyId { get; set; } = "";

    public string CredentialId { get; set; } = "";

    public string AuthorizationId { get; set; } = "";

    /// <summary>The filing set's digest — the consume conditions on it.</summary>
    public string Digest { get; set; } = "";

    public string PacketId { get; set; } = "";

    public string Court { get; set; } = "";

    public string Division { get; set; } = "";

    public string RegistryRelease { get; set; } = "";

    public DateTimeOffset ApprovedAt { get; set; }

    /// <summary>When the approver authenticated (Python's <c>authTime</c>).</summary>
    public DateTimeOffset AuthenticatedAt { get; set; }

    /// <summary>Epoch seconds — the consume compares it to now.</summary>
    public long ExpiresAt { get; set; }

    /// <summary><c>pending</c>, <c>consumed</c> or <c>voided</c>.</summary>
    public string Status { get; set; } = "";

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public string? VoidReason { get; set; }
}

/// <summary>
/// The case's CURRENT approval (the <c>FILING_APPROVAL</c> pointer item): one
/// row per case, the row a new approval's transaction conditions on
/// (<c>INSERT</c> when there is none, <c>UPDATE … WHERE approval_id = :read</c>
/// when it replaces one). Its foreign key keeps it pointing at an approval of
/// the same case.
/// </summary>
internal sealed class CurrentFilingApprovalRow
{
    public const string Table = "current_filing_approvals";

    public string CaseId { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string ApprovalId { get; set; } = "";
}

internal sealed class FilingRowConfiguration : IEntityTypeConfiguration<FilingRow>
{
    public void Configure(EntityTypeBuilder<FilingRow> builder)
    {
        builder.ToTable(FilingRow.Table, table =>
            table.HasCheckConstraint("ck_filings_state", WireEnums.CheckIn<FilingState>("state")));
        builder.HasKey(row => new { row.CaseId, row.FilingId }).HasName(FilingRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, FilingRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.State).HasConversion<WireEnumConverter<FilingState>>();
    }
}

internal sealed class FilingApprovalRowConfiguration : IEntityTypeConfiguration<FilingApprovalRow>
{
    public void Configure(EntityTypeBuilder<FilingApprovalRow> builder)
    {
        builder.ToTable(FilingApprovalRow.Table, table =>
            table.HasCheckConstraint("ck_filing_approvals_status", "status IN ('pending', 'consumed', 'voided')"));
        builder.HasKey(row => new { row.CaseId, row.ApprovalId });
        CaseChild.BelongsToCase(builder, FilingApprovalRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class CurrentFilingApprovalRowConfiguration : IEntityTypeConfiguration<CurrentFilingApprovalRow>
{
    public void Configure(EntityTypeBuilder<CurrentFilingApprovalRow> builder)
    {
        builder.ToTable(CurrentFilingApprovalRow.Table);
        builder.HasKey(row => row.CaseId);
        CaseChild.BelongsToCase(builder, CurrentFilingApprovalRow.Table, row => new { row.FirmId, row.CaseId });
        builder.HasOne<FilingApprovalRow>().WithMany()
            .HasForeignKey(row => new { row.CaseId, row.ApprovalId })
            .HasPrincipalKey(approval => new { approval.CaseId, approval.ApprovalId })
            .HasConstraintName("fk_current_filing_approvals_approval")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
