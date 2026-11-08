using System.Collections.Frozen;
using Insolvia.Domain.Assets;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Codebtors;
using Insolvia.Domain.ContractLeases;
using Insolvia.Domain.Creditors;
using Insolvia.Domain.Exemptions;
using Insolvia.Domain.Expenses;
using Insolvia.Domain.Income;
using Insolvia.Domain.MeansTest;
using Insolvia.Domain.Petitions;
using Insolvia.Domain.Plans;
using Insolvia.Domain.Sofa;

namespace Insolvia.Domain.CaseEntities;

/// <summary>
/// The registry of case collections — every <see cref="IEntityKind"/>,
/// addressable by its URL segment (from <c>case_collections.py</c>). One place
/// on purpose: routes, stores and tests iterate THIS map, so a kind cannot
/// exist half-wired. <c>debtor</c> and <c>document</c> are deliberately absent
/// (keyed by filing role / by the upload flow), and so is <c>note</c>.
/// </summary>
public static class CaseCollections
{
    /// <summary>Every collection, in Python's declaration order.</summary>
    public static readonly IReadOnlyList<IEntityKind> Kinds =
    [
        CreditorRules.Kind,
        ClaimRules.Kind,
        AssetRules.Kind,
        IncomeRules.Employment,
        IncomeRules.PayPeriodRecord,
        IncomeRules.OtherIncomeRecord,
        IncomeRules.IncomeSummary,
        ExpenseRules.Household,
        ExpenseRules.Expense,
        ExpenseRules.Dependent,
        CodebtorRules.Kind,
        SofaRules.Kind,
        PetitionRules.Petition,
        PetitionRules.PriorCase,
        PetitionRules.RelatedCase,
        PetitionRules.SoleProprietorship,
        PetitionRules.FilingProfessional,
        ExemptionRules.Kind,
        ContractLeaseRules.Kind,
        CodebtorRules.CommunityHouseholdMember,
        MeansTestInputRules.Kind,
        PlanRules.Kind,
    ];

    /// <summary>Every collection by its URL segment (Python's <c>COLLECTIONS</c>).</summary>
    public static readonly FrozenDictionary<string, IEntityKind> ByCollection =
        Kinds.ToFrozenDictionary(kind => kind.Collection, StringComparer.Ordinal);

    /// <summary>
    /// Every record namespace in a case besides the collections' own — the
    /// non-generic ones. A new prefix that collides with (or is a prefix of)
    /// an existing one would hand one collection's records to another's query.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedNamespaces =
    [
        "META",
        "ASSIGNEE",
        "DEBTOR",
        "DOCUMENT",
        "JOB",
        "PACKET",
        "CANDIDATE",
        "NOTE",
        "EVENT",
        "TASK",
        "STATUS",
    ];

    /// <summary>The collection named <paramref name="collection"/>, or null.</summary>
    public static IEntityKind? Find(string collection) =>
        ByCollection.TryGetValue(collection, out var kind) ? kind : null;
}
