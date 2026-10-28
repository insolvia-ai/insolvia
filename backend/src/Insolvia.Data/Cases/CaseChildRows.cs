using System.Text.Json;
using Insolvia.Core.Candidates;
using Insolvia.Core.Debtors;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.TaxIds;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Cases;

// The case's child items (DEBTOR#, <KIND>#, DOCUMENT#, CANDIDATE#, TASK#,
// DOCREQUEST#, TAXID#), one table each, keyed (case_id, …). A column is real
// when something keys, orders or conditions on it; an entity's validated
// body and its provenance map are jsonb documents, read back through the
// domain's own parser (provenance not enforced on a read).

/// <summary>A debtor (<c>DEBTOR#&lt;role&gt;</c>) — one per filing role per case.</summary>
internal sealed class DebtorRow
{
    public const string Table = "debtors";
    public const string PrimaryKey = "pk_debtors";

    /// <summary>One client, one role per case — the link's rule, structural.</summary>
    public const string OneRolePerClient = "ux_debtors_case_id_client_id";

    public string CaseId { get; set; } = "";

    public FilingRole FilingRole { get; set; }

    public string FirmId { get; set; } = "";

    public string Id { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary><c>BodyJson.Prune(DebtorRules.BodyNode(body))</c>.</summary>
    public JsonDocument Body { get; set; } = JsonDocument.Parse("{}");

    /// <summary><c>ProvenanceRules.ToJson(provenance)</c>.</summary>
    public JsonDocument Provenance { get; set; } = JsonDocument.Parse("{}");

    public TaxIdKind? TaxIdKind { get; set; }

    public string? TaxIdLastFour { get; set; }

    public string? TaxIdRef { get; set; }

    /// <summary>The firm client it was copied from — the by-client index.</summary>
    public string? ClientId { get; set; }

    /// <summary>The case's creation, with <see cref="ClientId"/>: the by-client order.</summary>
    public DateTimeOffset? CaseCreatedAt { get; set; }
}

/// <summary>A record of one generic collection (<c>&lt;PREFIX&gt;#&lt;id&gt;</c>) — creditors, assets, notes, ….</summary>
internal sealed class CaseEntityRow
{
    public const string Table = "case_entities";
    public const string PrimaryKey = "pk_case_entities";

    public string CaseId { get; set; } = "";

    /// <summary>The kind's sort-key prefix (<c>CREDITOR</c>, <c>NOTE</c>, …).</summary>
    public string Kind { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public JsonDocument Body { get; set; } = JsonDocument.Parse("{}");

    public JsonDocument Provenance { get; set; } = JsonDocument.Parse("{}");

    public bool Amended { get; set; }
}

/// <summary>A document's metadata (<c>DOCUMENT#</c>) — never the bytes.</summary>
internal sealed class DocumentRow
{
    public const string Table = "documents";
    public const string PrimaryKey = "pk_documents";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public DocumentKind Kind { get; set; }

    public string FileName { get; set; } = "";

    public DocumentContentType ContentType { get; set; }

    public long ByteSize { get; set; }

    public string StorageRef { get; set; } = "";

    public string UploadedBy { get; set; } = "";

    /// <summary>The listing order, newest first.</summary>
    public DateTimeOffset UploadedAt { get; set; }

    public DocumentStatus Status { get; set; }

    public string? Etag { get; set; }

    public DocumentChannel Channel { get; set; }

    public string? RequestId { get; set; }
}

/// <summary>A candidate (<c>CANDIDATE#</c>): a proposal awaiting review.</summary>
internal sealed class CandidateRow
{
    public const string Table = "candidates";
    public const string PrimaryKey = "pk_candidates";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string EntityType { get; set; } = "";

    /// <summary>The proposed record — a document, shape-validated at proposal.</summary>
    public JsonDocument Payload { get; set; } = JsonDocument.Parse("{}");

    /// <summary>The review's compare-and-swap conditions on it.</summary>
    public CandidateStatus Status { get; set; }

    public OriginChannel OriginChannel { get; set; }

    public string OriginClientId { get; set; } = "";

