using Insolvia.Core;
using Insolvia.Core.Assets;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106A/B @ 2015-12-01 (revision 12/15) — Schedule A/B's mapping (from
/// <c>form_projections/b106ab.py</c>): the category picks the line,
/// creation order the row. Part 1-2 rows print the full column set,
/// named-row lines one row per asset, single-box lines aggregate.
/// </summary>
public static class B106AB
{
    private static readonly Dictionary<string, string> PropertyTypeBoxes = new(StringComparer.Ordinal)
    {
        ["single_family_home"] = "type_single_family_home",
        ["duplex_or_multi_unit"] = "type_duplex_or_multi_unit_building",
        ["condominium_or_cooperative"] = "type_condominium_or_cooperative",
        ["manufactured_or_mobile_home"] = "type_manufactured_or_mobile_home",
        ["land"] = "type_land",
        ["investment_property"] = "type_investment_property",
        ["timeshare"] = "type_timeshare",
        ["other"] = "type_other",
    };

    private static readonly (string Line, string Category, bool HasDescription)[] SingleBoxLines =
    [
        ("6", "household_goods", true),
        ("7", "electronics", true),
        ("8", "collectibles", true),
        ("9", "sports_and_hobby_equipment", true),
        ("10", "firearms", true),
        ("11", "clothes", true),
        ("12", "jewelry", true),
        ("13", "non_farm_animals", true),
        ("14", "other_personal_or_household", true),
        ("16", "cash", false),
        ("25", "trusts_and_future_interests", true),
        ("26", "intellectual_property", true),
        ("27", "licenses_and_franchises", true),
        ("30", "other_amounts_owed", true),
        ("32", "property_due_from_a_death", true),
        ("33", "claims_against_third_parties", true),
        ("34", "other_contingent_and_unliquidated_claims", true),
        ("35", "other_financial_assets", true),
        ("38", "accounts_receivable", true),
        ("39", "office_equipment", true),
        ("40", "machinery_and_tools_of_trade", true),
        ("41", "inventory", true),
        ("43", "customer_lists_and_intangibles", true),
        ("47", "farm_animals", true),
        ("48", "crops", true),
        ("49", "farm_and_fishing_equipment", true),
        ("50", "farm_and_fishing_supplies", true),
        ("51", "other_farm_property", true),
    ];

    private static readonly (string Line, string Category, (string FieldId, string Attribute)[] Columns)[] RowLines =
    [
        ("17", "deposits_of_money", [("line_17_institution", "detail")]),
        ("18", "bonds_and_mutual_funds", [("line_18_institution", "detail")]),
        ("19", "non_publicly_traded_stock_and_business_interests", [("line_19_entity", "description"), ("line_19_ownership_pct", "detail")]),
        ("20", "government_and_corporate_bonds", [("line_20_issuer", "detail")]),
        ("21", "retirement_accounts", [("line_21_institution", "detail")]),
        ("22", "security_deposits_and_prepayments", [("line_22_institution", "detail")]),
        ("23", "annuities", [("line_23_issuer", "description")]),
        ("24", "education_accounts", [("line_24_institution", "description")]),
        ("31", "insurance_policy_interests", [("line_31_company", "description"), ("line_31_beneficiary", "detail")]),
        ("42", "partnership_and_joint_venture_interests", [("line_42_entity", "description"), ("line_42_ownership_pct", "detail")]),
        ("44", "other_business_property", [("line_44_description", "description")]),
    ];

    private static readonly Dictionary<string, string> RowAmountFields = new(StringComparer.Ordinal)
    {
        ["17"] = "line_17_amount",
        ["18"] = "line_18_amount",
        ["19"] = "line_19_amount",
        ["20"] = "line_20_amount",
        ["21"] = "line_21_amount",
        ["22"] = "line_22_amount",
        ["23"] = "line_23_amount",
        ["24"] = "line_24_amount",
        ["31"] = "line_31_value",
        ["42"] = "line_42_amount",
        ["44"] = "line_44_amount",
    };

    private static readonly (string Keyword, string Field)[] Line28Columns =
    [
        ("federal", "line_28_amount_federal"),
        ("state", "line_28_amount_state"),
        ("local", "line_28_amount_local"),
    ];

