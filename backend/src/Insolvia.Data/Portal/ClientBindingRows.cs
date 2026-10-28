using Insolvia.Core.Clients;
using Insolvia.Data.Firms;
using Insolvia.Data.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Insolvia.Data.Portal;

// The three records a portal binding is (ADR 0023), formerly spread over the
// firm table (CLIENT#<subject>) and the case table (the CLIENT#<subject>
// mirror and the CLIENTROLE#<role> claims). In one database they are three
// tables and one transaction. The two case-keyed tables need no case table
// to exist; they carry firm_id (indexed with case_id) so the case stores
// (18.11) can add the composite foreign key (firm_id, case_id) → cases, ON
// DELETE CASCADE, in a migration of their own without rewriting this one.

/// <summary>
/// The authoritative binding, keyed (firm, subject): which case a portal
/// subject may reach. Rewritten in place on a re-bind (a refiled case).
/// </summary>
internal sealed class ClientBindingRow
{
    public const string Table = "client_bindings";

    public string FirmId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string CaseId { get; set; } = "";

    /// <summary>Lower-cased: the refiled-case lookup matches on it.</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>The roles in canonical order, as wire strings — a set of at most two, never a document.</summary>
    public string[] Roles { get; set; } = [];

    public BindingStatus Status { get; set; }

    public string InvitedBy { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The case-side mirror, keyed (case, subject): every binding a case ever had,
/// revoked included — a re-bind to another case leaves this case's row behind
/// as the record that the person once had access.
/// </summary>
internal sealed class CaseClientBindingRow
{
    public const string Table = "case_client_bindings";

    public string CaseId { get; set; } = "";

    public string Subject { get; set; } = "";

    public string FirmId { get; set; } = "";

    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string[] Roles { get; set; } = [];

    public BindingStatus Status { get; set; }

    public string InvitedBy { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Who answers for one filing role on one case — the claim that makes "at
/// most one live binding per role per case" structural: the primary key is
/// (case, role), so a second holder is a key the bind's upsert refuses.
/// </summary>
internal sealed class CaseClientRoleClaimRow
{
    public const string Table = "case_client_role_claims";

    public string CaseId { get; set; } = "";

    public ClientRole Role { get; set; }

    public string FirmId { get; set; } = "";

    public string Subject { get; set; } = "";
}

internal sealed class ClientBindingRowConfiguration : IEntityTypeConfiguration<ClientBindingRow>
{
    public void Configure(EntityTypeBuilder<ClientBindingRow> builder)
    {
        builder.ToTable(ClientBindingRow.Table, table =>
            table.HasCheckConstraint("ck_client_bindings_status", WireEnums.CheckIn<BindingStatus>("status")));
        builder.HasKey(binding => new { binding.FirmId, binding.Subject });

        // The by-subject GSI under a CLIENT# key: the portal token's one
        // unscoped read. Not unique, for FindUserAsync's reason.
        builder.HasIndex(binding => binding.Subject);
        builder.HasIndex(binding => new { binding.FirmId, binding.Email });
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(binding => binding.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(ClientBindingRow.Table)).OnDelete(DeleteBehavior.Restrict);
        builder.Property(binding => binding.Status).HasConversion<WireEnumConverter<BindingStatus>>();
    }
}

internal sealed class CaseClientBindingRowConfiguration : IEntityTypeConfiguration<CaseClientBindingRow>
{
    public void Configure(EntityTypeBuilder<CaseClientBindingRow> builder)
    {
        builder.ToTable(CaseClientBindingRow.Table, table =>
            table.HasCheckConstraint("ck_case_client_bindings_status", WireEnums.CheckIn<BindingStatus>("status")));
        builder.HasKey(binding => new { binding.CaseId, binding.Subject });
        builder.HasIndex(binding => new { binding.FirmId, binding.CaseId });
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(binding => binding.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(CaseClientBindingRow.Table)).OnDelete(DeleteBehavior.Restrict);
        builder.Property(binding => binding.Status).HasConversion<WireEnumConverter<BindingStatus>>();
    }
}

internal sealed class CaseClientRoleClaimRowConfiguration : IEntityTypeConfiguration<CaseClientRoleClaimRow>
{
    public void Configure(EntityTypeBuilder<CaseClientRoleClaimRow> builder)
    {
        builder.ToTable(CaseClientRoleClaimRow.Table, table =>
            table.HasCheckConstraint("ck_case_client_role_claims_role", WireEnums.CheckIn<ClientRole>("role")));
        builder.HasKey(claim => new { claim.CaseId, claim.Role });
        builder.HasIndex(claim => new { claim.FirmId, claim.CaseId });
        builder.HasOne<FirmRow>().WithMany().HasForeignKey(claim => claim.FirmId).HasConstraintName(FirmRow.ForeignKeyFrom(CaseClientRoleClaimRow.Table)).OnDelete(DeleteBehavior.Restrict);
        builder.Property(claim => claim.Role).HasConversion<WireEnumConverter<ClientRole>>();
    }
}
