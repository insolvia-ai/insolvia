using Insolvia.Core.Assets;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Codebtors;
using Insolvia.Core.ContractLeases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Debtors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Expenses;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Core.Petitions;
using Insolvia.Core.Plans;
using Insolvia.Core.Sofa;

namespace Insolvia.Engines.Projections;

/// <summary>
/// Everything a projection reads, in presentation order (from
/// <c>form_projections/shared.py</c>). The caller loads the case's records
/// and hands over the BODIES already sorted the way rows print; the
/// projection reaches into no store. Collections referenced by id elsewhere
/// carry (id, body) pairs. <see cref="TaxIds"/> holds the debtors' full
/// tax identifiers by filing role — produced by the logged full-value read
/// only when B121 is being rendered, empty otherwise.
/// </summary>
public sealed record CaseFile(Case Case)
{
    /// <summary>The debtor records.</summary>
    public IReadOnlyList<Debtor> Debtors { get; init; } = [];

    /// <summary>Full tax identifiers' digits by filing role (<c>debtor_1</c>), B121 only.</summary>
    public IReadOnlyDictionary<string, string> TaxIds { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The petition's answers.</summary>
    public PetitionBody? Petition { get; init; }

    /// <summary>B101 line 9.</summary>
    public IReadOnlyList<PriorCaseBody> PriorCases { get; init; } = [];

    /// <summary>B101 line 10.</summary>
    public IReadOnlyList<RelatedCaseBody> RelatedCases { get; init; } = [];

    /// <summary>B101 line 12.</summary>
    public IReadOnlyList<SoleProprietorshipBody> SoleProprietorships { get; init; } = [];

    /// <summary>B101 Part 7 / B2030.</summary>
    public IReadOnlyList<FilingProfessionalBody> FilingProfessionals { get; init; } = [];

    /// <summary>106I employments, with ids (pay periods reference them).</summary>
    public IReadOnlyList<(string Id, EmploymentBody Body)> Employments { get; init; } = [];

    /// <summary>106I's per-debtor summaries.</summary>
    public IReadOnlyList<IncomeSummaryBody> IncomeSummaries { get; init; } = [];

    /// <summary>The dated pay history (the means test).</summary>
    public IReadOnlyList<PayPeriodRecordBody> PayPeriodRecords { get; init; } = [];

    /// <summary>The dated non-wage receipts (the means test).</summary>
    public IReadOnlyList<OtherIncomeRecordBody> OtherIncomeRecords { get; init; } = [];

    /// <summary>The entered means-test figures — the first one is read.</summary>
    public IReadOnlyList<MeansTestInputBody> MeansTestInputs { get; init; } = [];

    /// <summary>106A/B's assets, with ids.</summary>
    public IReadOnlyList<(string Id, AssetBody Body)> Assets { get; init; } = [];

    /// <summary>106C's claimed exemptions.</summary>
    public IReadOnlyList<ExemptionBody> Exemptions { get; init; } = [];

    /// <summary>The creditors, with ids.</summary>
    public IReadOnlyList<(string Id, CreditorBody Body)> Creditors { get; init; } = [];

    /// <summary>The claims, with ids.</summary>
    public IReadOnlyList<(string Id, ClaimBody Body)> Claims { get; init; } = [];

    /// <summary>106G's contracts and leases, with ids.</summary>
    public IReadOnlyList<(string Id, ContractLeaseBody Body)> ContractLeases { get; init; } = [];

    /// <summary>106H's codebtors.</summary>
    public IReadOnlyList<CodebtorBody> Codebtors { get; init; } = [];

    /// <summary>106H line 2's community-property household members.</summary>
    public IReadOnlyList<CommunityHouseholdMemberBody> CommunityHouseholdMembers { get; init; } = [];

    /// <summary>106J households, with ids.</summary>
    public IReadOnlyList<(string Id, HouseholdBody Body)> Households { get; init; } = [];

    /// <summary>106J expense rows.</summary>
    public IReadOnlyList<ExpenseBody> Expenses { get; init; } = [];

    /// <summary>106J dependents.</summary>
    public IReadOnlyList<DependentBody> Dependents { get; init; } = [];

    /// <summary>B107's entries.</summary>
    public IReadOnlyList<SofaEntryBody> SofaEntries { get; init; } = [];

    /// <summary>B113's plan — the first one is read.</summary>
    public IReadOnlyList<PlanBody> Plans { get; init; } = [];

    /// <summary>The first debtor in any of <paramref name="roles"/>.</summary>
    public Debtor? Debtor(params FilingRole[] roles) => Debtors.FirstOrDefault(debtor => roles.Contains(debtor.FilingRole));

    /// <summary>The asset with this id, or null.</summary>
    public AssetBody? Asset(string? assetId) => Assets.FirstOrDefault(pair => pair.Id == assetId).Body;

    /// <summary>The creditor with this id, or null.</summary>
    public CreditorBody? Creditor(string? creditorId) => Creditors.FirstOrDefault(pair => pair.Id == creditorId).Body;

    /// <summary>The case's creation date (UTC) — the filing-date stand-in several projections resolve registries on.</summary>
    public DateOnly CreatedOn => DateOnly.FromDateTime(Case.CreatedAt.UtcDateTime);
}
