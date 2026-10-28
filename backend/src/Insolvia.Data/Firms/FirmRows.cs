using System.Text.Json;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Firms;

// The firm partition of the DynamoDB firm table (META, USER#, LIBCREDITOR#,
// FIRMCLIENT#, QUESTIONNAIRE, DOCUMENT_CHECKLIST), one table per item kind.
// A column is real when something keys, orders or conditions on it; a jsonb
// document holds a nested shape the domain treats as a whole and validates
// in code. Every tenant row carries firm_id, and its foreign key to firms is
// RESTRICT: no port deletes a firm, so a delete that reached one would be a
// mistake to refuse, not a cascade to follow.

/// <summary>A firm — the tenant (<c>META</c>).</summary>
internal sealed class FirmRow
{
    public const string Table = "firms";
    public const string PrimaryKey = "pk_firms";

    /// <summary>The name of <paramref name="table"/>'s foreign key onto this table.</summary>
    public static string ForeignKeyFrom(string table) => $"fk_{table}_firm";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public FirmStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? CreatedByEmail { get; set; }

    /// <summary>The default court's district code; set with <see cref="DefaultDivision"/> or not at all.</summary>
    public string? DefaultCourt { get; set; }

    public string? DefaultDivision { get; set; }

    public int? DefaultChapter { get; set; }

    /// <summary>The letterhead — a document: printed whole, never queried.</summary>
    public JsonDocument? Letterhead { get; set; }
}

/// <summary>One person at one firm (<c>USER#</c>), keyed by (firm, Cognito subject).</summary>
internal sealed class FirmUserRow
{
    public const string Table = "firm_users";
    public const string PrimaryKey = "pk_firm_users";

    public string FirmId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string Email { get; set; } = "";

    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";

    public FirmRole Role { get; set; }

    public bool IsAdmin { get; set; }

    public bool AccessAllCases { get; set; }

    /// <summary>
    /// The feature → level map. A document because a newer version may add
    /// features this one must read past (dropped on read, never over-granted).
    /// </summary>
    public JsonDocument Permissions { get; set; } = JsonDocument.Parse("{}");

    public FirmUserStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public JsonDocument? SignatureBlock { get; set; }
}

/// <summary>A firm's reusable creditor (<c>LIBCREDITOR#</c>).</summary>
internal sealed class LibraryCreditorRow
{
    public const string Table = "library_creditors";
    public const string PrimaryKey = "pk_library_creditors";

    public string FirmId { get; set; } = "";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public JsonDocument Address { get; set; } = JsonDocument.Parse("{}");

    public JsonDocument AdditionalNoticeParties { get; set; } = JsonDocument.Parse("[]");

