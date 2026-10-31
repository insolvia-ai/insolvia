using System.Globalization;
using Insolvia.Core;
using Insolvia.Core.Assets;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Claims;
using Insolvia.Core.Codebtors;
using Insolvia.Core.ContractLeases;
using Insolvia.Core.Creditors;
using Insolvia.Core.Exemptions;
using Insolvia.Core.Expenses;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Core.Petitions;
using Insolvia.Core.Plans;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests.Support;

/// <summary>
/// A parity case file as <see cref="CaseData"/>: every body wrapped with the
/// case file's id, or <c>&lt;collection&gt;-&lt;n&gt;</c> where it carries
/// none — the same synthetic ids the Python parity harness's <c>case_data</c> built.
/// </summary>
internal static class CaseDatas
{
    public static readonly DateTimeOffset EntityTime = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public static CaseData Named(string name) => Of(CaseFiles.Named(name));

    public static CaseData Of(CaseFile file)
    {
        var caseId = file.Case.Id;
        List<CaseEntity<TBody>> Numbered<TBody>(EntityKind<TBody> kind, IEnumerable<TBody> bodies)
            where TBody : class =>
            [.. bodies.Select((body, n) => Entity(kind, $"{kind.Collection}-{n + 1}", caseId, body))];
        List<CaseEntity<TBody>> Identified<TBody>(EntityKind<TBody> kind, IEnumerable<(string Id, TBody Body)> pairs)
            where TBody : class =>
            [.. pairs.Select(pair => Entity(kind, pair.Id, caseId, pair.Body))];

        return new CaseData(file.Case)
        {
            Debtors = [.. file.Debtors.Select(debtor => debtor with { UpdatedAt = EntityTime })],
            TaxIds = file.TaxIds,
            Petitions = Numbered(PetitionRules.Petition, file.Petition is { } petition ? [petition] : []),
            PriorCases = Numbered(PetitionRules.PriorCase, file.PriorCases),
            RelatedCases = Numbered(PetitionRules.RelatedCase, file.RelatedCases),
            SoleProprietorships = Numbered(PetitionRules.SoleProprietorship, file.SoleProprietorships),
            FilingProfessionals = Numbered(PetitionRules.FilingProfessional, file.FilingProfessionals),
            Employments = Identified(IncomeRules.Employment, file.Employments),
            IncomeSummaries = Numbered(IncomeRules.IncomeSummary, file.IncomeSummaries),
            PayPeriodRecords = Numbered(IncomeRules.PayPeriodRecord, file.PayPeriodRecords),
            OtherIncomeRecords = Numbered(IncomeRules.OtherIncomeRecord, file.OtherIncomeRecords),
            MeansTestInputs = Numbered(MeansTestInputRules.Kind, file.MeansTestInputs),
            Assets = Identified(AssetRules.Kind, file.Assets),
            Exemptions = Numbered(ExemptionRules.Kind, file.Exemptions),
            Creditors = Identified(CreditorRules.Kind, file.Creditors),
            Claims = Identified(ClaimRules.Kind, file.Claims),
            ContractLeases = Identified(ContractLeaseRules.Kind, file.ContractLeases),
            Codebtors = Numbered(CodebtorRules.Kind, file.Codebtors),
            CommunityHouseholdMembers = Numbered(CodebtorRules.CommunityHouseholdMember, file.CommunityHouseholdMembers),
            Households = Identified(ExpenseRules.Household, file.Households),
            Expenses = Numbered(ExpenseRules.Expense, file.Expenses),
            Dependents = Numbered(ExpenseRules.Dependent, file.Dependents),
            SofaEntries = Numbered(SofaRules.Kind, file.SofaEntries),
            Plans = Numbered(PlanRules.Kind, file.Plans),
        };
    }

    /// <summary>The parity harness's <c>amended</c>: the case filed on 2026-09-15, the first asset and the first nonpriority claim flagged amended.</summary>
    public static CaseData Amended(CaseData data)
    {
        var firstNonpriority = data.Claims.FirstOrDefault(e => e.Body.ClaimClass == ClaimClass.NonpriorityUnsecured)?.Id;
        return data with
        {
            Case = data.Case with { Status = CaseStatus.Filed, FiledAt = DateOnly.Parse("2026-09-15", CultureInfo.InvariantCulture) },
            Assets = [.. data.Assets.Select((e, i) => e with { Amended = i == 0 })],
            Claims = [.. data.Claims.Select(e => e with { Amended = e.Id == firstNonpriority })],
        };
    }

    public static CaseEntity<TBody> Entity<TBody>(EntityKind<TBody> kind, string id, string caseId, TBody body, bool amended = false)
        where TBody : class =>
        new(kind, id, caseId, EntityTime, EntityTime, body, ValueCollections.EmptyDictionary<string, Core.Provenance.ProvenanceEntry>(), amended);
}
