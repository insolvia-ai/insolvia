using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Vault;

// The credential vault (ADR 0024; ADR 0028 § Data, "vault isolation in two
// layers"). DynamoDB kept it in its own table, one partition per attorney
// (ATTORNEY#<firm>#<attorney>) holding the credentials (CREDENTIAL#<id>), the
// current authorization (AUTHORIZATION) and the authorization history
// (AUTHORIZATION#<id>). Here: three tables, the same keys.
//
// Foreign keys go to `firms` only. Not to `firm_users`: Python's vault never
// depended on membership — detaching a user leaves their vault partition as
// it was — and a key onto `firm_users` would either refuse the detach
// (RESTRICT) or erase the signed authorization history with it (CASCADE).
// Within the vault the keys are composite, so a credential can only cite a
// signature its own attorney made, and the current pointer only one of the
// same attorney's signatures.

/// <summary>
/// The column split behind the API's column-level grant. The API role may
/// <c>SELECT</c> the metadata (the status view, the enrolment's duplicate
/// check, the approval's credential choice) and never an envelope column;
/// the filing worker reads both. A new column must be put in one list — a
/// data test fails until it is.
/// </summary>
public static class FilingCredentialColumns
{
    /// <summary>The table.</summary>
    public const string Table = "filing_credentials";

    /// <summary>What the status view is built from — plain, readable by the API.</summary>
    public static IReadOnlyList<string> Metadata { get; } =
    [
        "firm_id", "attorney_id", "credential_id", "login", "courts", "status", "scheme", "created_at", "updated_at", "authorization_ref",
    ];

    /// <summary>The sealed secret — readable by the filing worker alone.</summary>
    public static IReadOnlyList<string> Envelope { get; } = ["ciphertext", "nonce", "wrapped_key"];
}

/// <summary>
/// One sealed credential (<c>CREDENTIAL#&lt;id&gt;</c> in the attorney's
/// partition). The envelope is stored as bytes (Core's base64 strings,
/// decoded strictly), and the three facts the encryption context binds —
/// firm, attorney, credential — are columns beside it, verbatim: they are the
/// AES-GCM associated data and the KMS context, so the open rebuilds both
/// from this row.
/// </summary>
internal sealed class FilingCredentialRow
{
    public const string Table = FilingCredentialColumns.Table;
    public const string PrimaryKey = "pk_filing_credentials";

    public string FirmId { get; set; } = "";

    public string AttorneyId { get; set; } = "";

    public string CredentialId { get; set; } = "";

    public string Login { get; set; } = "";

    public string[] Courts { get; set; } = [];

    public CredentialStatus Status { get; set; }

    public string Scheme { get; set; } = "";

    public byte[] Ciphertext { get; set; } = [];

    public byte[] Nonce { get; set; } = [];

    public byte[] WrappedKey { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The signature it was enrolled under (null only for a legacy item, which is never openable).</summary>
    public string? AuthorizationRef { get; set; }
}

/// <summary>
/// One signature (<c>AUTHORIZATION#&lt;id&gt;</c>, the history). Never
/// deleted: a superseded or withdrawn signature stays on record, and its
/// status says which.
/// </summary>
internal sealed class FilingAuthorizationRow
{
    public const string Table = "filing_authorizations";
    public const string PrimaryKey = "pk_filing_authorizations";

    /// <summary>The (id, firm, attorney) key the pointer and the credentials cite.</summary>
    public const string OwnedKey = "ak_filing_authorizations_owner";

    public string AuthorizationId { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string AttorneyId { get; set; } = "";

    public string TextVersion { get; set; } = "";

    public string TextDigest { get; set; } = "";

    public DateTimeOffset SignedAt { get; set; }

    /// <summary>The sign-in's <c>auth_time</c>, epoch seconds.</summary>
    public long AuthenticatedAt { get; set; }

    public AuthorizationStatus Status { get; set; }

    public DateTimeOffset? WithdrawnAt { get; set; }
}

/// <summary>
/// The attorney's CURRENT authorization (the <c>AUTHORIZATION</c> item): one
/// row per attorney, the row a signature's write conditions on
/// (<c>INSERT … ON CONFLICT DO NOTHING</c> when there is none,
/// <c>UPDATE … WHERE authorization_id = :held</c> when it replaces one) and the
/// row a withdrawal deletes — from that delete on, nothing the attorney holds
/// is openable.
/// </summary>
internal sealed class CurrentFilingAuthorizationRow
{
    public const string Table = "current_filing_authorizations";
    public const string PrimaryKey = "pk_current_filing_authorizations";

