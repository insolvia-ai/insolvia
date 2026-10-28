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

namespace Insolvia.Forms;

/// <summary>
/// The read half of the packet store that the approval basis needs: a case's
/// packets, NEWEST FIRST (Python's <c>PacketStore.list_for_case</c>). The
/// port lives here because <see cref="Packet"/> is a Forms record; the
/// packet store the job worker writes implements it (plan 18.22 wires the
/// Postgres one into the API and the filing worker).
/// </summary>
public interface IPacketReader
{
    /// <summary>The case's packets, newest first.</summary>
    Task<IReadOnlyList<Packet>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);
}

/// <summary>The in-memory <see cref="IPacketReader"/>: what a test or a local host was handed.</summary>
public sealed class MemoryPacketReader : IPacketReader
{
    private readonly Lock _gate = new();
    private readonly List<Packet> _packets = [];

    /// <summary>Record a packet (it becomes the newest).</summary>
    public void Add(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
        {
            _packets.Insert(0, packet);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Packet>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Packet>>([.. _packets.Where(packet => packet.CaseId == caseId)]);
        }
    }
}

/// <summary>Reads a <see cref="CaseData"/> from the stores (Python's <c>read_case_data</c>).</summary>
public static class CaseDataReader
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
}