    private static readonly (string Keyword, string Field)[] Line29Columns =
    [
        ("property settlement", "line_29_amount_property_settlement"),
        ("divorce", "line_29_amount_divorce_settlement"),
        ("alimony", "line_29_amount_alimony"),
        ("maintenance", "line_29_amount_maintenance"),
        ("support", "line_29_amount_support"),
    ];

    private static readonly string[] Part2 = ["vehicle", "watercraft_aircraft_or_recreational_vehicle"];

    private static readonly string[] Part3 = [.. SingleBoxLines.Take(9).Select(line => line.Category)];

    private static readonly string[] Part4 =
    [
        "cash",
        "deposits_of_money",
        "bonds_and_mutual_funds",
        "non_publicly_traded_stock_and_business_interests",
        "government_and_corporate_bonds",
        "retirement_accounts",
        "security_deposits_and_prepayments",
        "annuities",
        "education_accounts",
        "trusts_and_future_interests",
        "intellectual_property",
        "licenses_and_franchises",
        "money_owed_to_you",
        "family_support_owed",
        "other_amounts_owed",
        "insurance_policy_interests",
        "property_due_from_a_death",
        "claims_against_third_parties",
        "other_contingent_and_unliquidated_claims",
        "other_financial_assets",
    ];

    private static readonly string[] Part5 =
    [
        "accounts_receivable",
        "office_equipment",
        "machinery_and_tools_of_trade",
        "inventory",
        "partnership_and_joint_venture_interests",
        "customer_lists_and_intangibles",
        "other_business_property",
    ];

    private static readonly string[] Part6 = ["farm_animals", "crops", "farm_and_fishing_equipment", "farm_and_fishing_supplies", "other_farm_property"];

    /// <summary>Line 55 — the Part 1 total; shared with 106Sum line 1a.</summary>
    public static decimal RealEstateTotal(CaseFile caseFile) => PortionTotal(In(caseFile, "real_property"));

    /// <summary>Line 62 — the Parts 2-7 totals summed; shared with 106Sum line 1b.</summary>
    public static decimal PersonalPropertyTotal(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return PortionTotal(caseFile.Assets.Select(pair => pair.Body).Where(body => Category(body) != "real_property").ToList());
    }

    /// <summary>The values for <c>form/b106ab@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        Part1(release, caseFile, values, problems);
        PartTwo(release, caseFile, values, problems);
        SingleBox(release, caseFile, values);
        KeywordLines(release, caseFile, values, problems);
        NamedRows(release, caseFile, values, problems);

        values["line_37_gate"] = YesNo(release, "line_37_gate", In(caseFile, Part5).Count > 0);
        values["line_46_gate"] = YesNo(release, "line_46_gate", In(caseFile, Part6).Count > 0);

        Totals(caseFile, values);

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    private static string? Category(AssetBody body) => body.Category?.ToWire();

    private static List<AssetBody> In(CaseFile caseFile, params string[] categories) =>
        [.. caseFile.Assets.Select(pair => pair.Body).Where(body => Category(body) is { } category && categories.Contains(category))];

    private static decimal PortionTotal(IEnumerable<AssetBody> assets) => assets.Aggregate(0m, (sum, asset) => sum + Amount(asset.ValuePortionOwned));

    private static string? EntryText(AssetBody asset)
    {
        var parts = new[] { asset.Description, asset.Detail }.Where(part => !string.IsNullOrEmpty(part));
        var joined = string.Join("; ", parts);
        return joined.Length > 0 ? joined : null;
    }

    private static string? Attribute(AssetBody asset, string attribute) => attribute == "detail" ? asset.Detail : asset.Description;

    private static TextFill? MoneyOrNone(decimal? value) => value is { } present ? new TextFill(FormatMoney(present)) : null;