    public string FirmId { get; set; } = "";

    public string AttorneyId { get; set; } = "";

    public string AuthorizationId { get; set; } = "";
}

internal sealed class FilingCredentialRowConfiguration : IEntityTypeConfiguration<FilingCredentialRow>
{
    public void Configure(EntityTypeBuilder<FilingCredentialRow> builder)
    {
        builder.ToTable(FilingCredentialRow.Table, table =>
            table.HasCheckConstraint("ck_filing_credentials_status", WireEnums.CheckIn<CredentialStatus>("status")));

        // Python's (PK, SK): the attorney's partition, then the credential.
        // Every read and the delete name all three, so another attorney's id
        // is simply not found.
        builder.HasKey(row => new { row.FirmId, row.AttorneyId, row.CredentialId }).HasName(FilingCredentialRow.PrimaryKey);
        builder.HasOne<FirmRow>().WithMany()
            .HasForeignKey(row => row.FirmId)
            .HasConstraintName(FirmRow.ForeignKeyFrom(FilingCredentialRow.Table))
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FilingAuthorizationRow>().WithMany()
            .HasForeignKey(row => new { row.AuthorizationRef, row.FirmId, row.AttorneyId })
            .HasPrincipalKey(signature => new { signature.AuthorizationId, signature.FirmId, signature.AttorneyId })
            .HasConstraintName("fk_filing_credentials_authorization")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<CredentialStatus>>();
    }
}

internal sealed class FilingAuthorizationRowConfiguration : IEntityTypeConfiguration<FilingAuthorizationRow>
{
    public void Configure(EntityTypeBuilder<FilingAuthorizationRow> builder)
    {
        builder.ToTable(FilingAuthorizationRow.Table, table =>
        {
            table.HasCheckConstraint("ck_filing_authorizations_status", WireEnums.CheckIn<AuthorizationStatus>("status"));
            table.HasCheckConstraint("ck_filing_authorizations_withdrawn", "(status = 'withdrawn') = (withdrawn_at IS NOT NULL)");
        });
        builder.HasKey(row => row.AuthorizationId).HasName(FilingAuthorizationRow.PrimaryKey);
        builder.HasAlternateKey(row => new { row.AuthorizationId, row.FirmId, row.AttorneyId }).HasName(FilingAuthorizationRow.OwnedKey);
        builder.HasIndex(row => new { row.FirmId, row.AttorneyId, row.SignedAt });
        builder.HasOne<FirmRow>().WithMany()
            .HasForeignKey(row => row.FirmId)
            .HasConstraintName(FirmRow.ForeignKeyFrom(FilingAuthorizationRow.Table))
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(row => row.Status).HasConversion<WireEnumConverter<AuthorizationStatus>>();
    }
}

internal sealed class CurrentFilingAuthorizationRowConfiguration : IEntityTypeConfiguration<CurrentFilingAuthorizationRow>
{
    public void Configure(EntityTypeBuilder<CurrentFilingAuthorizationRow> builder)
    {
        builder.ToTable(CurrentFilingAuthorizationRow.Table);
        builder.HasKey(row => new { row.FirmId, row.AttorneyId }).HasName(CurrentFilingAuthorizationRow.PrimaryKey);
        builder.HasOne<FirmRow>().WithMany()
            .HasForeignKey(row => row.FirmId)
            .HasConstraintName(FirmRow.ForeignKeyFrom(CurrentFilingAuthorizationRow.Table))
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FilingAuthorizationRow>().WithMany()
            .HasForeignKey(row => new { row.AuthorizationId, row.FirmId, row.AttorneyId })
            .HasPrincipalKey(signature => new { signature.AuthorizationId, signature.FirmId, signature.AttorneyId })
            .HasConstraintName("fk_current_filing_authorizations_authorization")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
