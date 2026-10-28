using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Fields;
using Insolvia.Engines.Liens;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106D @ 2015-12-01 (revision 12/15) — Schedule D's mapping (from
/// <c>form_projections/b106d.py</c>): secured claims onto the five printed
/// rows, the collateral columns from the lien arithmetic, Column A's
/// subtotals summed here. Rows 2.4 and 2.5 carry the PDF's broken
/// who-owes groups.
/// </summary>
public static class B106D
{
    private static readonly DebtorAttribution[] WhoPrintOrder = [DebtorAttribution.Debtor1, DebtorAttribution.Debtor2, DebtorAttribution.Both, DebtorAttribution.AtLeastOnePlusAnother];

    private static readonly Dictionary<DebtorAttribution, string> WhoRow5Boxes = new()
    {
        [DebtorAttribution.Debtor1] = "Debtor 1 only_5",
        [DebtorAttribution.Debtor2] = "Debtor 2 only_5",
        [DebtorAttribution.Both] = "Debtor 1 and Debtor 2 only_5",
        [DebtorAttribution.AtLeastOnePlusAnother] = "At least one of the debtors and another_5",
    };

    private static readonly Dictionary<LienNature, string> LienBoxes = new()
    {
        [LienNature.Agreement] = "claim.lien_agreement",
        [LienNature.Statutory] = "claim.lien_statutory",
        [LienNature.Judgment] = "claim.lien_judgment",
        [LienNature.Other] = "claim.lien_other",
    };

    // Rows 2.1-2.2 print on page one, 2.3-2.5 on page two.
    private static readonly int[][] PageRows = [[0, 1], [2, 3, 4]];

    /// <summary>Column A's total — copied to B106Sum line 2.</summary>
    public static decimal SecuredTotal(CaseFile caseFile) => ClaimsOf(caseFile, ClaimClass.Secured).Aggregate(0m, (sum, claim) => sum + Amount(claim.Amount));

    /// <summary>The values for <c>form/b106d@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var secured = ClaimsOf(caseFile, ClaimClass.Secured);
        values["line_1_any_secured_claims"] = YesNo(release, "line_1_any_secured_claims", secured.Count > 0);
        var liens = LienEngine.Derive(caseFile).Claims;

        for (var index = 0; index < secured.Count; index++)
        {
            var claim = secured[index];
            var lien = liens[index];
            if (caseFile.Creditor(claim.CreditorId) is { } creditor)
            {
                RowFill(release, values, "claim.creditor_name", index, TextOrNone(creditor.Name), problems);
                foreach (var (fieldId, part) in AddressParts(creditor.Address, "claim.creditor"))
                {
                    RowFill(release, values, fieldId, index, TextOrNone(part), problems);
                }
            }

            RowFill(release, values, "claim.collateral_description", index, TextOrNone(lien.CollateralDescription), problems);
            RowFill(release, values, "claim.amount", index, claim.Amount is { } amount ? new TextFill(FormatMoney(amount)) : null, problems);
            if (lien.CollateralValue is { } collateral)
            {
                RowFill(release, values, "claim.collateral_value", index, new TextFill(FormatMoney(collateral)), problems);
            }

            if (lien.UnsecuredAmount is { } unsecured)
            {
                RowFill(release, values, "claim.unsecured_portion", index, new TextFill(FormatMoney(unsecured)), problems);
            }

            foreach (var (flag, fieldId) in new[] { (claim.Contingent, "claim.contingent"), (claim.Unliquidated, "claim.unliquidated"), (claim.Disputed, "claim.disputed"), (claim.CommunityDebt, "claim.community_debt") })
            {
                if (flag == true)
                {
                    RowFill(release, values, fieldId, index, new CheckFill(), problems);
                }
            }

            foreach (var member in claim.LienNature)
            {
                RowFill(release, values, LienBoxes[member], index, new CheckFill(), problems);
            }

            RowFill(release, values, "claim.lien_other_specify", index, TextOrNone(claim.LienNatureOther), problems);
            if (claim.WhoIncurred is { } who && index < release.Field("claim.creditor_name").PdfNames.Count)
            {
                WhoOwes(release, values, index, who, problems);
            }

            RowFill(release, values, "claim.date_incurred", index, claim.DateIncurred is { } incurred ? new TextFill(FormatDate(incurred)) : null, problems);
            RowFill(release, values, "claim.account_last4", index, TextOrNone(claim.AccountLast4), problems);
        }

