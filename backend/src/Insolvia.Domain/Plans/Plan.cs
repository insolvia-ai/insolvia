using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Plans;

// From plans.py: the Chapter 13 plan's PROPOSAL (issue 16.2 / #366), one per
// case. Its figures are the calculator's, never stored.

/// <summary>From <see cref="StartMonth"/> on (1-based, inclusive) the monthly payment is <see cref="MonthlyPayment"/>.</summary>
public sealed record StepPayment(string Id, int? StartMonth, decimal? MonthlyPayment);

/// <summary>A one-time payment into the plan in <see cref="Month"/>.</summary>
public sealed record LumpSum(string Id, int? Month, decimal? Amount, string? Description);

/// <summary>
/// How the plan treats one secured claim: cure-and-maintain (arrearage cured,
/// optional conduit maintenance), cramdown (paid to the collateral's value)
/// or surrender (nothing through the plan). <see cref="MonthlyPayment"/>
/// fixes the equal monthly payment; absent, the calculator amortises.
/// </summary>
public sealed record SecuredTreatment(
    string Id,
    string? ClaimId,
    SecuredTreatmentKind? Treatment,
    decimal? Arrearage,
    decimal? ArrearageInterestRate,
    decimal? MaintenancePayment,
    decimal? CramdownValue,
    decimal? InterestRate,
    decimal? MonthlyPayment);

/// <summary>The plan's proposal.</summary>
public sealed record PlanBody(
    int? TermMonths,
    PaymentSource? PaymentSource,
    decimal? MonthlyPayment,
    ValueCollection<StepPayment> StepPayments,
    ValueCollection<LumpSum> LumpSums,
    decimal? TrusteePercentage,
    decimal? AttorneyFees,
    decimal? AttorneyFeeMonthly,
    ValueCollection<SecuredTreatment> SecuredTreatments,
    decimal? PriorityPercentage,
    decimal? PriorityInterestRate,
    UnsecuredTreatment? UnsecuredTreatment,
    decimal? UnsecuredPercentage,
    decimal? UnsecuredAmount,
    decimal? UnsecuredInterestRate,
    [property: JsonPropertyName("chapter_7_other_costs")] decimal? Chapter7OtherCosts,
    [property: JsonPropertyName("chapter_7_other_costs_description")] string? Chapter7OtherCostsDescription,
    decimal? PresentValueRate);

/// <summary>The plan collection.</summary>
public static class PlanRules
{
    /// <summary>§ 1322(d) caps a plan at five years.</summary>
    public const int MaxTermMonths = 60;

    /// <summary>The trustee's percentage fee is capped at ten per cent.</summary>
    public static readonly decimal MaxTrusteePercentage = 10m;

    /// <summary>The collection's kind.</summary>
    public static readonly EntityKind<PlanBody> Kind = new("plan", "plans", "PLAN", Parse);

    /// <summary>Validates a plan body (Python's <c>parse_plan</c>).</summary>
    public static PlanBody Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        decimal? Amount(string key) => FieldParsers.Money(payload[key], key, errors);
        decimal? Rate(string key) => FieldParsers.Percentage(payload[key], key, errors);

        var term = FieldParsers.WholeNumber(payload["term_months"], "term_months", errors, maximum: 1000);
        if (term is not null and (< 1 or > MaxTermMonths))
        {
            errors["term_months"] = $"A plan runs 1 to {MaxTermMonths} months — § 1322(d) caps it at five years.";
            term = null;
        }

