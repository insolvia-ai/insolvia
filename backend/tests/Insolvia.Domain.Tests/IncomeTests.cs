using System.Text.Json.Nodes;
using Insolvia.Core.Income;

namespace Insolvia.Core.Tests;

/// <summary>The dated income history, translated from test_income.py.</summary>
public sealed class IncomeTests
{
    [Fact]
    public void A_full_pay_period_record_parses()
    {
        var body = IncomeRules.ParsePayPeriodRecord(Body.Of("""
            {"employment_id": "em-1", "period_start": "2026-03-01", "period_end": "2026-03-14", "pay_date": "2026-03-20",
             "gross": "2600.00", "net": "2015.00",
             "deductions": [{"id": "d1", "category": "tax", "amount": "455.00"}, {"id": "d2", "category": "other", "description": "Parking"}],
             "frequency": "biweekly"}
            """));
        Assert.Equal(new DateOnly(2026, 3, 20), body.PayDate);
        Assert.Equal([DeductionCategory.Tax, DeductionCategory.Other], body.Deductions.Select(d => d.Category!.Value));
    }

    [Theory]
    [InlineData("""{"frequency": "fortnightly"}""", "frequency")]
    [InlineData("""{"pay_date": "03/20/2026"}""", "pay_date")]
    [InlineData("""{"gross": "2600.001"}""", "gross")]
    [InlineData("""{"deductions": [{"category": "tax"}]}""", "deductions[0].id")]
    [InlineData("""{"deductions": [{"id": "d1", "category": "yacht"}]}""", "deductions[0].category")]
    [InlineData("""{"deductions": [{"id": "d1", "category": "tax"}, {"id": "d1", "category": "insurance"}]}""", "deductions[1].id")]
    public void Malformed_pay_period_fields_are_named(string payload, string path) =>
        Assert.Contains(path, Assert.Throws<FieldValidationException>(() => IncomeRules.ParsePayPeriodRecord(Body.Of(payload))).Fields.Keys);

    [Fact]
    public void Every_income_category_parses_including_the_excluded_set()
    {
        foreach (var category in IncomeRules.IncludedCategories.Concat(IncomeRules.ExcludedCategories))
        {
            var body = IncomeRules.ParseOtherIncomeRecord(new JsonObject { ["category"] = category.ToWire(), ["amount"] = "100.00", ["received_on"] = "2026-04-01" });
            Assert.Equal(category, body.Category);
        }
    }

    [Fact]
    public void Expenses_belong_only_to_business_and_rental_receipts()
    {
        foreach (var category in new[] { "business", "rental" })
        {
            Assert.Equal(600.00m, IncomeRules.ParseOtherIncomeRecord(new JsonObject { ["category"] = category, ["amount"] = "1800.00", ["expenses"] = "600.00" }).Expenses);
        }

        var error = Assert.Throws<FieldValidationException>(() => IncomeRules.ParseOtherIncomeRecord(Body.Of("""{"category": "unemployment", "amount": "275.00", "expenses": "10.00"}""")));
        Assert.Contains("expenses", error.Fields.Keys);
    }

    [Fact]
    public void The_income_vocabularies_keep_their_wire_strings()
    {
        Assert.Equal(
            ["alimony_maintenance", "household_contributions", "business", "rental", "interest_dividends_royalties", "unemployment", "pension_retirement", "other",
             "social_security_act_benefit", "veterans_disability_compensation", "war_crime_victim_payment", "terrorism_victim_payment"],
            Wire.Names<OtherIncomeCategory>());
        Assert.Equal(["weekly", "biweekly", "semimonthly", "monthly", "other"], Wire.Names<PayFrequency>());
        Assert.Equal(["employed", "not_employed"], Wire.Names<EmploymentStatus>());
    }
}