    private static void Part1(FormRelease release, CaseFile caseFile, FieldValues values, List<string> problems)
    {
        var estates = In(caseFile, "real_property");
        values["line_1_any_real_estate"] = YesNo(release, "line_1_any_real_estate", estates.Count > 0);
        for (var index = 0; index < estates.Count; index++)
        {
            var asset = estates[index];
            RowFill(release, values, "real_estate.street", index, TextOrNone(asset.Description), problems);
            RowFill(release, values, "real_estate.county", index, TextOrNone(asset.County), problems);
            foreach (var member in asset.PropertyTypes)
            {
                RowFill(release, values, $"real_estate.{PropertyTypeBoxes[member.ToWire()]}", index, new CheckFill(), problems);
            }

            SharedColumns(release, values, "real_estate", index, asset, problems);
            RowFill(release, values, "real_estate.ownership_nature", index, TextOrNone(asset.OwnershipInterestDescription), problems);
            RowFill(release, values, "real_estate.other_information", index, TextOrNone(asset.Detail), problems);
        }

        values["line_2_part1_total"] = new TextFill(FormatMoney(PortionTotal(estates)));
    }

    private static void SharedColumns(FormRelease release, FieldValues values, string prefix, int index, AssetBody asset, List<string> problems)
    {
        if (asset.OwnershipInterest is { } interest)
        {
            var fieldId = $"{prefix}.who_has_interest";
            var spec = release.Field(fieldId);
            if (index >= spec.PdfNames.Count)
            {
                problems.Add($"{fieldId}: row {index + 1} does not exist — the form prints {spec.PdfNames.Count} rows");
            }
            else
            {
                RowFill(release, values, fieldId, index, CanonicalOption(release, fieldId, spec.PdfNames[index], interest.ToWire()), problems);
            }
        }

        if (asset.CommunityProperty == true)
        {
            RowFill(release, values, $"{prefix}.community_property", index, new CheckFill(), problems);
        }

        RowFill(release, values, $"{prefix}.value_entire", index, MoneyOrNone(asset.ValueEntire), problems);
        RowFill(release, values, $"{prefix}.value_portion", index, MoneyOrNone(asset.ValuePortionOwned), problems);
    }

    private static void PartTwo(FormRelease release, CaseFile caseFile, FieldValues values, List<string> problems)
    {
        foreach (var (gate, prefix, category) in new[] { ("line_3_any_vehicles", "vehicle", "vehicle"), ("line_4_any_other_vehicles", "other_vehicle", "watercraft_aircraft_or_recreational_vehicle") })
        {
            var assets = In(caseFile, category);
            values[gate] = YesNo(release, gate, assets.Count > 0);
            for (var index = 0; index < assets.Count; index++)
            {
                var asset = assets[index];
                RowFill(release, values, $"{prefix}.year", index, TextOrNone(asset.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture)), problems);
                RowFill(release, values, $"{prefix}.make", index, TextOrNone(asset.Make), problems);
                RowFill(release, values, $"{prefix}.model", index, TextOrNone(asset.Model), problems);
                if (prefix == "vehicle")
                {
                    RowFill(release, values, "vehicle.mileage", index, TextOrNone(asset.Mileage?.ToString(System.Globalization.CultureInfo.InvariantCulture)), problems);
                }

                RowFill(release, values, $"{prefix}.other_information", index, TextOrNone(asset.Detail), problems);
                SharedColumns(release, values, prefix, index, asset, problems);
            }
        }