    public bool Preferred { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One person in a firm's client directory (<c>FIRMCLIENT#</c>). The editable
/// fields are one <see cref="Body"/> document (Python's <c>body</c>); every
/// server-owned attribute a write conditions on — status, the retained date,
/// the merge's terminal and in-flight pointers, the funnel stage — is a column.
/// </summary>
internal sealed class FirmClientRow
{
    public const string Table = "firm_clients";
    public const string PrimaryKey = "pk_firm_clients";

    public string FirmId { get; set; } = "";

    public string Id { get; set; } = "";

    public FirmClientStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    /// <summary>Name, other names, birth date, addresses and contact fields (<see cref="FirmClientBody"/>).</summary>
    public JsonDocument Body { get; set; } = JsonDocument.Parse("{}");

    /// <summary>Unset means a prospect — the funnel's conditions read it.</summary>
    public DateOnly? FirstRetainedAt { get; set; }

    public string? TaxIdRef { get; set; }

    public string? TaxIdLastFour { get; set; }

    public string? MergedInto { get; set; }

    public string? MergingInto { get; set; }

    public string? MergingFrom { get; set; }

    public ProspectStage? ProspectStage { get; set; }
}

/// <summary>A firm's questionnaire config (<c>QUESTIONNAIRE</c>): one row per firm, the sections a document.</summary>
internal sealed class FirmQuestionnaireRow
{
    public const string Table = "firm_questionnaires";

    public string FirmId { get; set; } = "";

    public JsonDocument Sections { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = "";
}

/// <summary>A firm's document checklist (<c>DOCUMENT_CHECKLIST</c>): one row per firm, the items a document.</summary>
internal sealed class FirmDocumentChecklistRow
{
    public const string Table = "firm_document_checklists";

    public string FirmId { get; set; } = "";

    public JsonDocument Items { get; set; } = JsonDocument.Parse("[]");

    public DateTimeOffset UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = "";
}

internal sealed class FirmRowConfiguration : IEntityTypeConfiguration<FirmRow>
{
    public void Configure(EntityTypeBuilder<FirmRow> builder)
    {
        builder.ToTable(FirmRow.Table, table =>
        {
            table.HasCheckConstraint("ck_firms_status", WireEnums.CheckIn<FirmStatus>("status"));
            table.HasCheckConstraint("ck_firms_default_court", "(default_court IS NULL) = (default_division IS NULL)");
        });
        builder.HasKey(firm => firm.Id).HasName(FirmRow.PrimaryKey);
        builder.Property(firm => firm.Status).HasConversion<WireEnumConverter<FirmStatus>>();
    }
}

internal sealed class FirmUserRowConfiguration : IEntityTypeConfiguration<FirmUserRow>
{
    public void Configure(EntityTypeBuilder<FirmUserRow> builder)
    {
        builder.ToTable(FirmUserRow.Table, table =>
        {
            table.HasCheckConstraint("ck_firm_users_role", WireEnums.CheckIn<FirmRole>("role"));
            table.HasCheckConstraint("ck_firm_users_status", WireEnums.CheckIn<FirmUserStatus>("status"));
        });
        builder.HasKey(user => new { user.FirmId, user.Subject }).HasName(FirmUserRow.PrimaryKey);

        // The by-subject GSI: the hottest read in the API. NOT unique — one
        // person, one firm is an application invariant the read enforces by
        // refusing to choose, exactly as the GSI could not enforce it either.
        builder.HasIndex(user => user.Subject);
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(user => user.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(FirmUserRow.Table)).OnDelete(DeleteBehavior.Restrict);
        builder.Property(user => user.Role).HasConversion<WireEnumConverter<FirmRole>>();
        builder.Property(user => user.Status).HasConversion<WireEnumConverter<FirmUserStatus>>();
    }
}

internal sealed class LibraryCreditorRowConfiguration : IEntityTypeConfiguration<LibraryCreditorRow>
{
    public void Configure(EntityTypeBuilder<LibraryCreditorRow> builder)
    {
        builder.ToTable(LibraryCreditorRow.Table);
        builder.HasKey(creditor => new { creditor.FirmId, creditor.Id }).HasName(LibraryCreditorRow.PrimaryKey);
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(creditor => creditor.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(LibraryCreditorRow.Table)).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FirmClientRowConfiguration : IEntityTypeConfiguration<FirmClientRow>
{
    public void Configure(EntityTypeBuilder<FirmClientRow> builder)
    {
        builder.ToTable(FirmClientRow.Table, table =>
        {
            table.HasCheckConstraint("ck_firm_clients_status", WireEnums.CheckIn<FirmClientStatus>("status"));
            table.HasCheckConstraint("ck_firm_clients_prospect_stage", "prospect_stage IS NULL OR " + WireEnums.CheckIn<ProspectStage>("prospect_stage"));
        });
        builder.HasKey(client => new { client.FirmId, client.Id }).HasName(FirmClientRow.PrimaryKey);
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(client => client.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(FirmClientRow.Table)).OnDelete(DeleteBehavior.Restrict);

        // The merge's pointers are composite foreign keys onto the same
        // table: a merge cannot name a client in another firm, or one that
        // does not exist.
        builder.HasOne<FirmClientRow>().WithMany()
            .HasForeignKey(client => new { client.FirmId, client.MergedInto })
            .HasPrincipalKey(client => new { client.FirmId, client.Id })
            .HasConstraintName("fk_firm_clients_merged_into")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FirmClientRow>().WithMany()
            .HasForeignKey(client => new { client.FirmId, client.MergingInto })
            .HasPrincipalKey(client => new { client.FirmId, client.Id })
            .HasConstraintName("fk_firm_clients_merging_into")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FirmClientRow>().WithMany()
            .HasForeignKey(client => new { client.FirmId, client.MergingFrom })
            .HasPrincipalKey(client => new { client.FirmId, client.Id })
            .HasConstraintName("fk_firm_clients_merging_from")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(client => client.Status).HasConversion<WireEnumConverter<FirmClientStatus>>();
        builder.Property(client => client.ProspectStage).HasConversion<WireEnumConverter<ProspectStage>>();
    }
}

internal sealed class FirmQuestionnaireRowConfiguration : IEntityTypeConfiguration<FirmQuestionnaireRow>
{
    public void Configure(EntityTypeBuilder<FirmQuestionnaireRow> builder)
    {
        builder.ToTable(FirmQuestionnaireRow.Table);
        builder.HasKey(config => config.FirmId);
        builder.HasOne<FirmRow>().WithOne().HasForeignKey<FirmQuestionnaireRow>(config => config.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(FirmQuestionnaireRow.Table)).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FirmDocumentChecklistRowConfiguration : IEntityTypeConfiguration<FirmDocumentChecklistRow>
{
    public void Configure(EntityTypeBuilder<FirmDocumentChecklistRow> builder)
    {
        builder.ToTable(FirmDocumentChecklistRow.Table);
        builder.HasKey(checklist => checklist.FirmId);
        builder.HasOne<FirmRow>().WithOne().HasForeignKey<FirmDocumentChecklistRow>(checklist => checklist.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(FirmDocumentChecklistRow.Table)).OnDelete(DeleteBehavior.Restrict);
    }
}