        var body = new PlanBody(
            TermMonths: term,
            PaymentSource: FieldParsers.Choice<PaymentSource>(payload["payment_source"], "payment_source", errors),
            MonthlyPayment: Amount("monthly_payment"),
            StepPayments: IdRows.Parse(
                payload["step_payments"],
                "step_payments",
                errors,
                (id, raw, path) => new StepPayment(
                    id,
                    Month(raw["start_month"], $"{path}.start_month", errors),
                    FieldParsers.Money(raw["monthly_payment"], $"{path}.monthly_payment", errors))),
            LumpSums: IdRows.Parse(
                payload["lump_sums"],
                "lump_sums",
                errors,
                (id, raw, path) => new LumpSum(
                    id,
                    Month(raw["month"], $"{path}.month", errors),
                    FieldParsers.Money(raw["amount"], $"{path}.amount", errors),
                    FieldParsers.Text(raw["description"], $"{path}.description", errors))),
            TrusteePercentage: FieldParsers.Percentage(payload["trustee_percentage"], "trustee_percentage", errors, maximum: MaxTrusteePercentage),
            AttorneyFees: Amount("attorney_fees"),
            AttorneyFeeMonthly: Amount("attorney_fee_monthly"),
            SecuredTreatments: IdRows.Parse(
                payload["secured_treatments"],
                "secured_treatments",
                errors,
                (id, raw, path) => new SecuredTreatment(
                    id,
                    FieldParsers.Text(raw["claim_id"], $"{path}.claim_id", errors, limit: 64),
                    FieldParsers.Choice<SecuredTreatmentKind>(raw["treatment"], $"{path}.treatment", errors),
                    FieldParsers.Money(raw["arrearage"], $"{path}.arrearage", errors),
                    FieldParsers.Percentage(raw["arrearage_interest_rate"], $"{path}.arrearage_interest_rate", errors),
                    FieldParsers.Money(raw["maintenance_payment"], $"{path}.maintenance_payment", errors),
                    FieldParsers.Money(raw["cramdown_value"], $"{path}.cramdown_value", errors),
                    FieldParsers.Percentage(raw["interest_rate"], $"{path}.interest_rate", errors),
                    FieldParsers.Money(raw["monthly_payment"], $"{path}.monthly_payment", errors))),
            PriorityPercentage: Rate("priority_percentage"),
            PriorityInterestRate: Rate("priority_interest_rate"),
            UnsecuredTreatment: FieldParsers.Choice<UnsecuredTreatment>(payload["unsecured_treatment"], "unsecured_treatment", errors),
            UnsecuredPercentage: Rate("unsecured_percentage"),
            UnsecuredAmount: Amount("unsecured_amount"),
            UnsecuredInterestRate: Rate("unsecured_interest_rate"),
            Chapter7OtherCosts: Amount("chapter_7_other_costs"),
            Chapter7OtherCostsDescription: FieldParsers.Text(payload["chapter_7_other_costs_description"], "chapter_7_other_costs_description", errors),
            PresentValueRate: Rate("present_value_rate"));

        // NOTE: the index is the row's position among the PARSED rows, not the
        // payload's — as the contract has it; it differs only when an earlier row was refused,
        // and then the request fails on that row anyway.
        var starts = new HashSet<int>();
        for (var index = 0; index < body.StepPayments.Count; index++)
        {
            if (body.StepPayments[index].StartMonth is not { } start)
            {
                continue;
            }

            if (!starts.Add(start))
            {
                errors[$"step_payments[{index}].start_month"] = "Another step already starts in this month.";
            }
        }

        var treated = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < body.SecuredTreatments.Count; index++)
        {
            if (body.SecuredTreatments[index].ClaimId is not { } claimId)
            {
                continue;
            }

            if (!treated.Add(claimId))
            {
                errors[$"secured_treatments[{index}].claim_id"] = "This claim already has a treatment.";
            }
        }

        IdRows.ThrowIfAny(errors);
        return body;
    }

    private static int? Month(JsonNode? value, string path, Dictionary<string, string> errors)
    {
        var month = FieldParsers.WholeNumber(value, path, errors, maximum: MaxTermMonths);
        if (month is < 1)
        {
            errors[path] = $"Must be a month of the plan, 1 to {MaxTermMonths}.";
            return null;
        }

        return month;
    }
}

/// <summary>Where a Chapter 13 plan's payment figure comes from (Python's <c>PAYMENT_SOURCES</c>).</summary>
public enum PaymentSource
{
    /// <summary>The wire value <c>fixed</c>.</summary>
    [JsonStringEnumMemberName("fixed")]
    Fixed,

    /// <summary>The wire value <c>schedule_j_excess</c>.</summary>
    [JsonStringEnumMemberName("schedule_j_excess")]
    ScheduleJExcess,

    /// <summary>The wire value <c>disposable_income</c>.</summary>
    [JsonStringEnumMemberName("disposable_income")]
    DisposableIncome,
}

/// <summary>How a plan treats a secured claim (Python's <c>SECURED_TREATMENTS</c>).</summary>
public enum SecuredTreatmentKind
{
    /// <summary>The wire value <c>cure_and_maintain</c>.</summary>
    [JsonStringEnumMemberName("cure_and_maintain")]
    CureAndMaintain,

    /// <summary>The wire value <c>cramdown</c>.</summary>
    [JsonStringEnumMemberName("cramdown")]
    Cramdown,

    /// <summary>The wire value <c>surrender</c>.</summary>
    [JsonStringEnumMemberName("surrender")]
    Surrender,
}

/// <summary>How a plan states what general unsecured creditors receive (Python's <c>UNSECURED_TREATMENTS</c>).</summary>
public enum UnsecuredTreatment
{
    /// <summary>The wire value <c>pot</c>.</summary>
    [JsonStringEnumMemberName("pot")]
    Pot,

    /// <summary>The wire value <c>percentage</c>.</summary>
    [JsonStringEnumMemberName("percentage")]
    Percentage,

    /// <summary>The wire value <c>amount</c>.</summary>
    [JsonStringEnumMemberName("amount")]
    Amount,
}
