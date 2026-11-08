using Insolvia.Core.Claims;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106G @ 2015-12-01 — Schedule G's mapping (from <c>form_projections/b106g.py</c>):
/// contracts and leases in creation order onto the printed rows.
/// </summary>
public static class B106G
{
    /// <summary>The values for <c>form/b106g@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        var contracts = caseFile.ContractLeases.Select(pair => pair.Body).ToList();
        values["line_1_any_contracts"] = YesNo(release, "line_1_any_contracts", contracts.Count > 0);
        for (var index = 0; index < contracts.Count; index++)
        {
            var contract = contracts[index];
            RowFill(release, values, "line_2_counterparty_name", index, TextOrNone(contract.CounterpartyName), problems);
            foreach (var (fieldId, part) in new[]
            {
                ("line_2_counterparty_street", contract.CounterpartyAddress.Line1),
                ("line_2_counterparty_city", contract.CounterpartyAddress.City),
                ("line_2_counterparty_state", contract.CounterpartyAddress.State),
                ("line_2_counterparty_zip", contract.CounterpartyAddress.PostalCode),
            })
            {
                RowFill(release, values, fieldId, index, TextOrNone(part), problems);
            }

            RowFill(release, values, "line_2_contract_description", index, TextOrNone(contract.Description), problems);
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }
}

/// <summary>
/// B106H @ 2015-12-01 — Schedule H's mapping (from <c>form_projections/b106h.py</c>):
/// codebtors onto the printed rows, the which-schedule boxes derived from
/// what each codebtor's links resolve to; line 2 prints one community-property block.
/// </summary>
public static class B106H
{
    /// <summary>The values for <c>form/b106h@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var problems = new List<string>();
        var values = new FieldValues();
        Caption(caseFile, values);

        values["line_1_any_codebtors"] = YesNo(release, "line_1_any_codebtors", caseFile.Codebtors.Count > 0);

        var members = caseFile.CommunityHouseholdMembers;
        values["line_2_lived_in_community_state"] = YesNo(release, "line_2_lived_in_community_state", members.Count > 0);
        if (members.Count > 1)
        {
            problems.Add($"line_2: the form prints one community-property block; the case holds {members.Count} household members");
        }

        if (members.Count > 0)
        {
            var member = members[0];
            if (!string.IsNullOrEmpty(member.CommunityState))
            {
                values["line_2_community_state"] = new TextFill(member.CommunityState);
            }

            if (member.LivedWithDebtor is { } lived)
            {
                values["line_2_spouse_lived_with_you"] = YesNo(release, "line_2_spouse_lived_with_you", lived);
            }

            if (!string.IsNullOrEmpty(member.Name))
            {
                values["line_2_spouse_name"] = new TextFill(member.Name);
            }

            AddressFills(values, member.Address, "line_2_spouse_street", null, "line_2_spouse_city", "line_2_spouse_state", "line_2_spouse_zip");
        }

        var claimClasses = new Dictionary<string, ClaimClass?>(StringComparer.Ordinal);
        foreach (var (id, body) in caseFile.Claims)
        {
            claimClasses[id] = body.ClaimClass;
        }

        var contractIds = caseFile.ContractLeases.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < caseFile.Codebtors.Count; index++)
        {
            var codebtor = caseFile.Codebtors[index];
            RowFill(release, values, "line_3_codebtor_name", index, TextOrNone(codebtor.Name), problems);
            foreach (var (fieldId, part) in new[]
            {
                ("line_3_codebtor_street", codebtor.Address.Line1),
                ("line_3_codebtor_city", codebtor.Address.City),
                ("line_3_codebtor_state", codebtor.Address.State),
                ("line_3_codebtor_zip", codebtor.Address.PostalCode),
            })
            {
                RowFill(release, values, fieldId, index, TextOrNone(part), problems);
            }

            var linked = codebtor.ClaimIds.Select(claimId => claimClasses.GetValueOrDefault(claimId)).ToList();
            if (linked.Contains(ClaimClass.Secured))
            {
                RowFill(release, values, "line_3_schedule_d_applies", index, new CheckFill(), problems);
            }

            if (linked.Contains(ClaimClass.PriorityUnsecured) || linked.Contains(ClaimClass.NonpriorityUnsecured))
            {
                RowFill(release, values, "line_3_schedule_ef_applies", index, new CheckFill(), problems);
            }

            if (codebtor.ContractLeaseIds.Any(contractIds.Contains))
            {
                RowFill(release, values, "line_3_schedule_g_applies", index, new CheckFill(), problems);
            }
        }

        if (problems.Count > 0)
        {
            throw new FormProjectionException(Sorted(problems));
        }

        return values;
    }
}