    public string OriginSubject { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? DocumentId { get; set; }

    public JsonDocument? ExternalRef { get; set; }

    public string? Note { get; set; }

    /// <summary>A model's score, not money: a double is the honest type.</summary>
    public double? Confidence { get; set; }

    public JsonDocument? Locator { get; set; }

    public string? ConfirmedBy { get; set; }

    /// <summary>Kept as the text the domain carries (Python's ISO string).</summary>
    public string? ConfirmedAt { get; set; }

    public JsonDocument? CorrectedPayload { get; set; }

    public string? ResultingRecordId { get; set; }
}

/// <summary>A case task (<c>TASK#</c>).</summary>
internal sealed class CaseTaskRow
{
    public const string Table = "case_tasks";
    public const string PrimaryKey = "pk_case_tasks";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string? Description { get; set; }

    public DateOnly? DueDate { get; set; }

    public string? AssigneeSubject { get; set; }

    public string? FormSeries { get; set; }

    public bool Done { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string CreatedBy { get; set; } = "";
}

/// <summary>A document request (<c>DOCREQUEST#</c>).</summary>
internal sealed class DocumentRequestRow
{
    public const string Table = "document_requests";
    public const string PrimaryKey = "pk_document_requests";

    public string CaseId { get; set; } = "";

    public string Id { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string Title { get; set; } = "";

    public DocumentKind Kind { get; set; }

    public string? Description { get; set; }

    public DocumentRequestStatus Status { get; set; }

    /// <summary>The documents answering it, in order — a list of ids, never a document.</summary>
    public string[] DocumentIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }
}

/// <summary>
/// A sealed tax id (<c>TAXID#&lt;ref&gt;</c>), keyed (case, ref) — the case is
/// where it lives today, the ref what identifies it. The envelope's three
/// byte strings are opaque <c>bytea</c>: Postgres never sees a plaintext, and
/// the cipher that opens them is 18.12's.
/// </summary>
internal sealed class TaxIdRow
{
    public const string Table = "tax_ids";

    public string CaseId { get; set; } = "";

    public string Ref { get; set; } = "";

    public string FirmId { get; set; } = "";

    public TaxIdKind Kind { get; set; }

    public string LastFour { get; set; } = "";

    public string Scheme { get; set; } = "";

    public byte[] Ciphertext { get; set; } = [];

    public byte[] Nonce { get; set; } = [];

