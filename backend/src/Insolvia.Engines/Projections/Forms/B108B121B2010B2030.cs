using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.Petitions;
using Insolvia.Core.TaxIds;
using Insolvia.Engines.Liens;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B108 @ 2015-12-01 — the Statement of Intention (from <c>form_projections/b108.py</c>):
/// Part 1 the secured claims in Schedule D's order with their intentions,
/// Part 2 the leases flagged for the statement.
/// </summary>
public static class B108
{
    // The explanation prints on a short first line (about ten characters)
    // and a full second line.
    private const int ExplainFirst = 10;
    private const int ExplainSecond = 34;

    /// <summary>The values for <c>form/b108@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var liens = LienEngine.Derive(caseFile);
        var secured = caseFile.Claims.Where(pair => pair.Body.ClaimClass == ClaimClass.Secured).ToList();
        var intentionNames = release.Field("line_1_intention").PdfNames;
        for (var index = 0; index < secured.Count; index++)
        {
            var (claimId, claim) = secured[index];
            RowFill(release, values, "line_1_creditor_name", index, TextOrNone(caseFile.Creditor(claim.CreditorId)?.Name), problems);
            var lien = liens.Claim(claimId);
            RowFill(release, values, "line_1_property_description", index, TextOrNone(lien?.CollateralDescription), problems);
            if (claim.Intention is { } intention && index < intentionNames.Count)
            {
                RowFill(release, values, "line_1_intention", index, CanonicalOption(release, "line_1_intention", intentionNames[index], intention.ToWire()), problems);
                if (intention == ClaimIntention.RetainOther && !string.IsNullOrEmpty(claim.IntentionExplanation))
                {
                    if (ExplainLines(claim.IntentionExplanation) is not { } lines)
                    {
                        problems.Add($"line_1_intention_explanation: row {index + 1}'s explanation does not fit the two printed lines");
                    }
                    else
                    {
                        RowFill(release, values, "line_1_intention_explanation_line1", index, TextOrNone(lines.First), problems);
                        RowFill(release, values, "line_1_intention_explanation_line2", index, TextOrNone(lines.Second), problems);
                    }
                }
            }
            else if (claim.Intention is not null)
            {
                problems.Add($"line_1_intention: row {index + 1} does not exist — the form prints {intentionNames.Count} rows");
            }

            var claimedExempt = claim.AssetId is not null && caseFile.Exemptions.Any(exemption => exemption.AssetId == claim.AssetId);
            RowFill(release, values, "line_1_claimed_exempt", index, YesNo(release, "line_1_claimed_exempt", claimedExempt), problems);
        }

        var leases = caseFile.ContractLeases.Select(pair => pair.Body).Where(body => body.ListOnStatementOfIntention == true).ToList();
        var assumedNames = release.Field("line_2_assumed").PdfNames;
        for (var index = 0; index < leases.Count; index++)
        {
            var lease = leases[index];
            RowFill(release, values, "line_2_lessor_name", index, TextOrNone(lease.CounterpartyName), problems);
            RowFill(release, values, "line_2_property_description", index, TextOrNone(lease.Description), problems);
            if (lease.Intention is { } intention && index < assumedNames.Count)
            {
                RowFill(release, values, "line_2_assumed", index, CanonicalOption(release, "line_2_assumed", assumedNames[index], intention.ToWire()), problems);
            }
            else if (lease.Intention is not null)
            {
                problems.Add($"line_2_assumed: row {index + 1} does not exist — the form prints {assumedNames.Count} rows");
            }
        }

        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "debtor1_signature_date"), (FilingRole.Debtor2, "debtor2_signature_date") })
        {
            if (caseFile.Debtor(role)?.Body.SignedAt is { } signed)
            {
                values[fieldId] = new TextFill(FormatDate(signed));
            }
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems.Distinct(StringComparer.Ordinal)));
        }

        return values;
    }

    /// <summary>The explanation split across the short first line and the full second, or null when it cannot fit.</summary>
    internal static (string First, string Second)? ExplainLines(string value)
    {
        var words = new Queue<string>(PyText.SplitWhitespace(value));
        var first = new List<string>();
        while (words.Count > 0 && PyText.Length(string.Join(' ', first.Append(words.Peek()))) <= ExplainFirst)
        {
            first.Add(words.Dequeue());
        }

        var second = string.Join(' ', words);
        if (PyText.Length(second) > ExplainSecond)
        {
            return null;
        }

        return (string.Join(' ', first), second);
    }
}

/// <summary>
/// B121 @ 2015-12-01 — the Statement About Your Social Security Numbers
/// (from <c>form_projections/b121.py</c>): names, signature dates, and the
/// full number from the logged read, or blank without it.
/// </summary>
public static class B121
{
    /// <summary>The values for <c>form/b121@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        var problems = new List<string>();
        values["caption.district"] = new TextFill(caseFile.Case.District);
        foreach (var (role, prefix) in new[] { (FilingRole.Debtor1, "debtor1"), (FilingRole.Debtor2, "debtor2") })
        {
            var debtor = caseFile.Debtor(role);
            if (debtor is null)
            {
                continue;
            }

            foreach (var (value, fieldId) in new[] { (debtor.Body.Name.Given, $"line_1_{prefix}_first_name"), (debtor.Body.Name.Middle, $"line_1_{prefix}_middle_name"), (debtor.Body.Name.Surname, $"line_1_{prefix}_last_name") })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    values[fieldId] = new TextFill(value);
                }
            }

            if (debtor.Body.SignedAt is { } signed)
            {
                values[$"{prefix}_signature_date"] = new TextFill(FormatDate(signed));
            }

            if (debtor.TaxId is { } taxId && caseFile.TaxIds.TryGetValue(role.ToWire(), out var digits))
            {
                var line = taxId.Kind == TaxIdKind.Ssn ? "line_2" : "line_3";
                var box = taxId.Kind == TaxIdKind.Ssn ? "ssn" : "itin";
                Rows(release, values, $"{line}_{prefix}_{box}", [TaxIdRules.FormatForB121(taxId.Kind, digits)], problems);
            }
        }

        return problems.Count == 0 ? values : throw new InvalidOperationException("one value into a two-row field cannot overflow");
    }
}

