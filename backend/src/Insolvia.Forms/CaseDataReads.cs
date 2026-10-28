using Insolvia.Core.Assets;
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
using Insolvia.Core.TaxIds;

namespace Insolvia.Forms;

/// <summary>
/// The two store reads every assembler shares (from <c>packet_assembly.py</c>'s
/// <c>read_case_data</c> and <c>disclose_tax_ids</c>): the worker, the case
/// summary, the forms hub and the review all read a case file this way. Pure
/// over the ports it is handed — Forms still knows no store.
/// </summary>
public static class CaseDataReads
{
    /// <summary>One read per collection, in the stores' own listing order — which IS printed row order.</summary>
    public static async Task<CaseData> ReadAsync(Case @case, IDebtorStore debtors, ICaseEntityStore entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(entities);
        var id = @case.Id;
        return new CaseData(@case)
        {
            Debtors = await debtors.ListForCaseAsync(id, cancellationToken).ConfigureAwait(false),
            Petitions = await entities.ListTypedAsync(id, PetitionRules.Petition, cancellationToken).ConfigureAwait(false),
            PriorCases = await entities.ListTypedAsync(id, PetitionRules.PriorCase, cancellationToken).ConfigureAwait(false),
            RelatedCases = await entities.ListTypedAsync(id, PetitionRules.RelatedCase, cancellationToken).ConfigureAwait(false),
            SoleProprietorships = await entities.ListTypedAsync(id, PetitionRules.SoleProprietorship, cancellationToken).ConfigureAwait(false),
            FilingProfessionals = await entities.ListTypedAsync(id, PetitionRules.FilingProfessional, cancellationToken).ConfigureAwait(false),
            Employments = await entities.ListTypedAsync(id, IncomeRules.Employment, cancellationToken).ConfigureAwait(false),
            IncomeSummaries = await entities.ListTypedAsync(id, IncomeRules.IncomeSummary, cancellationToken).ConfigureAwait(false),
            PayPeriodRecords = await entities.ListTypedAsync(id, IncomeRules.PayPeriodRecord, cancellationToken).ConfigureAwait(false),
            OtherIncomeRecords = await entities.ListTypedAsync(id, IncomeRules.OtherIncomeRecord, cancellationToken).ConfigureAwait(false),
            MeansTestInputs = await entities.ListTypedAsync(id, MeansTestInputRules.Kind, cancellationToken).ConfigureAwait(false),
            Assets = await entities.ListTypedAsync(id, AssetRules.Kind, cancellationToken).ConfigureAwait(false),
            Exemptions = await entities.ListTypedAsync(id, ExemptionRules.Kind, cancellationToken).ConfigureAwait(false),
            Creditors = await entities.ListTypedAsync(id, CreditorRules.Kind, cancellationToken).ConfigureAwait(false),
            Claims = await entities.ListTypedAsync(id, ClaimRules.Kind, cancellationToken).ConfigureAwait(false),
            ContractLeases = await entities.ListTypedAsync(id, ContractLeaseRules.Kind, cancellationToken).ConfigureAwait(false),
            Codebtors = await entities.ListTypedAsync(id, CodebtorRules.Kind, cancellationToken).ConfigureAwait(false),
            CommunityHouseholdMembers = await entities.ListTypedAsync(id, CodebtorRules.CommunityHouseholdMember, cancellationToken).ConfigureAwait(false),
            Households = await entities.ListTypedAsync(id, ExpenseRules.Household, cancellationToken).ConfigureAwait(false),
            Expenses = await entities.ListTypedAsync(id, ExpenseRules.Expense, cancellationToken).ConfigureAwait(false),
            Dependents = await entities.ListTypedAsync(id, ExpenseRules.Dependent, cancellationToken).ConfigureAwait(false),
            SofaEntries = await entities.ListTypedAsync(id, SofaRules.Kind, cancellationToken).ConfigureAwait(false),
            Plans = await entities.ListTypedAsync(id, PlanRules.Kind, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The logged full-value read for every debtor carrying a tax id — ONE
    /// <c>taxid.read</c> row per debtor, naming <paramref name="principal"/> and
    /// <paramref name="purpose"/> — answering the data with <see cref="CaseData.TaxIds"/>
    /// filled so B121 can print. Only for a path about to print B121 or to
    /// reproduce a packet that did; never for one that answers a client.
    /// </summary>
    public static async Task<CaseData> DiscloseTaxIdsAsync(
        CaseData data,
        string principal,
        string purpose,
        ITaxIdStore taxIdStore,
        ITaxIdCipher cipher,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var disclosed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var debtor in data.Debtors)
        {
            if (debtor.TaxId is null)
            {
                continue;
            }

            var digits = await TaxIdRules.ReadAsync(debtor.TaxId, data.Case.FirmId, data.Case.Id, debtor.FilingRole, principal, purpose, cipher, taxIdStore, accessLog, cancellationToken).ConfigureAwait(false);
            if (digits is not null)
            {
                disclosed[Core.Wire.ToWire(debtor.FilingRole)] = digits;
            }
        }

        return data with { TaxIds = disclosed };
    }
}
