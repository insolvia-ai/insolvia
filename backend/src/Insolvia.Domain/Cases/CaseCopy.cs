using Insolvia.Core.CaseEntities;
using Insolvia.Core.Debtors;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.Cases;

/// <summary>
/// Everything a Copy case writes: the case and its copier's assignment (one
/// transaction with the debtors), and the collection records that follow.
/// </summary>
public sealed record CaseCopy(Case Case, CaseAssignment Assignment, ValueCollection<Debtor> Debtors, ValueCollection<ICaseEntity> Entities);

/// <summary>
/// Copy a case — a new matter from an existing one's data (issue 14.3 / #355;
/// from <c>case_copy.py</c>). The copy is a NEW case carrying the source's
/// PREPARATION data, every copied value naming the case it came from in its
/// provenance. Not copied: the status and its history, the docket facts and
/// dates, the pins, the archive stamp, documents, candidates, notes, tasks,
/// events, portal bindings, and every assignment but the copier's. IDS AND
/// <c>created_at</c> ARE KEPT (records are scoped by their case, and the forms
/// print in creation order); <c>amended</c> is cleared.
/// </summary>
public static class CaseCopies
{
    /// <summary>
    /// The copy of <paramref name="source"/> that <paramref name="createdBy"/>
    /// opens, from the source's debtors and collection records as read. It
    /// opens at intake, as every case does.
    /// </summary>
    public static CaseCopy Copy(Case source, string createdBy, IEnumerable<Debtor> debtors, IEnumerable<ICaseEntity> entities, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(debtors);
        ArgumentNullException.ThrowIfNull(entities);
        var now = Clock.Now(clock);
        var copy = new Case(Guid.NewGuid().ToString(), source.FirmId, createdBy, source.Chapter, source.District, CaseStatus.Intake, now, now)
        {
            Court = source.Court,
            Division = source.Division,
            ExemptionSet = source.ExemptionSet,
        };
        var copiedDebtors = debtors.Select(debtor => debtor with
        {
            CaseId = copy.Id,
            UpdatedAt = now,
            // The by-client entry sorts by the COPY's creation — the newer matter it is.
            CaseCreatedAt = debtor.ClientId is not null ? copy.CreatedAt : null,
            Provenance = Marked(debtor.Provenance, source.Id),
        }).ToValueCollection();
        var copiedEntities = entities
            .Select(entity => entity.CopiedTo(copy.Id, now, Marked(entity.Provenance, source.Id)))
            .ToValueCollection();
        return new CaseCopy(copy, CaseRules.Assign(copy, createdBy, createdBy, clock), copiedDebtors, copiedEntities);
    }

    /// <summary>Every entry as it was, naming the case it was copied from.</summary>
    private static ValueDictionary<string, ProvenanceEntry> Marked(IReadOnlyDictionary<string, ProvenanceEntry> provenance, string sourceCaseId) =>
        provenance.Select(pair => new KeyValuePair<string, ProvenanceEntry>(pair.Key, pair.Value with { CopiedFromCaseId = sourceCaseId })).ToValueDictionary();
}
