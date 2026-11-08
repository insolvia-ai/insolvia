using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Domain.Engines;
using Insolvia.Domain.Engines.Regulatory;

namespace Insolvia.UstTool;

/// <summary>
/// A refusal the ingester stops on — the Python's <c>SystemExit(message)</c>:
/// a table shape it does not recognise, a figure that is not money, a
/// release the engine would not load. Printed, and the command exits 1.
/// </summary>
internal sealed class IngestException : Exception
{
    public IngestException()
    {
    }

    public IngestException(string message)
        : base(message)
    {
    }

    public IngestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The arguments every <c>ingest</c> command takes.</summary>
internal sealed record IngestArgs(string Period, string Effective, int Sequence, string Accessed, string OutRoot, string? MsaFile = null);

/// <summary>One release directory, before it is written: <c>&lt;series&gt;/&lt;dir&gt;/manifest.json + dataset.json</c>.</summary>
internal sealed record UstReleaseFiles(string SeriesId, string DirectoryName, JsonObject Manifest, JsonObject Dataset);

/// <summary>Fetches one URL's bytes (HttpClient in the tool, a fixture in a test).</summary>
internal delegate Task<byte[]> Fetcher(string url);

/// <summary>
/// Ingests one UST means-testing dataset release into the regulatory registry
/// (scripts/ingest-ust-data.py, ported). The U.S. Trustee Program republishes
/// the data the 122A/122C forms need — Census median family income by state
/// and household size, the IRS National Standards, the IRS Local Standards,
/// and the Chapter 13 administrative multipliers — as XLSX files on
/// per-period pages under https://www.justice.gov/ust/means-testing (issue
/// #99). Each period becomes a release of an effective-dated series per
/// docs/reference/effective-dating.md. This is the mechanical half of
/// "ingestion is a reviewed PR": download, convert to the payload shape the
/// engine validates, write manifest.json + dataset.json. The diff is the
/// review surface; merge is the release.
/// </summary>
internal static partial class Ingest
{
    public const string MediansSeries = UstData.MedianIncomeSeries;
    public const string NationalSeries = UstData.NationalStandardsSeries;
    public const string LocalSeries = UstData.LocalStandardsSeries;
    public const string Ch13Series = UstData.Ch13MultipliersSeries;

    /// <summary>
    /// What a release's <c>notes</c> say converted it. The releases committed
    /// before #585 name the Python script this replaced
    /// (<see cref="FormerConverter"/>) and keep it: they are history, and a
    /// re-ingest of one differs from it in this name only.
    /// </summary>
    public const string ConvertedBy = "the UST tool (backend/src/Insolvia.UstTool)";

    /// <summary>The converter the releases committed before #585 name.</summary>
    public const string FormerConverter = "scripts/ingest-ust-data.py";

    // The launch set (ADR 0017): the local-standards payload is scoped to
    // these states because nothing downstream computes for any other state —
    // the exemptions registry draws the same line. Widening the launch set
    // means a new release of ust/irs-local-standards with the new states.
    public static readonly IReadOnlyList<string> LaunchStates = ["FL", "GA", "TX"];

