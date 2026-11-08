using Insolvia.Core;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Petitions;
using Insolvia.Core.Sofa;
using Insolvia.Engines.Regulatory;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B107 @ 2025-04-01 (revision 04/25) — the Statement of Financial Affairs
/// (from <c>form_projections/b107.py</c>). The typed-entry table fanned
/// back out by payload type, one question at a time; each gate derived from
/// whether entries of its type exist; overflow and the PDF's shared-widget
/// defects are problems, never silent misprints.
/// </summary>
public static class B107
{
    private static readonly Dictionary<MaritalStatusKind, string> MaritalExports = new()
    {
        [MaritalStatusKind.Married] = "married",
        [MaritalStatusKind.NotMarried] = "yes",
    };

    private static readonly Dictionary<PaymentPurpose, string> PaymentPurposeBoxes = new()
    {
        [PaymentPurpose.Mortgage] = "q6_was_payment_for_mortgage",
        [PaymentPurpose.Car] = "q6_was_payment_for_car",
        [PaymentPurpose.CreditCard] = "q6_was_payment_for_credit_card",
        [PaymentPurpose.LoanRepayment] = "q6_was_payment_for_loan",
        [PaymentPurpose.SuppliersOrVendors] = "q6_was_payment_for_vendors",
        [PaymentPurpose.Other] = "q6_was_payment_for_other",
    };

    private static readonly Dictionary<RepossessionAction, string> RepossessionBoxes = new()
    {
        [RepossessionAction.Repossessed] = "q10_happened_repossessed",
        [RepossessionAction.Foreclosed] = "q10_happened_foreclosed",
        [RepossessionAction.Garnished] = "q10_happened_garnished",
        [RepossessionAction.Attached] = "q10_happened_attached",
    };

    private static readonly Dictionary<AccountType, string> AccountTypeBoxes = new()
    {
        [AccountType.Checking] = "q20_type_checking",
        [AccountType.Savings] = "q20_type_savings",
        [AccountType.MoneyMarket] = "q20_type_money_market",
        [AccountType.Brokerage] = "q20_type_brokerage",
        [AccountType.Other] = "q20_type_other",
    };

    private static readonly Dictionary<BusinessConnectionKind, string> ConnectionBoxes = new()
    {
        [BusinessConnectionKind.SoleProprietor] = "q27_connection_sole_proprietor",
        [BusinessConnectionKind.LlcMember] = "q27_connection_llc_member",
        [BusinessConnectionKind.Partner] = "q27_connection_partner",
        [BusinessConnectionKind.OfficerOrDirector] = "q27_connection_officer",
        [BusinessConnectionKind.OwnerOf5Percent] = "q27_connection_owner_5pct",
    };

    /// <summary>The values for <c>form/b107@2025-04-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        Q1Q3(release, values, caseFile, problems);
        Q4Q5(release, values, caseFile, problems);
        Q6(release, values, caseFile, problems);
        Q7Q8(release, values, caseFile, problems);
        Q9Q12(release, values, caseFile, problems);
        Q13Q17(release, values, caseFile, problems);
        Q18Q23(release, values, caseFile, problems);
        Q24Q28(release, values, caseFile, problems);