        if (secured.Count > 0)
        {
            var subtotalNames = release.Field("part1_page_subtotal").PdfNames;
            for (var page = 0; page < PageRows.Length; page++)
            {
                var onPage = PageRows[page].Where(row => row < secured.Count).Select(row => secured[row]).ToList();
                if (onPage.Count > 0)
                {
                    values.Rows("part1_page_subtotal")[subtotalNames[page]] = new TextFill(FormatMoney(onPage.Aggregate(0m, (sum, c) => sum + Amount(c.Amount))));
                }
            }

            values["part1_total"] = new TextFill(FormatMoney(SecuredTotal(caseFile)));
        }

        NoticeRows(release, values, secured, problems);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static (string FieldId, string? Part)[] AddressParts(Address address, string prefix) =>
    [
        ($"{prefix}_street", address.Line1),
        ($"{prefix}_street2", address.Line2),
        ($"{prefix}_city", address.City),
        ($"{prefix}_state", address.State),
        ($"{prefix}_zip", address.PostalCode),
    ];

    private static void WhoOwes(FormRelease release, FieldValues values, int index, DebtorAttribution who, List<string> problems)
    {
        if (index < 3)
        {
            var spec = release.Field("claim.who_owes");
            RowFill(release, values, "claim.who_owes", index, CanonicalOption(release, "claim.who_owes", spec.PdfNames[index], who.ToWire()), problems);
        }
        else if (index == 3)
        {
            // Row 2.4's broken group: all four options export `On`, so the
            // selection is the widget's position in the printed order.
            values["claim.who_owes_row_2_4"] = WidgetStatesFill.ByIndexes(Array.IndexOf(WhoPrintOrder, who));
        }
        else
        {
            values.Rows("claim.who_owes_row_2_5")[WhoRow5Boxes[who]] = new CheckFill();
        }
    }

    private static void NoticeRows(FormRelease release, FieldValues values, List<ClaimBody> secured, List<string> problems)
    {
        var parties = secured.SelectMany(claim => claim.NoticeParties.Select(party => (Claim: claim, Party: party))).ToList();
        var accountNames = release.Field("notify.account_last4").PdfNames;
        for (var index = 0; index < parties.Count; index++)
        {
            var party = parties[index].Party;
            var address = party.Address ?? new Address();
            RowFill(release, values, "notify.name", index, TextOrNone(party.Name), problems);
            foreach (var (fieldId, part) in AddressParts(address, "notify").Select(pair => (pair.FieldId.Replace("notify_", "notify.", StringComparison.Ordinal), pair.Part)))
            {
                RowFill(release, values, fieldId, index, TextOrNone(part), problems);
            }

            if (party.AccountLast4 is not { } last4)
            {
                continue;
            }

            if (index == 3)
            {
                // The fourth row's account box is the PDF's second widget of
                // claim row 2.1's account field — it can only mirror it.
                var first = secured.Count > 0 ? secured[0].AccountLast4 : null;
                if (last4 != first)
                {
                    problems.Add($"notify.account_last4: the fourth notice row's box is the PDF's second widget of claim row 2.1's account field and can only repeat its value; {Py.Repr(last4)} differs");
                }

                continue;
            }

            var position = index < 3 ? index : index - 1;
            if (position >= accountNames.Count)
            {
                problems.Add($"notify.account_last4: row {index + 1} does not exist — the form prints 6 rows");
                continue;
            }

            values.Rows("notify.account_last4")[accountNames[position]] = new TextFill(last4);
        }
    }
}