/// <summary>B2010 @ 2020-12-01 — the notice to consumer debtors: a flat release with no field to project.</summary>
public static class B2010
{
    /// <summary>Nothing — the notice ships verbatim.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile) => new();
}

/// <summary>
/// B2030 @ 2025-12-01 — the attorney's compensation disclosure (from
/// <c>form_projections/b2030.py</c>), a flat release: the fee, the derived
/// balance, the sources, the sharing answer, and the narratives wrapped by
/// Helvetica width.
/// </summary>
public static partial class B2030
{
    /// <summary>
    /// A district split around "District of" for a caption that prints
    /// <c>__ District of __</c>: <c>Middle District of Florida</c> is
    /// (Middle, Florida); <c>District of Columbia</c> leaves the first blank.
    /// </summary>
    public static (string? Before, string? After) DistrictParts(string district)
    {
        var match = DistrictPattern().Match(district);
        if (!match.Success)
        {
            return (district, null);
        }

        var before = match.Groups[1].Value;
        return (before.Length > 0 ? before : null, match.Groups[2].Value);
    }

    /// <summary>The filing professional the form is about — the first attorney.</summary>
    public static FilingProfessionalBody? AttorneyOf(CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        return caseFile.FilingProfessionals.FirstOrDefault(p => p.Role == FilingProfessionalRole.Attorney);
    }

    /// <summary>The values for <c>form/b2030@2025-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        var (before, after) = DistrictParts(caseFile.Case.District);
        RowFill(release, values, "caption.district", 0, TextOrNone(before), problems);
        RowFill(release, values, "caption.district", 1, TextOrNone(after), problems);
        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "caption.debtor1_name"), (FilingRole.Debtor2, "caption.debtor2_name") })
        {
            if (FullName(caseFile.Debtor(role)) is { } name)
            {
                values[fieldId] = new TextFill(name);
            }
        }

        values["caption.chapter"] = new TextFill(caseFile.Case.Chapter.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var attorney = AttorneyOf(caseFile);
        if (attorney is null)
        {
            return problems.Count > 0 ? throw new FormProjectionException(Sorted(problems)) : values;
        }

        var agreed = attorney.CompensationAgreed;
        var received = attorney.CompensationReceived;
        if (agreed is { } agreedAmount)
        {
            values["line_1_fee_agreed"] = new TextFill(FormatMoney(agreedAmount));
        }

        if (received is { } receivedAmount)
        {
            values["line_1_fee_received"] = new TextFill(FormatMoney(receivedAmount));
        }

        if (agreed is { } a && received is { } r)
        {
            var balance = a - r;
            if (balance < 0)
            {
                problems.Add("line_1_balance_due: the amount received exceeds the amount agreed — the form cannot print a negative balance");
            }
            else
            {
                values["line_1_balance_due"] = new TextFill(FormatMoney(balance));
            }
        }

        SourceBoxes(values, attorney.CompensationSourcePaid, attorney.CompensationSourcePaidOther, "line_2_source_paid_debtor", "line_2_source_paid_other", "line_2_source_paid_other_specify");
        SourceBoxes(values, attorney.CompensationSourceToBePaid, attorney.CompensationSourceToBePaidOther, "line_3_source_to_be_paid_debtor", "line_3_source_to_be_paid_other", "line_3_source_to_be_paid_other_specify");
        if (attorney.CompensationShared == false)
        {
            values["line_4_not_shared"] = new CheckFill();
        }
        else if (attorney.CompensationShared == true)
        {
            values["line_4_shared"] = new CheckFill();
        }

        foreach (var (fieldId, narrative) in new[] { ("line_5e_other_services", attorney.ServicesOther), ("line_6_excluded_services", attorney.ServicesExcluded) })
        {
            if (string.IsNullOrEmpty(narrative))
            {
                continue;
            }

            var spec = release.Field(fieldId);
            var lines = WrapWidth(narrative, spec.PdfNames.Min(name => release.Boxes[name].W), spec.PdfNames.Count, fieldId, problems);
            for (var index = 0; index < lines.Count; index++)
            {
                RowFill(release, values, fieldId, index, new TextFill(lines[index]), problems);
            }
        }

        if (attorney.SignatureDate is { } signed)
        {
            values["certification.date"] = new TextFill(FormatDate(signed));
        }

        if (!string.IsNullOrEmpty(attorney.FirmName))
        {
            values["certification.firm_name"] = new TextFill(attorney.FirmName);
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }

    [GeneratedRegex(@"^(.*?)\s*District of\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistrictPattern();

    private static void SourceBoxes(FieldValues values, CompensationSource? source, string? other, string debtorBox, string otherBox, string specify)
    {
        if (source == CompensationSource.Debtor)
        {
            values[debtorBox] = new CheckFill();
        }
        else if (source == CompensationSource.Other)
        {
            values[otherBox] = new CheckFill();
            if (!string.IsNullOrEmpty(other))
            {
                values[specify] = new TextFill(other);
            }
        }
    }
}