        // Part 12 — signatures: dates only, plus the preparer question.
        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "sign.debtor1_date"), (FilingRole.Debtor2, "sign.debtor2_date") })
        {
            if (caseFile.Debtor(role)?.Body.SignedAt is { } signed)
            {
                values[fieldId] = new TextFill(FormatDate(signed));
            }
        }

        var preparers = caseFile.FilingProfessionals.Where(p => p.Role == FilingProfessionalRole.BankruptcyPetitionPreparer).ToList();
        values["sign.paid_preparer"] = YesNo(release, "sign.paid_preparer", preparers.Count > 0);
        if (preparers.Count > 0 && FullName(preparers[0].Name) is { } named)
        {
            values["sign.preparer_name"] = new TextFill(named);
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static List<T> Entries<T>(CaseFile caseFile)
        where T : SofaPayload =>
        [.. caseFile.SofaEntries.Select(entry => entry.Payload).OfType<T>()];

    private static (string FieldId, string? Value)[] AddressPairs(Address address, string street, string? street2, string city, string state, string zip)
    {
        var pairs = new List<(string, string?)> { (street, address.Line1) };
        if (street2 is not null)
        {
            pairs.Add((street2, address.Line2));
        }

        pairs.Add((city, address.City));
        pairs.Add((state, address.State));
        pairs.Add((zip, address.PostalCode));
        return [.. pairs];
    }

    private static void PutRow(FormRelease release, FieldValues values, int index, IEnumerable<(string FieldId, string? Value)> pairs, List<string> problems)
    {
        foreach (var (fieldId, value) in pairs)
        {
            RowFill(release, values, fieldId, index, TextOrNone(value), problems);
        }
    }

    private static void Named(FormRelease release, FieldValues values, string fieldId, string pdfName, FieldFill fill)
    {
        var spec = release.Field(fieldId);
        if (!spec.PdfNames.Contains(pdfName))
        {
            throw new EngineKeyException($"{fieldId} claims no PDF field {Exact.Repr(pdfName)}");
        }

        if (spec.PdfNames.Count == 1)
        {
            values[fieldId] = fill;
            return;
        }

        values.Rows(fieldId)[pdfName] = fill;
    }

    private static void DateBoxes(FormRelease release, FieldValues values, string fieldId, int row, int perRow, ValueCollection<DateOnly> dates, List<string> problems)
    {
        if (dates.Count > perRow)
        {
            problems.Add($"{fieldId}: row {row + 1} prints {perRow} date boxes; the entry holds {dates.Count} dates");
        }

        for (var offset = 0; offset < Math.Min(perRow, dates.Count); offset++)
        {
            RowFill(release, values, fieldId, (row * perRow) + offset, new TextFill(FormatDate(dates[offset])), problems);
        }
    }

    private static string? Money(decimal? value) => value is { } present ? FormatMoney(present) : null;

    private static string? Date(DateOnly? value) => value is { } present ? FormatDate(present) : null;

    private static string Which(SofaDebtor? which) => which?.ToWire() ?? "debtor_1";

    private static string[] DebtorColumns(SofaDebtor? which) => which switch
    {
        SofaDebtor.Both => ["debtor1", "debtor2"],
        SofaDebtor.Debtor2 => ["debtor2"],
        _ => ["debtor1"],
    };

    private static void Q1Q3(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var marital = Entries<MaritalStatus>(caseFile).FirstOrDefault();
        if (marital?.Status is { } status)
        {
            values["q1_marital_status"] = new OptionFill(MaritalExports[status]);
        }

        var addresses = Entries<PriorAddress>(caseFile);
        values["q2_gate"] = YesNo(release, "q2_gate", addresses.Count > 0);
        var rows = new Dictionary<string, int>(StringComparer.Ordinal) { ["debtor_1"] = 0, ["debtor_2"] = 0 };
        foreach (var entry in addresses)
        {
            var which = Which(entry.WhichDebtor);
            var column = which is "debtor_1" or "both" ? "debtor1" : "debtor2";
            var key = column == "debtor1" ? "debtor_1" : "debtor_2";
            var index = rows[key];
            rows[key]++;
            PutRow(
                release,
                values,
                index,
                [
                    .. AddressPairs(entry.Address, $"q2_{column}_street", $"q2_{column}_street2", $"q2_{column}_city", $"q2_{column}_state", $"q2_{column}_zip"),
                    ($"q2_{column}_from", Date(entry.FromDate)),
                    ($"q2_{column}_to", Date(entry.ToDate)),
                ],
                problems);
            if (which == "both")
            {
                RowFill(release, values, "q2_debtor2_same_as_debtor1", index, new CheckFill(), problems);
                RowFill(release, values, "q2_debtor2_same_dates", index, new CheckFill(), problems);
            }
        }

        values["q3_community_property"] = YesNo(release, "q3_community_property", caseFile.CommunityHouseholdMembers.Count > 0);
    }

    private static void Q4Q5(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var entries = Entries<IncomeByPeriod>(caseFile);
        var employment = entries.Where(e => e.Kind is IncomeKind.WagesAndCommissions or IncomeKind.OperatingABusiness).ToList();
        var other = entries.Where(e => e.Kind == IncomeKind.Other).ToList();
        var year = caseFile.Case.CreatedAt.UtcDateTime.Year;
        var periods = new Dictionary<int, string> { [year] = "current", [year - 1] = "last_year", [year - 2] = "year_before" };

        values["q4_gate"] = YesNo(release, "q4_gate", employment.Count > 0);
        if (employment.Count > 0)
        {
            values["q4_last_year_yyyy"] = new TextFill((year - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            values["q4_year_before_yyyy"] = new TextFill((year - 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var buckets = new OrderedDictionary<(string Debtor, string Period), List<IncomeByPeriod>>();
        foreach (var entry in employment)
        {
            var startYear = entry.PeriodStart?.Year ?? year;
            if (!periods.TryGetValue(startYear, out var period))
            {
                problems.Add($"q4: the form prints income for the filing year and the two before it; a period starting {(entry.PeriodStart is { } start ? Exact.Iso(start) : "None")} fits none");
                continue;
            }

            foreach (var debtor in DebtorColumns(entry.WhichDebtor))
            {
                if (!buckets.TryGetValue((debtor, period), out var bucket))
                {
                    bucket = [];
                    buckets[(debtor, period)] = bucket;
                }

                bucket.Add(entry);
            }
        }

        foreach (var ((debtor, period), bucket) in buckets)
        {
            var kinds = bucket.Select(entry => entry.Kind).ToHashSet();
            if (kinds.Contains(IncomeKind.WagesAndCommissions))
            {
                values[$"q4_{debtor}_{period}_wages"] = new CheckFill();
            }

            if (kinds.Contains(IncomeKind.OperatingABusiness))
            {
                values[$"q4_{debtor}_{period}_business"] = new CheckFill();
            }

            values[$"q4_{debtor}_{period}_gross"] = new TextFill(FormatMoney(bucket.Aggregate(0m, (sum, entry) => sum + Amount(entry.GrossAmount))));
        }

        // Q5 — only the No box carries a widget, so a Yes leaves the field untouched.
        if (other.Count == 0)
        {
            values["q5_gate"] = new CheckFill();
        }

        var columns = new Dictionary<string, int>(StringComparer.Ordinal) { ["debtor1"] = 0, ["debtor2"] = 0 };
        foreach (var entry in other)
        {
            foreach (var debtor in DebtorColumns(entry.WhichDebtor))
            {
                var index = columns[debtor];
                columns[debtor]++;
                PutRow(release, values, index, [($"q5_{debtor}_source", entry.Description), ($"q5_{debtor}_gross", Money(entry.GrossAmount))], problems);
            }
        }
    }

    private static void Q6(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var payments = Entries<CreditorPayment>(caseFile);
        bool? consumer = null;
        if (caseFile.Petition?.DebtCharacter is { } character)
        {
            consumer = character == DebtCharacter.Consumer;
            values["q6_consumer_debts"] = YesNo(release, "q6_consumer_debts", consumer.Value);
        }

        if (consumer is { } isConsumer)
        {
            var constants = DollarAmounts.Resolve(caseFile.CreatedOn);
            var floor = constants.Amount(isConsumer ? "sofa-payment-floor-consumer" : "sofa-payment-floor-business");
            foreach (var payment in payments)
            {
                if (payment.TotalPaid is { } total && total < floor.Value)
                {
                    problems.Add(
                        $"q6: the payments to {(string.IsNullOrEmpty(payment.Creditor.Name) ? "a creditor" : payment.Creditor.Name)} "
                        + $"total {Exact.F(total)}, below the {floor.Amount} reporting floor ({floor.Citation}) — question 6 lists "
                        + "only creditors paid at least the floor; remove the entry or correct the total");
                }
            }

            var answer = payments.Count > 0;
            if (isConsumer)
            {
                values["q6_paid_600"] = YesNo(release, "q6_paid_600", answer);
            }
            else
            {
                values["q6_paid_8575"] = YesNo(release, "q6_paid_8575", answer);
            }
        }

        for (var index = 0; index < payments.Count; index++)
        {
            var payment = payments[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q6_creditor_name", payment.Creditor.Name),
                    .. AddressPairs(payment.Creditor.Address, "q6_creditor_street", "q6_creditor_street2", "q6_creditor_city", "q6_creditor_state", "q6_creditor_zip"),
                    ("q6_total_paid", Money(payment.TotalPaid)),
                    ("q6_still_owe", Money(payment.AmountStillOwed)),
                    ("q6_other_specify", payment.PaymentForOther),
                ],
                problems);
            DateBoxes(release, values, "q6_payment_dates", index, 3, payment.Dates, problems);
            foreach (var purpose in payment.PaymentFor)
            {
                RowFill(release, values, PaymentPurposeBoxes[purpose], index, new CheckFill(), problems);
            }
        }
    }

    private static void Q7Q8(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var insiders = Entries<InsiderPayment>(caseFile);
        values["q7_gate"] = YesNo(release, "q7_gate", insiders.Count > 0);
        for (var index = 0; index < insiders.Count; index++)
        {
            var payment = insiders[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q7_insider_name", payment.Insider.Name),
                    .. AddressPairs(payment.Insider.Address, "q7_insider_street", "q7_insider_street2", "q7_insider_city", "q7_insider_state", "q7_insider_zip"),
                    ("q7_total_paid", Money(payment.TotalPaid)),
                    ("q7_still_owe", Money(payment.AmountStillOwed)),
                    ("q7_reason", payment.Reason),
                ],
                problems);
            DateBoxes(release, values, "q7_payment_dates", index, 3, payment.Dates, problems);
        }

        var benefits = Entries<InsiderBenefitPayment>(caseFile);
        values["q8_gate"] = YesNo(release, "q8_gate", benefits.Count > 0);
        for (var index = 0; index < benefits.Count; index++)
        {
            var benefit = benefits[index];
            var name = benefit.Recipient.Name;
            if (name is null)
            {
                name = benefit.InsiderName;
            }
            else if (!string.IsNullOrEmpty(benefit.InsiderName) && benefit.InsiderName != name)
            {
                problems.Add($"q8: the row prints one name box; the entry stores both a recipient ({Exact.Repr(name)}) and an insider ({Exact.Repr(benefit.InsiderName)})");
            }

            PutRow(
                release,
                values,
                index,
                [
                    ("q8_insider_name", name),
                    .. AddressPairs(benefit.Recipient.Address, "q8_insider_street", "q8_insider_street2", "q8_insider_city", "q8_insider_state", "q8_insider_zip"),
                    ("q8_total_paid", Money(benefit.TotalPaid)),
                    ("q8_reason", benefit.Reason),
                ],
                problems);
            DateBoxes(release, values, "q8_payment_dates", index, 3, benefit.Dates, problems);
        }
    }

    private static void Q9Q12(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var lawsuits = Entries<Lawsuit>(caseFile);
        values["q9_gate"] = YesNo(release, "q9_gate", lawsuits.Count > 0);
        for (var index = 0; index < lawsuits.Count; index++)
        {
            var suit = lawsuits[index];
            if (index >= 2)
            {
                problems.Add("q9: the form prints 2 rows; more lawsuits overflow");
                continue;
            }

            var letter = "ab"[index];
            if (!string.IsNullOrEmpty(suit.CaseTitle))
            {
                Named(release, values, "q9_case_title", $"Case title1 9{letter}", new TextFill(suit.CaseTitle));
            }

            foreach (var (fieldId, pdfName, value) in new[]
            {
                ("q9_case_number", $"Case number 9{letter}", suit.CaseNumber),
                ("q9_case_nature", $"Case nature 9{letter}", suit.NatureOfCase),
                ("q9_court_name", $"Court Name 9{letter}", suit.Court.Name),
                ("q9_court_street", $"Court address 9{letter}", suit.Court.Address.Line1),
                ("q9_court_city", $"Court City 9{letter}", suit.Court.Address.City),
                ("q9_court_state", $"Court State 9{letter}", suit.Court.Address.State),
                ("q9_court_zip", $"Court ZIP Code 9{letter}", suit.Court.Address.PostalCode),
            })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    Named(release, values, fieldId, pdfName, new TextFill(value));
                }
            }

            if (suit.Status is { } status)
            {
                RowFill(release, values, "q9_status", index, new OptionFill(status.ToWire().Replace('_', ' ')), problems);
            }
        }

        var repossessions = Entries<Repossession>(caseFile);
        values["q10_gate"] = YesNo(release, "q10_gate", repossessions.Count > 0);
        for (var index = 0; index < repossessions.Count; index++)
        {
            var happened = repossessions[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q10_creditor_name", happened.Creditor.Name),
                    .. AddressPairs(happened.Creditor.Address, "q10_creditor_street", "q10_creditor_street2", "q10_creditor_city", "q10_creditor_state", "q10_creditor_zip"),
                    ("q10_describe_property", happened.Description),
                    ("q10_date", Date(happened.Date)),
                    ("q10_value", Money(happened.Value)),
                ],
                problems);
            if (happened.Action is { } action)
            {
                RowFill(release, values, RepossessionBoxes[action], index, new CheckFill(), problems);
            }
        }

        var setoffs = Entries<Setoff>(caseFile);
        values["q11_gate"] = YesNo(release, "q11_gate", setoffs.Count > 0);
        for (var index = 0; index < setoffs.Count; index++)
        {
            var setoff = setoffs[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q11_creditor_name", setoff.Creditor.Name),
                    .. AddressPairs(setoff.Creditor.Address, "q11_creditor_street", "q11_creditor_street2", "q11_creditor_city", "q11_creditor_state", "q11_creditor_zip"),
                    ("q11_describe_action", setoff.Description),
                    ("q11_date", Date(setoff.Date)),
                    ("q11_amount", Money(setoff.Amount)),
                ],
                problems);
        }

        values["q12_assignee"] = YesNo(release, "q12_assignee", Entries<Receivership>(caseFile).Count > 0);
    }

    private static void Q13Q17(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var gifts = Entries<Gift>(caseFile);
        values["q13_gate"] = YesNo(release, "q13_gate", gifts.Count > 0);
        for (var index = 0; index < gifts.Count; index++)
        {
            var gift = gifts[index];
            if (index >= 2)
            {
                problems.Add("q13: the form prints 2 rows; more gifts overflow");
                continue;
            }

            var letter = "ab"[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q13_recipient_name", gift.Recipient.Name),
                    .. AddressPairs(gift.Recipient.Address, "q13_recipient_street", "q13_recipient_street2", "q13_recipient_city", "q13_recipient_state", "q13_recipient_zip"),
                    ("q13_relationship", gift.Relationship),
                    ("q13_describe", gift.Description),
                ],
                problems);
            if (gift.Dates.Count > 2)
            {
                problems.Add($"q13: row {index + 1} prints 2 date boxes; the entry holds {gift.Dates.Count} dates");
            }

            for (var offset = 0; offset < Math.Min(2, gift.Dates.Count); offset++)
            {
                Named(release, values, "q13_dates", $"Date{offset + 1} 13{letter}", new TextFill(FormatDate(gift.Dates[offset])));
            }

            if (gift.Value is { } value)
            {
                Named(release, values, "q13_values", $"Amount1 13{letter}", new TextFill(FormatMoney(value)));
            }
        }

        var contributions = Entries<CharitableContribution>(caseFile);
        values["q14_gate"] = YesNo(release, "q14_gate", contributions.Count > 0);
        for (var index = 0; index < contributions.Count; index++)
        {
            var contribution = contributions[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q14_charity_name", contribution.Organization.Name),
                    .. AddressPairs(contribution.Organization.Address, "q14_charity_street", "q14_charity_street2", "q14_charity_city", "q14_charity_state", "q14_charity_zip"),
                    ("q14_describe", contribution.Description),
                ],
                problems);
            DateBoxes(release, values, "q14_dates", index, 2, contribution.Dates, problems);
            if (contribution.Value is { } value)
            {
                RowFill(release, values, "q14_values", index * 2, new TextFill(FormatMoney(value)), problems);
            }
        }

        var losses = Entries<Loss>(caseFile);
        values["q15_gate"] = YesNo(release, "q15_gate", losses.Count > 0);
        for (var index = 0; index < losses.Count; index++)
        {
            var loss = losses[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q15_describe_loss", loss.Description),
                    ("q15_describe_insurance", loss.InsuranceCoverage),
                    ("q15_date", Date(loss.Date)),
                    ("q15_value", Money(loss.Value)),
                ],
                problems);
        }

        var consultants = Entries<ConsultantPayment>(caseFile);
        values["q16_gate"] = YesNo(release, "q16_gate", consultants.Count > 0);
        for (var index = 0; index < consultants.Count; index++)
        {
            var payment = consultants[index];
            if (index >= 2)
            {
                problems.Add("q16: the form prints 2 rows; more payments overflow");
                continue;
            }

            PutRow(
                release,
                values,
                index,
                [
                    ("q16_payee_name", payment.Person.Name),
                    .. AddressPairs(payment.Person.Address, "q16_payee_street", "q16_payee_street2", "q16_payee_city", "q16_payee_state", "q16_payee_zip"),
                    ("q16_email", payment.EmailOrWebsite),
                    ("q16_payer", payment.WhoMadePayment),
                    ("q16_describe", payment.Description),
                ],
                problems);
            var letter = "ab"[index];
            if (payment.Date is { } date)
            {
                Named(release, values, "q16_dates", $"Date1 16{letter}", new TextFill(FormatDate(date)));
            }

            if (payment.Amount is { } amount)
            {
                Named(release, values, "q16_amounts", $"Amount1 16{letter}", new TextFill(FormatMoney(amount)));
            }
        }

        var workouts = Entries<CreditorAssistancePayment>(caseFile);
        values["q17_gate"] = YesNo(release, "q17_gate", workouts.Count > 0);
        for (var index = 0; index < workouts.Count; index++)
        {
            var workout = workouts[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q17_payee_name", workout.Person.Name),
                    .. AddressPairs(workout.Person.Address, "q17_payee_street", "q17_payee_street2", "q17_payee_city", "q17_payee_state", "q17_payee_zip"),
                    ("q17_describe", workout.Description),
                ],
                problems);
            if (workout.Date is { } date)
            {
                RowFill(release, values, "q17_dates", index * 2, new TextFill(FormatDate(date)), problems);
            }

            if (workout.Amount is { } amount)
            {
                RowFill(release, values, "q17_amounts", index * 2, new TextFill(FormatMoney(amount)), problems);
            }
        }
    }

    private static void Q18Q23(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var transfers = Entries<PropertyTransfer>(caseFile);
        values["q18_gate"] = YesNo(release, "q18_gate", transfers.Count > 0);
        for (var index = 0; index < transfers.Count; index++)
        {
            var transfer = transfers[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q18_recipient_name", transfer.Transferee.Name),
                    .. AddressPairs(transfer.Transferee.Address, "q18_recipient_street", "q18_recipient_street2", "q18_recipient_city", "q18_recipient_state", "q18_recipient_zip"),
                    ("q18_relationship", transfer.Relationship),
                    ("q18_describe_property", transfer.Description),
                    ("q18_describe_exchange", transfer.ValueReceived),
                    ("q18_date", Date(transfer.Date)),
                ],
                problems);
        }

        var trusts = Entries<SelfSettledTrust>(caseFile);
        values["q19_gate"] = YesNo(release, "q19_gate", trusts.Count > 0);
        for (var index = 0; index < trusts.Count; index++)
        {
            var trust = trusts[index];
            PutRow(release, values, index, [("q19_trust_name", trust.TrustName), ("q19_describe", trust.Description)], problems);
            if (trust.Date is { } date)
            {
                // The trust's date box is the PDF's second widget of Q18 row two's date field.
                var mirrored = transfers.Count > 1 ? transfers[1].Date : null;
                if (date != mirrored)
                {
                    problems.Add($"q19: the transfer-date box is the PDF's second widget of Q18 row two's date field and can only repeat its value; {Exact.Repr(Exact.Iso(date))} differs");
                }
            }
        }

        var accounts = Entries<ClosedAccount>(caseFile);
        values["q20_gate"] = YesNo(release, "q20_gate", accounts.Count > 0);
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var pairs = new List<(string FieldId, string? Value)>
            {
                ("q20_institution_name", account.Institution.Name),
            };
            pairs.AddRange(AddressPairs(account.Institution.Address, "q20_institution_street", "q20_institution_street2", "q20_institution_city", "q20_institution_state", "q20_institution_zip"));
            if (index == 1)
            {
                // Row two's ZIP box is the PDF's second widget of row one's.
                pairs.RemoveAt(pairs.Count - 1);
                var zip = account.Institution.Address.PostalCode;
                if (!string.IsNullOrEmpty(zip) && zip != accounts[0].Institution.Address.PostalCode)
                {
                    problems.Add($"q20: row two's ZIP box is the PDF's second widget of row one's and can only repeat its value; {Exact.Repr(zip)} differs");
                }
            }

            pairs.Add(("q20_account_last4", account.AccountLast4));
            pairs.Add(("q20_date_closed", Date(account.DateClosed)));
            pairs.Add(("q20_balance", Money(account.LastBalance)));
            PutRow(release, values, index, pairs, problems);
            if (account.AccountType is { } type)
            {
                RowFill(release, values, AccountTypeBoxes[type], index, new CheckFill(), problems);
            }
        }

        var boxes = Entries<SafeDepositBox>(caseFile);
        values["q21_gate"] = YesNo(release, "q21_gate", boxes.Count > 0);
        for (var index = 0; index < boxes.Count; index++)
        {
            var box = boxes[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q21_institution_name", box.Institution.Name),
                    .. AddressPairs(box.Institution.Address, "q21_institution_street", "q21_institution_street2", "q21_institution_city", "q21_institution_state", "q21_institution_zip"),
                    ("q21_describe", box.Description),
                ],
                problems);
            if (box.WhoHasAccess.Count > 1)
            {
                problems.Add($"q21: the form prints one access-holder block; the entry names {box.WhoHasAccess.Count} people");
            }

            if (box.WhoHasAccess.Count > 0)
            {
                values["q21_access_name"] = new TextFill(box.WhoHasAccess[0]);
            }

            if (box.StillHave is { } still)
            {
                values["q21_still_have"] = YesNo(release, "q21_still_have", still);
            }
        }

        var units = Entries<StorageUnit>(caseFile);
        values["q22_gate"] = YesNo(release, "q22_gate", units.Count > 0);
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q22_facility_name", unit.Facility.Name),
                    .. AddressPairs(unit.Facility.Address, "q22_facility_street", "q22_facility_street2", "q22_facility_city", "q22_facility_state", "q22_facility_zip"),
                    ("q22_describe", unit.Description),
                ],
                problems);
            if (unit.WhoHasAccess.Count > 1)
            {
                problems.Add($"q22: the form prints one access-holder block; the entry names {unit.WhoHasAccess.Count} people");
            }

            if (unit.WhoHasAccess.Count > 0)
            {
                values["q22_access_name"] = new TextFill(unit.WhoHasAccess[0]);
            }

            if (unit.StillHave is { } still)
            {
                values["q22_still_have"] = YesNo(release, "q22_still_have", still);
            }
        }

        var held = Entries<HeldForAnother>(caseFile);
        values["q23_gate"] = YesNo(release, "q23_gate", held.Count > 0);
        for (var index = 0; index < held.Count; index++)
        {
            var item = held[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q23_owner_name", item.Owner.Name),
                    .. AddressPairs(item.Owner.Address, "q23_owner_street", "q23_owner_street2", "q23_owner_city", "q23_owner_state", "q23_owner_zip"),
                    ("q23_where_street", item.Location),
                    ("q23_describe", item.Description),
                    ("q23_value", Money(item.Value)),
                ],
                problems);
        }
    }

    private static void Q24Q28(FormRelease release, FieldValues values, CaseFile caseFile, List<string> problems)
    {
        var notices = Entries<EnvironmentalNotice>(caseFile);
        foreach (var (question, kind) in new[] { ("q24", EnvironmentalNoticeKind.LiabilityNoticeReceived), ("q25", EnvironmentalNoticeKind.ReleaseReported) })
        {
            var matching = notices.Where(notice => notice.Kind == kind).ToList();
            values[$"{question}_gate"] = YesNo(release, $"{question}_gate", matching.Count > 0);
            for (var index = 0; index < matching.Count; index++)
            {
                var notice = matching[index];
                PutRow(
                    release,
                    values,
                    index,
                    [
                        ($"{question}_site_name", notice.Site.Name),
                        .. AddressPairs(notice.Site.Address, $"{question}_site_street", $"{question}_site_street2", $"{question}_site_city", $"{question}_site_state", $"{question}_site_zip"),
                        ($"{question}_gov_unit", notice.GovernmentalUnit.Name),
                        .. AddressPairs(notice.GovernmentalUnit.Address, $"{question}_gov_street", null, $"{question}_gov_city", $"{question}_gov_state", $"{question}_gov_zip"),
                        ($"{question}_law", notice.EnvironmentalLaw),
                        ($"{question}_date", Date(notice.Date)),
                    ],
                    problems);
            }
        }

        var proceedings = Entries<EnvironmentalProceeding>(caseFile);
        if (proceedings.Count == 0)
        {
            values["q26_gate_and_status"] = new OptionFill("no");
        }
        else
        {
            // The merged gate+status group: Yes and the status are two
            // appearance states of one wrongly-exclusive field.
            var states = new List<string> { "yes" };
            if (proceedings[0].Status is { } status)
            {
                states.Add(status.ToWire().Replace('_', ' '));
            }

            values["q26_gate_and_status"] = WidgetStatesFill.ByStates([.. states]);
            for (var index = 0; index < proceedings.Count; index++)
            {
                var proceeding = proceedings[index];
                PutRow(
                    release,
                    values,
                    index,
                    [
                        ("q26_case_title", proceeding.CaseTitle),
                        ("q26_case_number", proceeding.CaseNumber),
                        ("q26_case_nature", proceeding.NatureOfCase),
                        ("q26_court_name", proceeding.Court.Name),
                        .. AddressPairs(proceeding.Court.Address, "q26_court_street", null, "q26_court_city", "q26_court_state", "q26_court_zip"),
                    ],
                    problems);
            }
        }

        var businesses = Entries<BusinessConnection>(caseFile);
        values["q27_gate"] = YesNo(release, "q27_gate", businesses.Count > 0);

        // The ported code iterated a set here, in hash order; the values are
        // the same whatever the order.
        foreach (var member in businesses.SelectMany(business => business.Connection).Distinct())
        {
            values[ConnectionBoxes[member]] = new CheckFill();
        }

        for (var index = 0; index < businesses.Count; index++)
        {
            var business = businesses[index];
            if (index >= 3)
            {
                problems.Add("q27: the form prints 3 rows; more businesses overflow");
                continue;
            }

            var letter = "abc"[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q27_business_name", business.Business.Name),
                    .. AddressPairs(business.Business.Address, "q27_business_street", "q27_business_street2", "q27_business_city", "q27_business_state", "q27_business_zip"),
                    ("q27_nature", business.NatureOfBusiness),
                    ("q27_date_from", Date(business.FromDate)),
                ],
                problems);
            if (!string.IsNullOrEmpty(business.Ein))
            {
                var digits = string.Concat(business.Ein.EnumerateRunes().Where(System.Text.Rune.IsDigit).Select(rune => rune.ToString()));
                Named(release, values, "q27_ein", $"Debtor1.Employer Identification Number27{letter}", new TextFill(digits));
            }

            if (business.ToDate is { } to)
            {
                if (index == 2)
                {
                    // Row c's To box is the PDF's second widget of row b's.
                    var second = businesses.Count > 1 ? businesses[1].ToDate : null;
                    if (to != second)
                    {
                        problems.Add($"q27: row three's To box is the PDF's second widget of row two's and can only repeat its value; {Exact.Repr(Exact.Iso(to))} differs");
                    }
                }
                else
                {
                    RowFill(release, values, "q27_date_to", index, new TextFill(FormatDate(to)), problems);
                }
            }
        }

        var statements = Entries<FinancialStatementIssued>(caseFile);
        values["q28_gate"] = YesNo(release, "q28_gate", statements.Count > 0);
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            PutRow(
                release,
                values,
                index,
                [
                    ("q28_name", statement.Recipient.Name),
                    .. AddressPairs(statement.Recipient.Address, "q28_street", "q28_street2", "q28_city", "q28_state", "q28_zip"),
                    ("q28_date_issued", Date(statement.DateIssued)),
                ],
                problems);
        }
    }
}