        values["line_5_part2_total"] = new TextFill(FormatMoney(PortionTotal(In(caseFile, Part2))));
    }

    private static void SingleBox(FormRelease release, CaseFile caseFile, FieldValues values)
    {
        foreach (var (line, category, hasDescription) in SingleBoxLines)
        {
            var assets = In(caseFile, category);
            values[$"line_{line}_gate"] = YesNo(release, $"line_{line}_gate", assets.Count > 0);
            if (assets.Count == 0)
            {
                continue;
            }

            if (hasDescription)
            {
                var texts = assets.Select(EntryText).OfType<string>().ToList();
                if (texts.Count > 0)
                {
                    values[$"line_{line}_description"] = new TextFill(string.Join("; ", texts));
                }
            }

            values[$"line_{line}_amount"] = new TextFill(FormatMoney(PortionTotal(assets)));
            if (line == "43")
            {
                values["line_43_pii_gate"] = YesNo(release, "line_43_pii_gate", assets.Any(asset => asset.IncludesPersonalInformation == true));
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The keywords match against the lower-cased detail.")]
    private static void KeywordLines(FormRelease release, CaseFile caseFile, FieldValues values, List<string> problems)
    {
        foreach (var (line, category, columns) in new[] { ("28", "money_owed_to_you", Line28Columns), ("29", "family_support_owed", Line29Columns) })
        {
            var assets = In(caseFile, category);
            values[$"line_{line}_gate"] = YesNo(release, $"line_{line}_gate", assets.Count > 0);
            if (assets.Count == 0)
            {
                continue;
            }

            var texts = assets.Select(EntryText).OfType<string>().ToList();
            if (texts.Count > 0)
            {
                values[$"line_{line}_description"] = new TextFill(string.Join("; ", texts));
            }

            var totals = new OrderedDictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (asset.ValuePortionOwned is null)
                {
                    continue;
                }

                var detail = (asset.Detail ?? string.Empty).ToLowerInvariant();
                var box = columns.FirstOrDefault(column => detail.Contains(column.Keyword, StringComparison.Ordinal)).Field;
                if (box is null)
                {
                    problems.Add($"line_{line}: the amount columns route by keyword over the asset's detail ({string.Join(", ", columns.Select(column => column.Keyword))}); {(asset.Detail is null ? "None" : Exact.Repr(asset.Detail))} names none of them");
                    continue;
                }

                totals[box] = totals.GetValueOrDefault(box) + Amount(asset.ValuePortionOwned);
            }

            foreach (var (box, total) in totals)
            {
                values[box] = new TextFill(FormatMoney(total));
            }
        }
    }

    private static void NamedRows(FormRelease release, CaseFile caseFile, FieldValues values, List<string> problems)
    {
        foreach (var (line, category, columns) in RowLines)
        {
            var assets = In(caseFile, category);
            values[$"line_{line}_gate"] = YesNo(release, $"line_{line}_gate", assets.Count > 0);
            for (var index = 0; index < assets.Count; index++)
            {
                var asset = assets[index];
                foreach (var (fieldId, attribute) in columns)
                {
                    RowFill(release, values, fieldId, index, TextOrNone(Attribute(asset, attribute)), problems);
                }

                RowFill(release, values, RowAmountFields[line], index, MoneyOrNone(asset.ValuePortionOwned), problems);
            }
        }

        // Line 53: one description box, three amount rows.
        var others = In(caseFile, "other_property_not_listed");
        values["line_53_gate"] = YesNo(release, "line_53_gate", others.Count > 0);
        if (others.Count > 0)
        {
            var texts = others.Select(EntryText).OfType<string>().ToList();
            if (texts.Count > 0)
            {
                values["line_53_description"] = new TextFill(string.Join("; ", texts));
            }

            for (var index = 0; index < others.Count; index++)
            {
                RowFill(release, values, "line_53_amount", index, MoneyOrNone(others[index].ValuePortionOwned), problems);
            }
        }
    }

    private static void Totals(CaseFile caseFile, FieldValues values)
    {
        var partTotals = new (string FieldId, decimal Total)[]
        {
            ("line_15_part3_total", PortionTotal(In(caseFile, Part3))),
            ("line_36_part4_total", PortionTotal(In(caseFile, Part4))),
            ("line_45_part5_total", PortionTotal(In(caseFile, Part5))),
            ("line_52_part6_total", PortionTotal(In(caseFile, Part6))),
            ("line_54_part7_total", PortionTotal(In(caseFile, "other_property_not_listed"))),
        };
        var realEstate = RealEstateTotal(caseFile);
        var part2 = PortionTotal(In(caseFile, Part2));
        foreach (var (fieldId, total) in partTotals)
        {
            values[fieldId] = new TextFill(FormatMoney(total));
        }

        values["line_55_total"] = new TextFill(FormatMoney(realEstate));
        values["line_56_total"] = new TextFill(FormatMoney(part2));
        values["line_57_total"] = new TextFill(FormatMoney(partTotals[0].Total));
        values["line_58_total"] = new TextFill(FormatMoney(partTotals[1].Total));
        values["line_59_total"] = new TextFill(FormatMoney(partTotals[2].Total));
        values["line_60_total"] = new TextFill(FormatMoney(partTotals[3].Total));
        values["line_61_total"] = new TextFill(FormatMoney(partTotals[4].Total));
        var personal = PersonalPropertyTotal(caseFile);
        values["line_62_total_personal_property"] = new TextFill(FormatMoney(personal));
        values["line_63_total_all_property"] = new TextFill(FormatMoney(realEstate + personal));
    }
}
