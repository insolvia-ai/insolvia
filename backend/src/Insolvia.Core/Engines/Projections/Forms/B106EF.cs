using Insolvia.Core;
using Insolvia.Core.Claims;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106E/F @ 2015-12-01 (revision 12/15) — Schedule E/F's mapping (from
/// <c>form_projections/b106ef.py</c>): priority claims onto Part 1,
/// nonpriority onto Part 2, notice parties onto Part 3, and the Part 4
/// rollup whose 6e and 6j feed 106Sum.
/// </summary>
public static class B106EF
{
    private static readonly Dictionary<PriorityType, string> PriorityTypeBoxes = new()
    {
        [PriorityType.DomesticSupport] = "priority.type_domestic_support",
        [PriorityType.TaxAndGovernment] = "priority.type_taxes",
        [PriorityType.DeathOrInjuryWhileIntoxicated] = "priority.type_intoxicated",
        [PriorityType.Other] = "priority.type_other",
    };

    private static readonly Dictionary<NonpriorityType, string> NonpriorityTypeBoxes = new()
    {
        [NonpriorityType.StudentLoan] = "nonpriority.type_student_loans",
        [NonpriorityType.SeparationOrDivorce] = "nonpriority.type_divorce",
        [NonpriorityType.PensionOrProfitSharing] = "nonpriority.type_pension",
        [NonpriorityType.Other] = "nonpriority.type_other",
    };

    /// <summary>Line 6e — every priority claim's total; feeds 106Sum line 3a.</summary>
    public static decimal PriorityUnsecuredTotal(CaseFile caseFile) =>
        ClaimsOf(caseFile, ClaimClass.PriorityUnsecured).Aggregate(0m, (sum, claim) => sum + TotalClaim(claim));

    /// <summary>Line 6j — every nonpriority claim's amount; feeds 106Sum line 3b.</summary>
    public static decimal NonpriorityUnsecuredTotal(CaseFile caseFile) =>
        ClaimsOf(caseFile, ClaimClass.NonpriorityUnsecured).Aggregate(0m, (sum, claim) => sum + Amount(claim.Amount));

    /// <summary>One Part 4 line: priority claims of one type at their total; shared with 106Sum 9a-9c.</summary>
    public static decimal PriorityTypeTotal(CaseFile caseFile, PriorityType wanted) =>
        ClaimsOf(caseFile, ClaimClass.PriorityUnsecured).Where(claim => claim.PriorityType == wanted).Aggregate(0m, (sum, claim) => sum + TotalClaim(claim));

    /// <summary>One Part 4 line: nonpriority claims of one type; shared with 106Sum 9d-9f.</summary>
    public static decimal NonpriorityTypeTotal(CaseFile caseFile, NonpriorityType wanted) =>
        ClaimsOf(caseFile, ClaimClass.NonpriorityUnsecured).Where(claim => claim.NonpriorityType == wanted).Aggregate(0m, (sum, claim) => sum + Amount(claim.Amount));

    /// <summary>The values for <c>form/b106ef@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var priority = ClaimsOf(caseFile, ClaimClass.PriorityUnsecured);
        var nonpriority = ClaimsOf(caseFile, ClaimClass.NonpriorityUnsecured);

        values["line_1_any_priority_claims"] = YesNo(release, "line_1_any_priority_claims", priority.Count > 0);
        for (var index = 0; index < priority.Count; index++)
        {
            var claim = priority[index];
            CreditorBlock(release, values, caseFile, "priority", index, claim, problems);
            CommonColumns(release, values, "priority", index, claim, problems);
            if (claim.PriorityAmount is not null || claim.NonpriorityAmount is not null)
            {
                RowFill(release, values, "priority.total_claim", index, new TextFill(FormatMoney(TotalClaim(claim))), problems);
            }

            RowFill(release, values, "priority.priority_amount", index, Money(claim.PriorityAmount), problems);
            RowFill(release, values, "priority.nonpriority_amount", index, Money(claim.NonpriorityAmount), problems);
            if (claim.PriorityType is { } type)
            {
                RowFill(release, values, PriorityTypeBoxes[type], index, new CheckFill(), problems);
            }

            RowFill(release, values, "priority.type_other_specify", index, TextOrNone(claim.PriorityTypeOther), problems);
        }

        values["line_3_any_nonpriority_claims"] = YesNo(release, "line_3_any_nonpriority_claims", nonpriority.Count > 0);
        for (var index = 0; index < nonpriority.Count; index++)
        {
            var claim = nonpriority[index];
            CreditorBlock(release, values, caseFile, "nonpriority", index, claim, problems);
            CommonColumns(release, values, "nonpriority", index, claim, problems);
            RowFill(release, values, "nonpriority.amount", index, Money(claim.Amount), problems);
            if (claim.NonpriorityType is { } type)
            {
                RowFill(release, values, NonpriorityTypeBoxes[type], index, new CheckFill(), problems);
            }

            RowFill(release, values, "nonpriority.type_other_specify", index, TextOrNone(claim.NonpriorityTypeOther), problems);
        }