    public byte[] WrappedKey { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DebtorRowConfiguration : IEntityTypeConfiguration<DebtorRow>
{
    public void Configure(EntityTypeBuilder<DebtorRow> builder)
    {
        builder.ToTable(DebtorRow.Table, table =>
        {
            table.HasCheckConstraint("ck_debtors_filing_role", WireEnums.CheckIn<FilingRole>("filing_role"));
            table.HasCheckConstraint("ck_debtors_tax_id_kind", "tax_id_kind IS NULL OR " + WireEnums.CheckIn<TaxIdKind>("tax_id_kind"));
            table.HasCheckConstraint("ck_debtors_client", "(client_id IS NULL) = (case_created_at IS NULL)");
        });
        builder.HasKey(row => new { row.CaseId, row.FilingRole }).HasName(DebtorRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, DebtorRow.Table, row => new { row.FirmId, row.CaseId });

        // The by-client index, and its rule: a client answers for at most one
        // role of a case.
        builder.HasIndex(row => row.ClientId);
        builder.HasIndex(row => new { row.CaseId, row.ClientId }).IsUnique().HasFilter("client_id IS NOT NULL").HasDatabaseName(DebtorRow.OneRolePerClient);

        // The link names a client of the case's OWN firm, which exists.
        builder.HasOne<FirmClientRow>().WithMany()
            .HasForeignKey(row => new { row.FirmId, row.ClientId })
            .HasPrincipalKey(client => new { client.FirmId, client.Id })
            .HasConstraintName("fk_debtors_client")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.FilingRole).HasConversion<WireEnumConverter<FilingRole>>();
        builder.Property(row => row.TaxIdKind).HasConversion<WireEnumConverter<TaxIdKind>>();
    }
}

internal sealed class CaseEntityRowConfiguration : IEntityTypeConfiguration<CaseEntityRow>
{
    public void Configure(EntityTypeBuilder<CaseEntityRow> builder)
    {
        builder.ToTable(CaseEntityRow.Table);
        builder.HasKey(row => new { row.CaseId, row.Kind, row.Id }).HasName(CaseEntityRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, CaseEntityRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class DocumentRowConfiguration : IEntityTypeConfiguration<DocumentRow>
{
    public void Configure(EntityTypeBuilder<DocumentRow> builder)
    {
        builder.ToTable(DocumentRow.Table, table =>
        {
            table.HasCheckConstraint("ck_documents_kind", WireEnums.CheckIn<DocumentKind>("kind"));
            table.HasCheckConstraint("ck_documents_content_type", WireEnums.CheckIn<DocumentContentType>("content_type"));
            table.HasCheckConstraint("ck_documents_status", WireEnums.CheckIn<DocumentStatus>("status"));
            table.HasCheckConstraint("ck_documents_channel", WireEnums.CheckIn<DocumentChannel>("channel"));
        });
        builder.HasKey(row => new { row.CaseId, row.Id }).HasName(DocumentRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, DocumentRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.Kind).HasConversion<WireEnumConverter<DocumentKind>>();
        builder.Property(row => row.ContentType).HasConversion<WireEnumConverter<DocumentContentType>>();
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<DocumentStatus>>();
        builder.Property(row => row.Channel).HasConversion<WireEnumConverter<DocumentChannel>>();
    }
}

internal sealed class CandidateRowConfiguration : IEntityTypeConfiguration<CandidateRow>
{
    public void Configure(EntityTypeBuilder<CandidateRow> builder)
    {
        builder.ToTable(CandidateRow.Table, table =>
        {
            table.HasCheckConstraint("ck_candidates_status", WireEnums.CheckIn<CandidateStatus>("status"));
            table.HasCheckConstraint("ck_candidates_origin_channel", WireEnums.CheckIn<OriginChannel>("origin_channel"));
        });
        builder.HasKey(row => new { row.CaseId, row.Id }).HasName(CandidateRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, CandidateRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<CandidateStatus>>();
        builder.Property(row => row.OriginChannel).HasConversion<WireEnumConverter<OriginChannel>>();
    }
}

internal sealed class CaseTaskRowConfiguration : IEntityTypeConfiguration<CaseTaskRow>
{
    public void Configure(EntityTypeBuilder<CaseTaskRow> builder)
    {
        builder.ToTable(CaseTaskRow.Table);
        builder.HasKey(row => new { row.CaseId, row.Id }).HasName(CaseTaskRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, CaseTaskRow.Table, row => new { row.FirmId, row.CaseId });
    }
}

internal sealed class DocumentRequestRowConfiguration : IEntityTypeConfiguration<DocumentRequestRow>
{
    public void Configure(EntityTypeBuilder<DocumentRequestRow> builder)
    {
        builder.ToTable(DocumentRequestRow.Table, table =>
        {
            table.HasCheckConstraint("ck_document_requests_kind", WireEnums.CheckIn<DocumentKind>("kind"));
            table.HasCheckConstraint("ck_document_requests_status", WireEnums.CheckIn<DocumentRequestStatus>("status"));
        });
        builder.HasKey(row => new { row.CaseId, row.Id }).HasName(DocumentRequestRow.PrimaryKey);
        CaseChild.BelongsToCase(builder, DocumentRequestRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.Kind).HasConversion<WireEnumConverter<DocumentKind>>();
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<DocumentRequestStatus>>();
    }
}

internal sealed class TaxIdRowConfiguration : IEntityTypeConfiguration<TaxIdRow>
{
    public void Configure(EntityTypeBuilder<TaxIdRow> builder)
    {
        builder.ToTable(TaxIdRow.Table, table =>
            table.HasCheckConstraint("ck_tax_ids_kind", WireEnums.CheckIn<TaxIdKind>("kind")));
        builder.HasKey(row => new { row.CaseId, row.Ref });
        CaseChild.BelongsToCase(builder, TaxIdRow.Table, row => new { row.FirmId, row.CaseId });
        builder.Property(row => row.Kind).HasConversion<WireEnumConverter<TaxIdKind>>();
    }
}
