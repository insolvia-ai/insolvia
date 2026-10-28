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
using Insolvia.Core.Ports;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Exemptions;
using Insolvia.Engines.Projections;

namespace Insolvia.Api.Cases;

/// <summary>
/// The case's records read out of the stores into the engines' inputs —
/// packet_assembly.py's <c>read_case_data</c> + <c>to_case_file</c>, which the
/// means-test and plan routes call (the packet itself is 18.16's). One read
/// per collection, in the stores' own listing order, which IS printed row
/// order; <see cref="CaseFile.TaxIds"/> stays EMPTY — no route that answers
/// a caller discloses a full tax identifier.
/// </summary>
internal static class CaseFileReader
{
    /// <summary>Every collection a projection reads, as one <see cref="CaseFile"/>, plus the exemption records with their ids.</summary>
    public static async Task<StoredCaseFile> ReadAsync(Case target, IDebtorStore debtors, ICaseEntityStore entities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(entities);
        var id = target.Id;

        async Task<IReadOnlyList<TBody>> Bodies<TBody>(EntityKind<TBody> kind)
            where TBody : class =>
            [.. (await entities.ListTypedAsync(id, kind, cancellationToken).ConfigureAwait(false)).Select(entity => entity.Body)];

        async Task<IReadOnlyList<(string Id, TBody Body)>> Pairs<TBody>(EntityKind<TBody> kind)
            where TBody : class =>
            [.. (await entities.ListTypedAsync(id, kind, cancellationToken).ConfigureAwait(false)).Select(entity => (entity.Id, entity.Body))];

        var petitions = await Bodies(PetitionRules.Petition).ConfigureAwait(false);
        var exemptions = await Pairs(ExemptionRules.Kind).ConfigureAwait(false);
        var caseFile = new CaseFile(target)
        {
            Debtors = await debtors.ListForCaseAsync(id, cancellationToken).ConfigureAwait(false),
            // The petition collapses to the FIRST record (to_case_file).
            Petition = petitions.Count > 0 ? petitions[0] : null,
            PriorCases = await Bodies(PetitionRules.PriorCase).ConfigureAwait(false),
            RelatedCases = await Bodies(PetitionRules.RelatedCase).ConfigureAwait(false),
            SoleProprietorships = await Bodies(PetitionRules.SoleProprietorship).ConfigureAwait(false),
            FilingProfessionals = await Bodies(PetitionRules.FilingProfessional).ConfigureAwait(false),
            Employments = await Pairs(IncomeRules.Employment).ConfigureAwait(false),
            IncomeSummaries = await Bodies(IncomeRules.IncomeSummary).ConfigureAwait(false),
            PayPeriodRecords = await Bodies(IncomeRules.PayPeriodRecord).ConfigureAwait(false),
            OtherIncomeRecords = await Bodies(IncomeRules.OtherIncomeRecord).ConfigureAwait(false),
            MeansTestInputs = await Bodies(MeansTestInputRules.Kind).ConfigureAwait(false),
            Assets = await Pairs(AssetRules.Kind).ConfigureAwait(false),
            Exemptions = [.. exemptions.Select(pair => pair.Body)],
            Creditors = await Pairs(CreditorRules.Kind).ConfigureAwait(false),
            Claims = await Pairs(ClaimRules.Kind).ConfigureAwait(false),
            ContractLeases = await Pairs(ContractLeaseRules.Kind).ConfigureAwait(false),
            Codebtors = await Bodies(CodebtorRules.Kind).ConfigureAwait(false),
            CommunityHouseholdMembers = await Bodies(CodebtorRules.CommunityHouseholdMember).ConfigureAwait(false),
            Households = await Pairs(ExpenseRules.Household).ConfigureAwait(false),
            Expenses = await Bodies(ExpenseRules.Expense).ConfigureAwait(false),
            Dependents = await Bodies(ExpenseRules.Dependent).ConfigureAwait(false),
            SofaEntries = await Bodies(SofaRules.Kind).ConfigureAwait(false),
            Plans = await Bodies(PlanRules.Kind).ConfigureAwait(false),
        };
        return new StoredCaseFile(caseFile, exemptions);
    }

    /// <summary>
    /// The exemption workbench's input, from the collections the analysis
    /// reads and nothing else (exemption_analysis.py — not the whole file).
    /// </summary>
    public static async Task<ExemptionCase> ReadExemptionCaseAsync(Case target, IDebtorStore debtors, ICaseEntityStore entities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(entities);
        var id = target.Id;
        var petitions = await entities.ListTypedAsync(id, PetitionRules.Petition, cancellationToken).ConfigureAwait(false);
        return new ExemptionCase(target)
        {
            Debtors = await debtors.ListForCaseAsync(id, cancellationToken).ConfigureAwait(false),
            Petition = petitions.Count > 0 ? petitions[0].Body : null,
            Assets = Pairs(await entities.ListTypedAsync(id, AssetRules.Kind, cancellationToken).ConfigureAwait(false)),
            Claims = Pairs(await entities.ListTypedAsync(id, ClaimRules.Kind, cancellationToken).ConfigureAwait(false)),
            Exemptions = Pairs(await entities.ListTypedAsync(id, ExemptionRules.Kind, cancellationToken).ConfigureAwait(false)),
            SofaEntries = [.. (await entities.ListTypedAsync(id, SofaRules.Kind, cancellationToken).ConfigureAwait(false)).Select(entity => entity.Body)],
        };
    }

    private static List<(string Id, TBody Body)> Pairs<TBody>(IReadOnlyList<CaseEntity<TBody>> listed)
        where TBody : class => [.. listed.Select(entity => (entity.Id, entity.Body))];
}

/// <summary>A case file read from the stores, with the exemption records' ids the file itself drops.</summary>
/// <param name="CaseFile">The projections' input.</param>
/// <param name="Exemptions">The exemption records, with their ids.</param>
internal sealed record StoredCaseFile(CaseFile CaseFile, IReadOnlyList<(string Id, ExemptionBody Body)> Exemptions)
{
    /// <summary>
    /// The exemption workbench's input from the same read (plan.py's
    /// <c>_inputs</c>): the stored ids, not <c>exemption_case_of</c>'s positional ones.
    /// </summary>
    public ExemptionCase ExemptionCase => new(CaseFile.Case)
    {
        Debtors = CaseFile.Debtors,
        Petition = CaseFile.Petition,
        Assets = CaseFile.Assets,
        Claims = CaseFile.Claims,
        Exemptions = Exemptions,
        SofaEntries = CaseFile.SofaEntries,
    };
}