        var parties = priority.Concat(nonpriority).SelectMany(claim => claim.NoticeParties.Select(party => (Claim: claim, Party: party))).ToList();
        for (var index = 0; index < parties.Count; index++)
        {
            var (claim, party) = parties[index];
            var address = party.Address ?? new Core.Fields.Address();
            RowFill(release, values, "notify.name", index, TextOrNone(party.Name), problems);
            foreach (var (fieldId, part) in new[] { ("notify.street", address.Line1), ("notify.street2", address.Line2), ("notify.city", address.City), ("notify.state", address.State), ("notify.zip", address.PostalCode) })
            {
                RowFill(release, values, fieldId, index, TextOrNone(part), problems);
            }

            RowFill(release, values, "notify.account_last4", index, TextOrNone(party.AccountLast4), problems);
            RowFill(release, values, "notify.referenced_part", index, new OptionFill(claim.ClaimClass == ClaimClass.PriorityUnsecured ? "Part 1" : "yes"), problems);
        }

        values["line_6a_total"] = new TextFill(FormatMoney(PriorityTypeTotal(caseFile, PriorityType.DomesticSupport)));
        values["line_6b_total"] = new TextFill(FormatMoney(PriorityTypeTotal(caseFile, PriorityType.TaxAndGovernment)));
        values["line_6c_total"] = new TextFill(FormatMoney(PriorityTypeTotal(caseFile, PriorityType.DeathOrInjuryWhileIntoxicated)));
        values["line_6d_total"] = new TextFill(FormatMoney(PriorityTypeTotal(caseFile, PriorityType.Other)));
        values["line_6e_total"] = new TextFill(FormatMoney(PriorityUnsecuredTotal(caseFile)));
        values["line_6f_total"] = new TextFill(FormatMoney(NonpriorityTypeTotal(caseFile, NonpriorityType.StudentLoan)));
        values["line_6g_total"] = new TextFill(FormatMoney(NonpriorityTypeTotal(caseFile, NonpriorityType.SeparationOrDivorce)));
        values["line_6h_total"] = new TextFill(FormatMoney(NonpriorityTypeTotal(caseFile, NonpriorityType.PensionOrProfitSharing)));
        values["line_6i_total"] = new TextFill(FormatMoney(NonpriorityTypeTotal(caseFile, NonpriorityType.Other)));
        values["line_6j_total"] = new TextFill(FormatMoney(NonpriorityUnsecuredTotal(caseFile)));

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static decimal TotalClaim(ClaimBody claim) => Amount(claim.PriorityAmount) + Amount(claim.NonpriorityAmount);

    private static TextFill? Money(decimal? value) => value is { } present ? new TextFill(FormatMoney(present)) : null;

    private static void CreditorBlock(FormRelease release, FieldValues values, CaseFile caseFile, string prefix, int index, ClaimBody claim, List<string> problems)
    {
        if (caseFile.Creditor(claim.CreditorId) is not { } creditor)
        {
            return;
        }

        RowFill(release, values, $"{prefix}.creditor_name", index, TextOrNone(creditor.Name), problems);
        var parts = new List<(string FieldId, string? Part)>
        {
            ($"{prefix}.creditor_street", creditor.Address.Line1),
            ($"{prefix}.creditor_city", creditor.Address.City),
            ($"{prefix}.creditor_state", creditor.Address.State),
            ($"{prefix}.creditor_zip", creditor.Address.PostalCode),
        };
        if (prefix == "priority")
        {
            parts.Insert(1, ("priority.creditor_street2", creditor.Address.Line2));
        }

        foreach (var (fieldId, part) in parts)
        {
            RowFill(release, values, fieldId, index, TextOrNone(part), problems);
        }
    }

    private static void CommonColumns(FormRelease release, FieldValues values, string prefix, int index, ClaimBody claim, List<string> problems)
    {
        RowFill(release, values, $"{prefix}.account_last4", index, TextOrNone(claim.AccountLast4), problems);
        RowFill(release, values, $"{prefix}.date_incurred", index, claim.DateIncurred is { } incurred ? new TextFill(FormatDate(incurred)) : null, problems);
        if (claim.WhoIncurred is { } who)
        {
            var fieldId = $"{prefix}.who_incurred";
            var spec = release.Field(fieldId);
            if (index < spec.PdfNames.Count)
            {
                RowFill(release, values, fieldId, index, CanonicalOption(release, fieldId, spec.PdfNames[index], who.ToWire()), problems);
            }
        }

        foreach (var (attribute, flag) in new[] { ("contingent", claim.Contingent), ("unliquidated", claim.Unliquidated), ("disputed", claim.Disputed), ("community_debt", claim.CommunityDebt) })
        {
            if (flag == true)
            {
                RowFill(release, values, $"{prefix}.{attribute}", index, new CheckFill(), problems);
            }
        }

        if (claim.SubjectToOffset is { } offset)
        {
            RowFill(release, values, $"{prefix}.subject_to_offset", index, new OptionFill(offset ? "yes" : "no"), problems);
        }
    }
}
