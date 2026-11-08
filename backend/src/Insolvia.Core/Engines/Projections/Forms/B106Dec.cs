using Insolvia.Core.Debtors;
using Insolvia.Core.Petitions;
using static Insolvia.Engines.Projections.ProjectionHelpers;

namespace Insolvia.Engines.Projections.Forms;

/// <summary>
/// B106Dec @ 2015-12-01 — the schedules declaration (from
/// <c>form_projections/b106dec.py</c>): signature dates, and whether a paid
/// non-attorney preparer helped.
/// </summary>
public static class B106Dec
{
    /// <summary>The values for <c>form/b106dec@2015-12-01</c>.</summary>
    public static FieldValues Project(FormRelease release, CaseFile caseFile)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(caseFile);
        var values = new FieldValues();
        Caption(caseFile, values);

        var preparer = caseFile.FilingProfessionals.FirstOrDefault(p => p.Role == FilingProfessionalRole.BankruptcyPetitionPreparer);
        values["paid_nonattorney_preparer"] = YesNo(release, "paid_nonattorney_preparer", preparer is not null);
        if (preparer is not null && FullName(preparer.Name) is { } name)
        {
            values["preparer_name"] = new TextFill(name);
        }

        foreach (var (role, fieldId) in new[] { (FilingRole.Debtor1, "debtor1_signature_date"), (FilingRole.Debtor2, "debtor2_signature_date") })
        {
            if (caseFile.Debtor(role)?.Body.SignedAt is { } signed)
            {
                values[fieldId] = new TextFill(FormatDate(signed));
            }
        }

        return values;
    }
}
