using Insolvia.Core.Claims;
using Insolvia.Core.ContractLeases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Plans;
using Insolvia.Engines.Liens;
using Insolvia.Engines.Plans;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B113 @ 2017-12-01 — the Chapter 13 plan (from <c>form_projections/b113.py</c>),
/// a flat release and the one projection that runs a calculator: the plan
/// calculator's figures, the treated claims' rows, and the exhibit totals.
/// A plan the form cannot print honestly is a problem.
/// </summary>
public static class B113
{
    private const decimal Zero = 0.00m;

    // The printed rows per table.
    private const int Runs = 2;
    private const int TableRows = 2;

    /// <summary>The plan calculator over this file — the one call the projection makes.</summary>
    public static PlanCalculation Calculate(CaseFile caseFile, PlanBody plan)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        var inputs = Chapter13Plan.BuildPlanInputs(caseFile, Chapter13Plan.ExemptionCaseOf(caseFile), caseFile.CreatedOn);
        return Chapter13Plan.Calculate(inputs, plan);
    }

    /// <summary>A stored percentage as the form prints it before its own %: <c>7</c>, <c>6.25</c>.</summary>
    public static string Rate(decimal value) => value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The values for <c>form/b113@2017-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        var problems = new List<string>();
        Caption(release, values, caseFile, problems);
        if (caseFile.Plans.Count == 0)
        {
            return problems.Count > 0 ? throw new FormProjectionException(problems) : values;
        }

        var plan = caseFile.Plans[0];
        var calc = Calculate(caseFile, plan);

        problems.AddRange(calc.Problems.Select(problem => $"Plan: {problem}"));
        if (calc.Feasibility.Feasible == false)
        {
            problems.AddRange(calc.Feasibility.Reasons.Select(reason => $"The plan is not feasible — {reason}"));
        }

        if (calc.BestInterests.Passes == false)
        {
            problems.Add(
                "The plan fails the § 1325(a)(4) liquidation test: it pays general"
                + $" unsecured creditors {Str(calc.BestInterests.PlanPercentage)}% against"
                + $" the {Str(calc.BestInterests.LiquidationPercentage)}% a Chapter 7"
                + " liquidation would — § 5.1 of the form promises at least the"
                + " liquidation amount");
        }

        if (plan.PriorityPercentage is { } priorityShare && priorityShare < 100)
        {
            problems.Add(
                "The plan pays priority claims less than in full — § 1322(a)(4)'s"
                + " treatment of an assigned domestic support obligation, which § 4.5"
                + " lists claim by claim and is not prepared here");
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        Part2(release, values, calc, problems);
        var liens = LienEngine.Derive(caseFile);
        var exhibitA = Section31(release, values, caseFile, plan, calc, liens, problems);
        var exhibitB = Section32(release, values, caseFile, plan, calc, liens, problems);
        Section35(release, values, caseFile, plan, liens, problems);
        values["line_3_3_none"] = new CheckFill();
        values["line_3_4_none"] = new CheckFill();
        values[exhibitB is not null ? "line_1_1_included" : "line_1_1_not_included"] = new CheckFill();
        values["line_1_2_not_included"] = new CheckFill();
        values["line_1_3_not_included"] = new CheckFill();

        var exhibitE = Part4(values, plan, calc);
        var exhibitF = Part5(values, plan, calc);
        Part6(release, values, caseFile, problems);
        values["line_8_1_none"] = new CheckFill();

        var lines = new Dictionary<char, decimal>
        {
            ['a'] = exhibitA is { } a && a != 0 ? a : Zero,
            ['b'] = exhibitB is { } b && b != 0 ? b : Zero,
            ['e'] = exhibitE,
            ['f'] = exhibitF,
        };
        var total = Zero;
        foreach (var letter in "abcdefghij")
        {
            var amount = lines.GetValueOrDefault(letter, Zero);
            total += amount;
            values[$"exhibit.{letter}"] = MoneyText(amount);
        }

        values["exhibit.total"] = MoneyText(total);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(problems);
        }

        return values;
    }

    private static string Str(decimal? value) => value is { } present ? Py.F(present) : "None";

    private static TextFill MoneyText(decimal value) => new(FormatMoney(value));

    private static void Lines(FormRelease release, FieldValues values, string fieldId, int row, int perRow, string? value, List<string> problems)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var spec = release.Field(fieldId);
        var box = release.Boxes[spec.PdfNames[row * perRow]];
        var wrapped = WrapWidth(value, box.W, perRow, $"{fieldId} row {row + 1}", problems, box.Size);
        for (var index = 0; index < wrapped.Count; index++)
        {
            RowFill(release, values, fieldId, (row * perRow) + index, new TextFill(wrapped[index]), problems);
        }
    }

    private static List<(decimal Payment, int Months)> PaymentRuns(IReadOnlyList<(int Month, decimal Payment)> schedule)
    {
        var runs = new List<(decimal Payment, int Months)>();
        foreach (var (_, payment) in schedule)
        {
            if (runs.Count > 0 && runs[^1].Payment == payment)
            {
                runs[^1] = (runs[^1].Payment, runs[^1].Months + 1);
            }
            else
            {
                runs.Add((payment, 1));
            }
        }

        return runs;
    }

    private static PayoutRow? Row(PlanCalculation calc, string key) =>
        calc.Classes.SelectMany(summary => summary.Rows).FirstOrDefault(row => row.Key == key);

    private static decimal ClassPayout(PlanCalculation calc, string key) =>
        calc.Classes.FirstOrDefault(summary => summary.Key == key) is { } summary ? summary.Principal + summary.Interest : Zero;

    private static List<(string Id, ClaimBody Claim, SecuredTreatment Treatment)> Treated(CaseFile caseFile, PlanBody plan, SecuredTreatmentKind kind)
    {
        var rows = new List<(string, ClaimBody, SecuredTreatment)>();
        foreach (var (claimId, claim) in caseFile.Claims)
        {
            if (claim.ClaimClass != ClaimClass.Secured)
            {
                continue;
            }

            var treatment = plan.SecuredTreatments.FirstOrDefault(t => t.ClaimId == claimId);
            if (treatment is not null && treatment.Treatment == kind)
            {
                rows.Add((claimId, claim, treatment));
            }
        }

        return rows;
    }

    private static void TooMany(string section, int found, int printed, List<string> problems) =>
        problems.Add($"§ {section}: the plan lists {found}; Official Form 113 prints {printed} rows, and adding rows is a nonstandard change (Part 8), which is not prepared here");

    private static void Caption(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var (before, after) = B2030.DistrictParts(caseFile.Case.District);
        RowFill(release, values, "caption.district", 0, TextOrNone(before), problems);
        RowFill(release, values, "caption.district", 1, TextOrNone(after), problems);
        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "caption.debtor1_name"), (FilingRole.Debtor2, "caption.debtor2_name") })
        {
            if (FullName(caseFile.Debtor(role)) is { } name)
            {
                values[fieldId] = new TextFill(name);
            }
        }

        if (FullName(caseFile.Debtor(FilingRole.Debtor1)) is { } header)
        {
            var rows = new RowFills();
            foreach (var box in release.Field("header.debtor_name").PdfNames)
            {
                rows[box] = new TextFill(header);
            }

            values["header.debtor_name"] = rows;
        }

        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "sign.debtor1_executed_on"), (FilingRole.Debtor2, "sign.debtor2_executed_on") })
        {
            if (caseFile.Debtor(role)?.Body.SignedAt is { } signed)
            {
                values[fieldId] = new TextFill(FormatDate(signed));
            }
        }

        if (B2030.AttorneyOf(caseFile)?.SignatureDate is { } attorneyDate)
        {
            values["sign.attorney_date"] = new TextFill(FormatDate(attorneyDate));
        }
    }

    private static void Part2(FormRelease release, FieldValues values, PlanCalculation calc, List<string> problems)
    {
        var runs = PaymentRuns(calc.Funding.Schedule);
        if (runs.Count > Runs)
        {
            problems.Add($"§ 2.1: the plan's payments change {runs.Count - 1} times; Official Form 113 prints {Runs} payment lines, and adding lines is a nonstandard change (Part 8), which is not prepared here");
        }

        for (var index = 0; index < Math.Min(Runs, runs.Count); index++)
        {
            var (payment, months) = runs[index];
            RowFill(release, values, "line_2_1_amount", index, MoneyText(payment), problems);
            RowFill(release, values, "line_2_1_per", index, new TextFill("month"), problems);
            RowFill(release, values, "line_2_1_months", index, new TextFill(months.ToString(System.Globalization.CultureInfo.InvariantCulture)), problems);
        }

        var lumps = calc.Funding.LumpSums;
        if (lumps.Count == 0)
        {
            values["line_2_4_none"] = new CheckFill();
        }
        else
        {
            values["line_2_4_additional"] = new CheckFill();
            var described = string.Join("; ", lumps.Select(lump => $"{(string.IsNullOrEmpty(lump.Description) ? "Lump sum" : lump.Description)}: ${Py.Grouped(lump.Amount)} in month {lump.Month}"));
            var spec = release.Field("line_2_4_description");
            var box = release.Boxes[spec.PdfNames[0]];
            var wrapped = WrapWidth(described, box.W, spec.PdfNames.Count, "line_2_4_description", problems, box.Size);
            for (var index = 0; index < wrapped.Count; index++)
            {
                RowFill(release, values, "line_2_4_description", index, new TextFill(wrapped[index]), problems);
            }
        }

        values["line_2_5_total"] = MoneyText(calc.Funding.Total);
    }

    private static string? CreditorName(CaseFile caseFile, ClaimBody claim) => caseFile.Creditor(claim.CreditorId)?.Name;

    private static decimal? Section31(FormRelease release, FieldValues values, CaseFile caseFile, PlanBody plan, PlanCalculation calc, LienFigures liens, List<string> problems)
    {
        var rows = Treated(caseFile, plan, SecuredTreatmentKind.CureAndMaintain);
        if (rows.Count == 0)
        {
            values["line_3_1_none"] = new CheckFill();
            return null;
        }

        values["line_3_1_maintain"] = new CheckFill();
        if (rows.Count > TableRows)
        {
            TooMany("3.1", rows.Count, TableRows, problems);
        }

        var total = Zero;
        for (var index = 0; index < Math.Min(TableRows, rows.Count); index++)
        {
            var (claimId, claim, treatment) = rows[index];
            var lien = liens.Claim(claimId);
            Lines(release, values, "line_3_1_creditor", index, 2, CreditorName(caseFile, claim), problems);
            Lines(release, values, "line_3_1_collateral", index, 2, lien?.CollateralDescription, problems);
            if (treatment.MaintenancePayment is { } maintenance)
            {
                RowFill(release, values, "line_3_1_installment", index, new TextFill(FormatMoney(maintenance)), problems);
                RowFill(release, values, "line_3_1_disbursed_trustee", index, new CheckFill(), problems);
            }
            else
            {
                RowFill(release, values, "line_3_1_disbursed_debtor", index, new CheckFill(), problems);
            }

            if (treatment.Arrearage is { } arrearage)
            {
                RowFill(release, values, "line_3_1_arrearage", index, new TextFill(FormatMoney(arrearage)), problems);
            }

            if (treatment.ArrearageInterestRate is { } rate)
            {
                RowFill(release, values, "line_3_1_rate", index, new TextFill(Rate(rate)), problems);
            }

            var arrearageRow = Row(calc, $"secured:{claimId}");
            var conduit = Row(calc, $"conduit:{claimId}");
            if (arrearageRow?.MonthlyPayment is { } monthly)
            {
                RowFill(release, values, "line_3_1_monthly", index, MoneyText(monthly), problems);
            }

            var paid = (arrearageRow?.Payout ?? Zero) + (conduit?.Payout ?? Zero);
            RowFill(release, values, "line_3_1_total", index, MoneyText(paid), problems);
            total += paid;
        }

        return total;
    }

    private static decimal? Section32(FormRelease release, FieldValues values, CaseFile caseFile, PlanBody plan, PlanCalculation calc, LienFigures liens, List<string> problems)
    {
        var rows = Treated(caseFile, plan, SecuredTreatmentKind.Cramdown);
        if (rows.Count == 0)
        {
            values["line_3_2_none"] = new CheckFill();
            return null;
        }

        values["line_3_2_request"] = new CheckFill();
        if (rows.Count > TableRows)
        {
            TooMany("3.2", rows.Count, TableRows, problems);
        }

        var total = Zero;
        for (var index = 0; index < Math.Min(TableRows, rows.Count); index++)
        {
            var (claimId, claim, treatment) = rows[index];
            var lien = liens.Claim(claimId);
            Lines(release, values, "line_3_2_creditor", index, 2, CreditorName(caseFile, claim), problems);
            if (claim.Amount is { } amount)
            {
                RowFill(release, values, "line_3_2_claim_amount", index, new TextFill(FormatMoney(amount)), problems);
            }

            if (lien is not null)
            {
                Lines(release, values, "line_3_2_collateral", index, 2, lien.CollateralDescription, problems);
                if (lien.CollateralValue is { } collateral)
                {
                    RowFill(release, values, "line_3_2_collateral_value", index, MoneyText(collateral), problems);
                }

                RowFill(release, values, "line_3_2_senior", index, MoneyText(lien.SeniorLiens), problems);
            }

            if (treatment.InterestRate is { } rate)
            {
                RowFill(release, values, "line_3_2_rate", index, new TextFill(Rate(rate)), problems);
            }

            if (Row(calc, $"secured:{claimId}") is not { } secured)
            {
                continue;
            }

            RowFill(release, values, "line_3_2_secured", index, MoneyText(secured.Allowed), problems);
            if (secured.MonthlyPayment is { } monthly)
            {
                RowFill(release, values, "line_3_2_monthly", index, MoneyText(monthly), problems);
            }

            RowFill(release, values, "line_3_2_total", index, MoneyText(secured.Payout), problems);
            total += secured.Payout;
        }

        return total;
    }

    private static void Section35(FormRelease release, FieldValues values, CaseFile caseFile, PlanBody plan, LienFigures liens, List<string> problems)
    {
        var rows = Treated(caseFile, plan, SecuredTreatmentKind.Surrender);
        if (rows.Count == 0)
        {
            values["line_3_5_none"] = new CheckFill();
            return;
        }

        values["line_3_5_surrender"] = new CheckFill();
        if (rows.Count > TableRows)
        {
            TooMany("3.5", rows.Count, TableRows, problems);
        }

        for (var index = 0; index < Math.Min(TableRows, rows.Count); index++)
        {
            var (claimId, claim, _) = rows[index];
            var lien = liens.Claim(claimId);
            RowFill(release, values, "line_3_5_creditor", index, TextOrNone(CreditorName(caseFile, claim)), problems);
            RowFill(release, values, "line_3_5_collateral", index, TextOrNone(lien?.CollateralDescription), problems);
        }
    }

    private static decimal Part4(FieldValues values, PlanBody plan, PlanCalculation calc)
    {
        if (plan.TrusteePercentage is { } percentage)
        {
            values["line_4_2_percentage"] = new TextFill(Rate(percentage));
        }

        var trustee = ClassPayout(calc, Chapter13Plan.ClassTrustee);
        values["line_4_2_total"] = MoneyText(trustee);
        if (plan.AttorneyFees is { } fees)
        {
            values["line_4_3_attorney_fees"] = new TextFill(FormatMoney(fees));
        }

        var priority = calc.Classes.FirstOrDefault(summary => summary.Key == Chapter13Plan.ClassPriority);
        if (priority is null || priority.Rows.Count == 0)
        {
            values["line_4_4_none"] = new CheckFill();
        }
        else
        {
            values["line_4_4_estimate"] = new CheckFill();
            values["line_4_4_total"] = MoneyText(priority.Allowed);
        }

        values["line_4_5_none"] = new CheckFill();
        return trustee + ClassPayout(calc, Chapter13Plan.ClassAttorney) + ClassPayout(calc, Chapter13Plan.ClassPriority);
    }

    private static decimal Part5(FieldValues values, PlanBody plan, PlanCalculation calc)
    {
        var treatment = plan.UnsecuredTreatment ?? UnsecuredTreatment.Pot;
        if (treatment == UnsecuredTreatment.Amount)
        {
            values["line_5_1_sum"] = new CheckFill();
            if (plan.UnsecuredAmount is { } amount)
            {
                values["line_5_1_sum_amount"] = new TextFill(FormatMoney(amount));
            }
        }
        else if (treatment == UnsecuredTreatment.Percentage)
        {
            values["line_5_1_percentage"] = new CheckFill();
            if (plan.UnsecuredPercentage is { } percentage)
            {
                values["line_5_1_percentage_value"] = new TextFill(Rate(percentage));
            }

            if (calc.UnsecuredTarget is { } target)
            {
                values["line_5_1_percentage_estimate"] = MoneyText(target);
            }
        }
        else
        {
            values["line_5_1_remaining"] = new CheckFill();
        }

        values["line_5_1_liquidation"] = MoneyText(calc.Liquidation.Available);
        values["line_5_2_none"] = new CheckFill();
        values["line_5_3_none"] = new CheckFill();
        return ClassPayout(calc, Chapter13Plan.ClassUnsecured);
    }

    private static void Part6(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var assumed = caseFile.ContractLeases.Select(pair => pair.Body).Where(body => body.Intention == ContractLeaseIntention.Assume).ToList();
        if (assumed.Count == 0)
        {
            values["line_6_1_none"] = new CheckFill();
            return;
        }

        values["line_6_1_assumed"] = new CheckFill();
        if (assumed.Count > TableRows)
        {
            TooMany("6.1", assumed.Count, TableRows, problems);
        }

        for (var index = 0; index < Math.Min(TableRows, assumed.Count); index++)
        {
            var lease = assumed[index];
            Lines(release, values, "line_6_1_creditor", index, 2, lease.CounterpartyName, problems);
            Lines(release, values, "line_6_1_description", index, 4, lease.Description, problems);
            RowFill(release, values, "line_6_1_disbursed_debtor", index, new CheckFill(), problems);
        }
    }
}
