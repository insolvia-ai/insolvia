using Insolvia.Core.Assets;
using Insolvia.Core.CaseEntities;
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
using Insolvia.Engines.Projections;

namespace Insolvia.Forms;

/// <summary>
/// Everything assembly reads (from <c>packet_assembly.CaseData</c>): the
/// entities with their ids, in the collections' own listing order. The gate
/// needs the wrappers (a problem names the record to fix); the projections
/// get the bodies through <see cref="ToCaseFile"/>. <see cref="TaxIds"/> is
/// empty as read and filled only by the logged full-value read, on the one
/// path about to print B121 — the caller's (the worker's) job.
/// </summary>
public sealed record CaseData(Case Case)
{
    /// <summary>The debtors.</summary>
    public IReadOnlyList<Debtor> Debtors { get; init; } = [];

    /// <summary>The tax ids.</summary>
    public IReadOnlyDictionary<string, string> TaxIds { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The petitions.</summary>
    public IReadOnlyList<CaseEntity<PetitionBody>> Petitions { get; init; } = [];

    /// <summary>The prior cases.</summary>
    public IReadOnlyList<CaseEntity<PriorCaseBody>> PriorCases { get; init; } = [];

    /// <summary>The related cases.</summary>
    public IReadOnlyList<CaseEntity<RelatedCaseBody>> RelatedCases { get; init; } = [];

    /// <summary>The sole proprietorships.</summary>
    public IReadOnlyList<CaseEntity<SoleProprietorshipBody>> SoleProprietorships { get; init; } = [];

    /// <summary>The filing professionals.</summary>
    public IReadOnlyList<CaseEntity<FilingProfessionalBody>> FilingProfessionals { get; init; } = [];

    /// <summary>The employments.</summary>
    public IReadOnlyList<CaseEntity<EmploymentBody>> Employments { get; init; } = [];

    /// <summary>The income summaries.</summary>
    public IReadOnlyList<CaseEntity<IncomeSummaryBody>> IncomeSummaries { get; init; } = [];

    /// <summary>The pay period records.</summary>
    public IReadOnlyList<CaseEntity<PayPeriodRecordBody>> PayPeriodRecords { get; init; } = [];

    /// <summary>The other income records.</summary>
    public IReadOnlyList<CaseEntity<OtherIncomeRecordBody>> OtherIncomeRecords { get; init; } = [];

    /// <summary>The means test inputs.</summary>
    public IReadOnlyList<CaseEntity<MeansTestInputBody>> MeansTestInputs { get; init; } = [];

    /// <summary>The assets.</summary>
    public IReadOnlyList<CaseEntity<AssetBody>> Assets { get; init; } = [];

    /// <summary>The exemptions.</summary>
    public IReadOnlyList<CaseEntity<ExemptionBody>> Exemptions { get; init; } = [];

    /// <summary>The creditors.</summary>
    public IReadOnlyList<CaseEntity<CreditorBody>> Creditors { get; init; } = [];

    /// <summary>The claims.</summary>
    public IReadOnlyList<CaseEntity<ClaimBody>> Claims { get; init; } = [];

    /// <summary>The contract leases.</summary>
    public IReadOnlyList<CaseEntity<ContractLeaseBody>> ContractLeases { get; init; } = [];

    /// <summary>The codebtors.</summary>
    public IReadOnlyList<CaseEntity<CodebtorBody>> Codebtors { get; init; } = [];

    /// <summary>The community household members.</summary>
    public IReadOnlyList<CaseEntity<CommunityHouseholdMemberBody>> CommunityHouseholdMembers { get; init; } = [];

    /// <summary>The households.</summary>
    public IReadOnlyList<CaseEntity<HouseholdBody>> Households { get; init; } = [];

    /// <summary>The expenses.</summary>
    public IReadOnlyList<CaseEntity<ExpenseBody>> Expenses { get; init; } = [];

    /// <summary>The dependents.</summary>
    public IReadOnlyList<CaseEntity<DependentBody>> Dependents { get; init; } = [];

    /// <summary>The sofa entries.</summary>
    public IReadOnlyList<CaseEntity<SofaEntryBody>> SofaEntries { get; init; } = [];

    /// <summary>The plans.</summary>
    public IReadOnlyList<CaseEntity<PlanBody>> Plans { get; init; } = [];

    /// <summary>Every collection by its CaseData member name (Python's dataclass field order).</summary>
    public IReadOnlyList<(string Member, IReadOnlyList<ICaseEntity> Entities)> Collections() =>
    [
        ("petitions", [.. Petitions]),
        ("prior_cases", [.. PriorCases]),
        ("related_cases", [.. RelatedCases]),
        ("sole_proprietorships", [.. SoleProprietorships]),
        ("filing_professionals", [.. FilingProfessionals]),
        ("employments", [.. Employments]),
        ("income_summaries", [.. IncomeSummaries]),
        ("pay_period_records", [.. PayPeriodRecords]),
        ("other_income_records", [.. OtherIncomeRecords]),
        ("means_test_inputs", [.. MeansTestInputs]),
        ("assets", [.. Assets]),
        ("exemptions", [.. Exemptions]),
        ("creditors", [.. Creditors]),
        ("claims", [.. Claims]),
        ("contract_leases", [.. ContractLeases]),
        ("codebtors", [.. Codebtors]),
        ("community_household_members", [.. CommunityHouseholdMembers]),
        ("households", [.. Households]),
        ("expenses", [.. Expenses]),
        ("dependents", [.. Dependents]),
        ("sofa_entries", [.. SofaEntries]),
        ("plans", [.. Plans]),
    ];

    /// <summary>
    /// The projections' input (Python's <c>to_case_file</c>): bodies in
    /// listing order, id-paired where others reference them. The petition
    /// collapses to the first record — the gate refuses a second.
    /// </summary>
    public CaseFile ToCaseFile() => new(Case)
    {
        Debtors = Debtors,
        TaxIds = TaxIds,
        Petition = Petitions.Count > 0 ? Petitions[0].Body : null,
        PriorCases = [.. PriorCases.Select(e => e.Body)],
        RelatedCases = [.. RelatedCases.Select(e => e.Body)],
        SoleProprietorships = [.. SoleProprietorships.Select(e => e.Body)],
        FilingProfessionals = [.. FilingProfessionals.Select(e => e.Body)],
        Employments = [.. Employments.Select(e => (e.Id, e.Body))],
        IncomeSummaries = [.. IncomeSummaries.Select(e => e.Body)],
        PayPeriodRecords = [.. PayPeriodRecords.Select(e => e.Body)],
        OtherIncomeRecords = [.. OtherIncomeRecords.Select(e => e.Body)],
        MeansTestInputs = [.. MeansTestInputs.Select(e => e.Body)],
        Assets = [.. Assets.Select(e => (e.Id, e.Body))],
        Exemptions = [.. Exemptions.Select(e => e.Body)],
        Creditors = [.. Creditors.Select(e => (e.Id, e.Body))],
        Claims = [.. Claims.Select(e => (e.Id, e.Body))],
        ContractLeases = [.. ContractLeases.Select(e => (e.Id, e.Body))],
        Codebtors = [.. Codebtors.Select(e => e.Body)],
        CommunityHouseholdMembers = [.. CommunityHouseholdMembers.Select(e => e.Body)],
        Households = [.. Households.Select(e => (e.Id, e.Body))],
        Expenses = [.. Expenses.Select(e => e.Body)],
        Dependents = [.. Dependents.Select(e => e.Body)],
        SofaEntries = [.. SofaEntries.Select(e => e.Body)],
        Plans = [.. Plans.Select(e => e.Body)],
    };
}