    public static readonly IReadOnlyDictionary<string, string> StateCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Alabama"] = "AL",
        ["Alaska"] = "AK",
        ["Arizona"] = "AZ",
        ["Arkansas"] = "AR",
        ["California"] = "CA",
        ["Colorado"] = "CO",
        ["Connecticut"] = "CT",
        ["Delaware"] = "DE",
        ["District of Columbia"] = "DC",
        ["Florida"] = "FL",
        ["Georgia"] = "GA",
        ["Hawaii"] = "HI",
        ["Idaho"] = "ID",
        ["Illinois"] = "IL",
        ["Indiana"] = "IN",
        ["Iowa"] = "IA",
        ["Kansas"] = "KS",
        ["Kentucky"] = "KY",
        ["Louisiana"] = "LA",
        ["Maine"] = "ME",
        ["Maryland"] = "MD",
        ["Massachusetts"] = "MA",
        ["Michigan"] = "MI",
        ["Minnesota"] = "MN",
        ["Mississippi"] = "MS",
        ["Missouri"] = "MO",
        ["Montana"] = "MT",
        ["Nebraska"] = "NE",
        ["Nevada"] = "NV",
        ["New Hampshire"] = "NH",
        ["New Jersey"] = "NJ",
        ["New Mexico"] = "NM",
        ["New York"] = "NY",
        ["North Carolina"] = "NC",
        ["North Dakota"] = "ND",
        ["Ohio"] = "OH",
        ["Oklahoma"] = "OK",
        ["Oregon"] = "OR",
        ["Pennsylvania"] = "PA",
        ["Rhode Island"] = "RI",
        ["South Carolina"] = "SC",
        ["South Dakota"] = "SD",
        ["Tennessee"] = "TN",
        ["Texas"] = "TX",
        ["Utah"] = "UT",
        ["Vermont"] = "VT",
        ["Virginia"] = "VA",
        ["Washington"] = "WA",
        ["West Virginia"] = "WV",
        ["Wisconsin"] = "WI",
        ["Wyoming"] = "WY",
        // Territories, as the medians table names them.
        ["Guam"] = "GU",
        ["Northern Mariana Islands"] = "MP",
        ["Puerto Rico"] = "PR",
        ["Virgin Islands"] = "VI",
    };

    private static readonly string[] HousingHeader =
    [
        "FIPS Code", "State Initials", "State Name", "County",
        "Non-Mort 1", "Mort 1", "Non-Mort 2", "Mort 2", "Non-Mort 3", "Mort 3",
        "Non-Mort 4", "Mort 4", "Non-Mort 5+", "Mort 5+",
    ];

    // Household sizes 1, 2, 3, 4, 5+ — the columns after FIPS, state, name, county.
    private static readonly int[] NonMortgageColumns = [4, 6, 8, 10, 12];
    private static readonly int[] MortgageRentColumns = [5, 7, 9, 11, 13];

    [GeneratedRegex(@"\A[1-9]\d*(\.\d+)?\z")]
    private static partial Regex DollarFigure();

    [GeneratedRegex(@"Add \$([\d,]+) for each individual")]
    private static partial Regex ExcessPersonFootnote();

    /// <summary>A period's spreadsheet URL.</summary>
    public static string DocUrl(string period, string name) =>
        $"https://www.justice.gov/ust/eo/bapcpa/{period}/bci_data/docs/{name}.xlsx";

    /// <summary>
    /// A spreadsheet number as the repo's two-decimal money string. The UST
    /// files carry whole dollars; anything else is a format change to review.
    /// </summary>
    public static string Money(string value, string where)
    {
        var cleaned = value.Replace(",", "", StringComparison.Ordinal).Replace("$", "", StringComparison.Ordinal).Trim();
        if (!DollarFigure().IsMatch(cleaned))
        {
            throw new IngestException($"{where}: {Exact.Repr(value)} is not a positive dollar figure");
        }

        var dot = cleaned.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? cleaned : cleaned[..dot];
        var frac = dot < 0 ? "" : cleaned[(dot + 1)..];
        return $"{whole}.{(frac + "00")[..2]}";
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // ── Parsers: rows in, payload pieces out ────────────────────────────────

    /// <summary>The medians table: state code → four annual figures, and the one excess-person addition.</summary>
    public static (SortedDictionary<string, string[]> Medians, string Addition) ParseMedians(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var medians = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        var additions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Count == 0 || row[0].Length == 0)
            {
                continue;
            }

            var name = row[0].Trim();
            if (StateCodes.TryGetValue(name, out var code))
            {
                var values = row.Skip(1).Where(v => v.Length > 0).ToList();
                if (values.Count != 4)
                {
                    throw new IngestException($"medians: {name} has {values.Count} columns, not 4");
                }

                medians[code] = [.. values.Select(v => Money(v, $"medians {name}"))];
            }
            else if (ExcessPersonFootnote().Match(name) is { Success: true } footnote)
            {
                additions.Add(Money(footnote.Groups[1].Value, "medians footnote"));
            }
        }

        if (medians.Count != StateCodes.Count)
        {
            var missing = StateCodes.Values.Where(c => !medians.ContainsKey(c)).Order(StringComparer.Ordinal);
            throw new IngestException($"medians: rows missing for [{string.Join(", ", missing.Select(Exact.Repr))}]");
        }

        if (additions.Count != 1)
        {
            throw new IngestException($"medians: excess-person footnotes disagree: {{{string.Join(", ", additions.Select(Exact.Repr))}}}");
        }

        return (medians, additions.Single());
    }

    /// <summary>The National Standards table.</summary>
    public sealed record NationalTable(
        IReadOnlyList<string> Allowances,
        string Additional,
        IReadOnlyList<(string Item, IReadOnlyList<string> Monthly)> Components,
        IReadOnlyList<string> FoodAndClothing,
        string FoodAndClothingExtra,
        IReadOnlyList<string> FivePercentCap,
        string FivePercentCapExtra);

    public static NationalTable ParseNational(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var components = new List<(string, IReadOnlyList<string>)>();
        IReadOnlyList<string>? allowances = null;
        string? additional = null;
        IReadOnlyList<string>? foodAndClothing = null;
        IReadOnlyList<string>? fivePercentCap = null;
        string? foodAndClothingExtra = null;
        string? fivePercentCapExtra = null;
        foreach (var row in rows)
        {
            if (row.Count == 0 || row[0].Length == 0)
            {
                continue;
            }

            var label = row[0].Trim();
            var values = row.Skip(1).Where(v => v.Length > 0).ToList();
            if (label == "Total")
            {
                if (values.Count != 4)
                {
                    throw new IngestException("national: Total row does not have 4 columns");
                }

                allowances = [.. values.Select(v => Money(v, "national Total"))];
            }
            else if (label.StartsWith("For each additional person", StringComparison.Ordinal))
            {
                if (values.Count == 0)
                {
                    throw new IngestException("national: the additional-person row has no figure");
                }

                additional = Money(values[0], "national additional person");
            }
            else if (label == "Food & Clothing" && values.Count == 4)
            {
                foodAndClothing = [.. values.Select(v => Money(v, "national food & clothing"))];
            }
            else if (label == "5% of Food & Clothing" && values.Count == 4)
            {
                // B122A-2 line 30's cap: the optional additional food-and-
                // clothing allowance, published alongside the components.
                fivePercentCap = [.. values.Select(v => Money(v, "national 5% cap"))];
            }
            else if (label == "Food & Clothing" && values.Count == 1)
            {
                foodAndClothingExtra = Money(values[0], "food & clothing extra person");
            }
            else if (label == "5% of Food & Clothing" && values.Count == 1)
            {
                fivePercentCapExtra = Money(values[0], "5% cap extra person");
            }
            else if (values.Count == 4 && label != "Expense")
            {
                components.Add((label, [.. values.Select(v => Money(v, $"national {label}"))]));
            }
        }

        if (allowances is null || additional is null || components.Count == 0
            || foodAndClothing is null || fivePercentCap is null
            || foodAndClothingExtra is null || fivePercentCapExtra is null)
        {
            throw new IngestException("national: table shape changed — review the XLSX");
        }

        return new NationalTable(allowances, additional, components, foodAndClothing, foodAndClothingExtra, fivePercentCap, fivePercentCapExtra);
    }

    /// <summary>The out-of-pocket health care table: per person, under 65 and 65 and older.</summary>
    public static (string Under65, string Over65) ParseOopHealthcare(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        string? under65 = null;
        string? over65 = null;
        foreach (var row in rows)
        {
            var cells = row.Where(v => v.Length > 0).ToList();
            if (cells.Count == 2 && cells[0] == "Under 65")
            {
                under65 = Money(cells[1], "oop under 65");
            }

            if (cells.Count == 2 && cells[0] == "65 and Older")
            {
                over65 = Money(cells[1], "oop 65 and older");
            }
        }

        if (under65 is null || over65 is null)
        {
            throw new IngestException("oop healthcare: table shape changed — review the XLSX");
        }

        return (under65, over65);
    }

    /// <summary>Housing and utilities by county, for the launch states only, in the sheet's row order.</summary>
    public static IReadOnlyDictionary<string, List<JsonObject>> ParseHousing(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var header = rows.Count > 0 ? rows[0] : [];
        if (!header.Take(HousingHeader.Length).Select(h => h.Trim()).SequenceEqual(HousingHeader))
        {
            throw new IngestException($"housing: header changed — review the XLSX: [{string.Join(", ", header.Select(Exact.Repr))}]");
        }

        var housing = LaunchStates.ToDictionary(code => code, _ => new List<JsonObject>(), StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count < 14 || !housing.TryGetValue(row[1], out var counties))
            {
                continue;
            }

            var where = $"housing {row[1]} {row[3]}";
            counties.Add(new JsonObject
            {
                ["fips"] = row[0],
                ["county"] = row[3],
                ["non_mortgage"] = Strings(NonMortgageColumns.Select(i => Money(row[i], where))),
                ["mortgage_rent"] = Strings(MortgageRentColumns.Select(i => Money(row[i], where))),
            });
        }

        foreach (var (code, counties) in housing)
        {
            if (counties.Count == 0)
            {
                throw new IngestException($"housing: no counties parsed for {code}");
            }
        }

        return housing;
    }

    /// <summary>The transportation tables.</summary>
    public sealed record TransportationTable(
        string PublicTransit,
        JsonObject Ownership,
        IReadOnlyDictionary<string, JsonObject> RegionCosts,
        IReadOnlyDictionary<string, JsonObject> MsaCosts);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Region keys are lower-case in the payload (\"south\").")]
    public static TransportationTable ParseTransportation(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        string? publicTransit = null;
        JsonObject? ownership = null;
        var regionCosts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var msaCosts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var section = "";
        foreach (var row in rows.Select(r => r.Where(c => c.Length > 0).ToList()))
        {
            if (row.Count == 0)
            {
                continue;
            }

            var label = row[0].Trim();
            if (label is "Public Transportation" or "Ownership Costs" or "Operating Costs")
            {
                section = label;
                continue;
            }

            if (section == "Public Transportation" && label == "National")
            {
                publicTransit = Money(Cell(row, 1, "transportation public transit"), "transportation public transit");
            }
            else if (section == "Ownership Costs" && label == "National")
            {
                ownership = new JsonObject
                {
                    ["one_car"] = Money(Cell(row, 1, "ownership one car"), "ownership one car"),
                    ["two_cars"] = Money(Cell(row, 2, "ownership two cars"), "ownership two cars"),
                };
            }
            else if (section == "Operating Costs" && row.Count == 3 && label != "One Car")
            {
                var costs = new JsonObject
                {
                    ["one_car"] = Money(row[1], $"operating {label}"),
                    ["two_cars"] = Money(row[2], $"operating {label}"),
                };
                if (label.EndsWith("Region", StringComparison.Ordinal))
                {
                    regionCosts[label[..^"Region".Length].Trim().ToLowerInvariant()] = costs;
                }
                else
                {
                    msaCosts[label] = costs;
                }
            }
        }

        if (publicTransit is null || ownership is null || regionCosts.Count == 0)
        {
            throw new IngestException("transportation: table shape changed — review the XLSX");
        }

        return new TransportationTable(publicTransit, ownership, regionCosts, msaCosts);
    }

    /// <summary>The Chapter 13 multipliers, district → fraction (sorted), and the sheet's "Effective as of" line.</summary>
    public static (SortedDictionary<string, string> Multipliers, string SourceNote) ParseCh13(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var multipliers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sourceNote = "";
        foreach (var row in rows)
        {
            var cells = row.Where(c => c.Length > 0).ToList();
            if (cells.Count == 1 && cells[0].StartsWith("Effective as of", StringComparison.Ordinal))
            {
                sourceNote = cells[0];
            }

            if (cells.Count != 2 || cells[0] == "JUDICIAL DISTRICT")
            {
                continue;
            }

            var (district, raw) = (cells[0], cells[1]);
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
            {
                continue;
            }

            if (fraction is < 0m or >= 1m)
            {
                throw new IngestException($"ch13 multiplier {district}: {Exact.Repr(raw)} is not a fraction");
            }

            // The XLSX stores binary-float noise (9.2999...E-2); the published
            // figures are tenths of a percent, so round to 4 places (half to
            // even, as Python's Decimal.quantize) and drop trailing zeros.
            multipliers[district] = Math.Round(fraction, 4, MidpointRounding.ToEven).ToString("0.####", CultureInfo.InvariantCulture);
        }

        if (multipliers.Count < 50)
        {
            throw new IngestException(
                $"ch13 multipliers: only {multipliers.Count} districts parsed — the table shape changed; review the XLSX");
        }

        return (multipliers, sourceNote);
    }

    // ── Releases: fetch, parse, assemble ────────────────────────────────────

    public static async Task<UstReleaseFiles> MediansAsync(IngestArgs args, Fetcher fetch)
    {
        var url = DocUrl(args.Period, "median_income");
        var data = await fetch(url).ConfigureAwait(false);
        var (medians, addition) = ParseMedians(Xlsx.ReadSheetRows(data));

        var page = $"https://www.justice.gov/ust/means-testing/{args.Period}";
        var annual = new JsonObject();
        foreach (var (code, row) in medians)
        {
            annual[code] = Strings(row);
        }

        var dataset = new JsonObject
        {
            ["kind"] = "census-median-family-income",
            ["verification"] = "primary",
            ["sources"] = new JsonArray(
                SourceEntry("UST Census median family income (XLSX)", url, args.Accessed),
                SourceEntry(
                    "UST median family income HTML table (same period)",
                    $"https://www.justice.gov/ust/eo/bapcpa/{args.Period}/bci_data/median_income_table.htm",
                    args.Accessed)),
            ["annual_medians"] = annual,
            ["excess_person_annual_addition"] = addition,
            ["notes"] =
                "Annual median family income by state/territory for household "
                + "sizes 1-4 (the table's '1 Earner' column is the one-person "
                + "figure, per the UST's own 122A-1 line 13 usage). Households "
                + "above 4 add the excess-person figure per person — 12x the "
                + "monthly amount in 11 U.S.C. 707(b)(7)(A)(iii), which "
                + "code/dollar-amounts carries. Converted mechanically by "
                + ConvertedBy + " from the UST XLSX (sha256 in the "
                + "manifest).",
        };
        var manifest = ManifestFor(
            MediansSeries, args, url, Sha256(data),
            $"Census Bureau median family income as republished by the UST for cases filed on or after {args.Effective} "
            + $"(period page {page}). Converted mechanically; spot-check the diff against the UST's HTML table before merging.");
        return Release(MediansSeries, args, manifest, dataset);
    }

    public static async Task<UstReleaseFiles> NationalAsync(IngestArgs args, Fetcher fetch)
    {
        var url = DocUrl(args.Period, "national_expense_standards");
        var data = await fetch(url).ConfigureAwait(false);
        var table = ParseNational(Xlsx.ReadSheetRows(data));

        var oopUrl = DocUrl(args.Period, "national_oop_healthcare");
        var oopData = await fetch(oopUrl).ConfigureAwait(false);
        var (under65, over65) = ParseOopHealthcare(Xlsx.ReadSheetRows(oopData));

        var allowances = new JsonObject();
        for (var i = 0; i < table.Allowances.Count; i++)
        {
            allowances[(i + 1).ToString(CultureInfo.InvariantCulture)] = table.Allowances[i];
        }

        var dataset = new JsonObject
        {
            ["kind"] = "irs-national-standards",
            ["verification"] = "primary",
            ["sources"] = new JsonArray(
                SourceEntry("UST IRS National Standards (XLSX)", url, args.Accessed),
                SourceEntry("UST IRS out-of-pocket health care standards (XLSX)", oopUrl, args.Accessed)),
            ["monthly_allowances"] = allowances,
            ["each_additional_person"] = table.Additional,
            ["components"] = new JsonArray([.. table.Components.Select(c => (JsonNode)new JsonObject { ["item"] = c.Item, ["monthly"] = Strings(c.Monthly) })]),
            ["food_and_clothing"] = Strings(table.FoodAndClothing),
            ["food_and_clothing_each_additional_person"] = table.FoodAndClothingExtra,
            ["additional_food_clothing_cap"] = Strings(table.FivePercentCap),
            ["additional_food_clothing_cap_each_additional_person"] = table.FivePercentCapExtra,
            ["oop_healthcare"] = new JsonObject { ["under_65"] = under65, ["65_and_older"] = over65 },
            ["notes"] =
                "IRS National Standards for food, clothing and other items "
                + "(122A-2 line 6) by household size 1-4 plus a per-person "
                + "addition above 4, with the component lines as published; the "
                + "combined food-and-clothing figure and its published 5% cap "
                + "(line 30's optional additional allowance); and the "
                + "out-of-pocket health care allowance per person (line 7), "
                + "under 65 / 65 and older. Converted mechanically by "
                + ConvertedBy + " (sha256s: manifest and sources).",
        };
        var manifest = ManifestFor(
            NationalSeries, args, url, Sha256(data),
            $"IRS National Standards as republished by the UST for cases filed on or after {args.Effective}; "
            + $"includes the out-of-pocket health care table ({oopUrl}, sha256 {Sha256(oopData)}). "
            + "Converted mechanically; spot-check the diff against the UST's HTML tables before merging.");
        return Release(NationalSeries, args, manifest, dataset);
    }

    public static async Task<UstReleaseFiles> LocalAsync(IngestArgs args, Fetcher fetch)
    {
        var housingUrl = DocUrl(args.Period, "housing_util_standards_FIPS");
        var housingData = await fetch(housingUrl).ConfigureAwait(false);
        var housing = ParseHousing(Xlsx.ReadSheetRows(housingData)); // sheet 1 is the flat all-county table

        var transportUrl = DocUrl(args.Period, "transportation_standards");
        var transportData = await fetch(transportUrl).ConfigureAwait(false);
        var transport = ParseTransportation(Xlsx.ReadSheetRows(transportData));

        // The MSA county definitions live only on the UST's per-region HTML
        // pages; they are curated by hand into --msa-file. Shape:
        //   { "<msa name as the spreadsheet spells it>":
        //       {"state": "FL", "counties": ["Broward", ...]}, ... }
        if (args.MsaFile is null)
        {
            throw new IngestException("local: --msa-file is required");
        }

        var msaDefinitions = JsonNode.Parse(await File.ReadAllTextAsync(args.MsaFile).ConfigureAwait(false)) as JsonObject
            ?? throw new IngestException($"{args.MsaFile}: not a JSON object");
        var msas = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, definition) in msaDefinitions)
        {
            if (!transport.MsaCosts.TryGetValue(name, out var costs))
            {
                throw new IngestException($"msa-file names {Exact.Repr(name)}, not in the spreadsheet");
            }

            var state = (string?)definition?["state"];
            if (state is null || !LaunchStates.Contains(state))
            {
                throw new IngestException($"msa-file {Exact.Repr(name)} is outside the launch states");
            }

            msas[name] = new JsonObject
            {
                ["state"] = state,
                ["counties"] = definition!["counties"]?.DeepClone(),
                ["one_car"] = costs["one_car"]!.DeepClone(),
                ["two_cars"] = costs["two_cars"]!.DeepClone(),
            };
        }

        if (!transport.RegionCosts.TryGetValue("south", out var south))
        {
            throw new IngestException("transportation: no South region row");
        }

        var housingJson = new JsonObject();
        foreach (var code in housing.Keys.Order(StringComparer.Ordinal))
        {
            housingJson[code] = new JsonArray([.. housing[code]]);
        }

        var regions = new JsonObject();
        foreach (var code in LaunchStates)
        {
            regions[code] = "south";
        }

        var msaJson = new JsonObject();
        foreach (var (name, msa) in msas)
        {
            msaJson[name] = msa;
        }

        var dataset = new JsonObject
        {
            ["kind"] = "irs-local-standards",
            ["verification"] = "primary",
            ["sources"] = new JsonArray(
                SourceEntry("UST IRS housing and utilities standards by county (XLSX)", housingUrl, args.Accessed),
                SourceEntry("UST IRS transportation standards (XLSX)", transportUrl, args.Accessed),
                SourceEntry(
                    "UST South region transportation page (MSA county definitions)",
                    $"https://www.justice.gov/ust/eo/bapcpa/{args.Period}/bci_data/IRS_Trans_Exp_Stds_SO.htm",
                    args.Accessed)),
            ["states"] = Strings(LaunchStates),
            ["housing_utilities"] = housingJson,
            ["transportation"] = new JsonObject
            {
                ["public_transportation_national"] = transport.PublicTransit,
                ["ownership_costs"] = transport.Ownership.DeepClone(),
                ["operating_costs_region"] = regions,
                ["operating_costs"] = new JsonObject { ["south"] = south.DeepClone() },
                ["operating_costs_msa"] = msaJson,
            },
            ["notes"] =
                "IRS Local Standards as the UST republishes them, scoped to the "
                + "launch states (ADR 0017): housing/utilities insurance-and-"
                + "operating (non_mortgage) and mortgage/rent allowances per county "
                + "for household sizes 1,2,3,4,5+ (122A-2 lines 8-9), and the "
                + "transportation tables (lines 11-14) — national public transit, "
                + "national per-vehicle ownership, and operating costs by Census "
                + "region with MSA overrides whose county lists the UST publishes "
                + "only on its region HTML pages. All three launch states are in "
                + "the South region. Converted mechanically by "
                + ConvertedBy + ".",
        };
        var manifest = ManifestFor(
            LocalSeries, args, housingUrl, Sha256(housingData),
            "IRS Local Standards (housing/utilities by county + transportation) as republished by the UST for cases "
            + $"filed on or after {args.Effective}, scoped to the launch states FL/GA/TX (ADR 0017). "
            + $"Transportation XLSX: {transportUrl} (sha256 {Sha256(transportData)}). "
            + "Converted mechanically; spot-check the diff against the UST's HTML tables before merging.");
        return Release(LocalSeries, args, manifest, dataset);
    }

    public static async Task<UstReleaseFiles> Ch13MultipliersAsync(IngestArgs args, Fetcher fetch)
    {
        var url = DocUrl(args.Period, "ch13_exp_mult");
        var data = await fetch(url).ConfigureAwait(false);
        var (multipliers, sourceNote) = ParseCh13(Xlsx.ReadSheetRows(data));

        var multipliersJson = new JsonObject();
        foreach (var (district, fraction) in multipliers)
        {
            multipliersJson[district] = fraction;
        }

        var dataset = new JsonObject
        {
            ["kind"] = "ch13-admin-multipliers",
            ["verification"] = "primary",
            ["sources"] = new JsonArray(
                SourceEntry("UST Chapter 13 administrative expense multipliers (XLSX)", url, args.Accessed)),
            ["multipliers"] = multipliersJson,
            ["notes"] =
                "Schedule of actual administrative expenses of administering a "
                + "Chapter 13 plan, per judicial district, as fractions "
                + "(§ 707(b)(2)(A)(ii)(III); B122A-2 line 36 multiplies the "
                + "projected plan payment by this figure). District names are the "
                + $"UST's own ('Middle Florida'). {sourceNote}. Converted "
                + "mechanically by " + ConvertedBy + ".",
        };
        var manifest = ManifestFor(
            Ch13Series, args, url, Sha256(data),
            $"Chapter 13 administrative expense multipliers as published by the UST for cases filed on or after {args.Effective}. "
            + "Converted mechanically; spot-check the diff against the UST's HTML table before merging.");
        return Release(Ch13Series, args, manifest, dataset);
    }

    // ── Writing: validate through the engine, then write ───────────────────

    /// <summary>
    /// Validates the release as the engine will load it — the whole UST
    /// registry under <paramref name="outRoot"/> (over the shipped one, for a
    /// series the out-root does not hold) plus this release, through
    /// <see cref="UstData.LoadRegistry"/> — and only then writes it.
    /// Releases are append-only: an existing directory is refused.
    /// </summary>
    public static string Write(string outRoot, UstReleaseFiles release)
    {
        var releaseDir = Path.Combine(outRoot, release.SeriesId, release.DirectoryName);
        if (Directory.Exists(releaseDir))
        {
            throw new IngestException(
                $"{releaseDir} already exists — releases are append-only; a correction is a new directory with the next +sequence");
        }

        var manifestText = PythonJson.Dumps(release.Manifest);
        var datasetText = PythonJson.Dumps(release.Dataset);
        var files = RegistryTree.Shipped.Files
            .Where(file => file.Key.StartsWith("ust/", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var onDisk = Path.Combine(outRoot, "ust");
        if (Directory.Exists(onDisk))
        {
            foreach (var path in Directory.EnumerateFiles(onDisk, "*.json", SearchOption.AllDirectories))
            {
                files[Path.GetRelativePath(outRoot, path).Replace('\\', '/')] = File.ReadAllText(path);
            }
        }

        var prefix = $"{release.SeriesId}/{release.DirectoryName}/";
        files[prefix + "manifest.json"] = manifestText;
        files[prefix + "dataset.json"] = datasetText;
        try
        {
            UstData.LoadRegistry(new RegistryTree(files));
        }
        catch (MalformedReleaseException error)
        {
            throw new IngestException($"the engine refuses this release, so it was not written: {error.Message}", error);
        }

        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "manifest.json"), manifestText);
        File.WriteAllText(Path.Combine(releaseDir, "dataset.json"), datasetText);
        return releaseDir;
    }

    private static UstReleaseFiles Release(string seriesId, IngestArgs args, JsonObject manifest, JsonObject dataset) =>
        new(seriesId, args.Sequence == 1 ? args.Effective : $"{args.Effective}+{args.Sequence}", manifest, dataset);

    private static JsonObject ManifestFor(string seriesId, IngestArgs args, string url, string sha256, string notes) => new()
    {
        ["series_id"] = seriesId,
        ["effective_date"] = args.Effective,
        ["sequence"] = args.Sequence,
        ["source"] = new JsonObject { ["url"] = url, ["published"] = null, ["sha256"] = sha256 },
        ["notes"] = notes,
    };

    private static JsonObject SourceEntry(string title, string url, string accessed) => new()
    {
        ["title"] = title,
        ["url"] = url,
        ["accessed"] = accessed,
    };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    private static string Cell(List<string> row, int index, string where) =>
        index < row.Count ? row[index] : throw new IngestException($"{where}: the row has no column {index}");
}
