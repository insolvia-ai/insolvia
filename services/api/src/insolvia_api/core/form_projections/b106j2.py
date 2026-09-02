"""B106J-2 @ 2015-12-01 (revision 12/15) — the second household's Schedule J.

The identical line set as 106J, printed for Debtor 2's separate household —
so the household block IS `b106j.household_block`, run over the
`debtor_2_separate` household row. Line 22 sums this household's expense
lines and carries to 106J line 22b. Without a second household row the form
projects only its caption and line 1's No — packet assembly decides whether
a schedule with nothing to say is filed at all.
"""

from __future__ import annotations

from ..form_fill import Text
from ..form_templates import FormRelease
from .b106j import household_block, household_expense_total, household_row
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    format_money,
    full_name,
    yes_no,
)


def project_b106j2_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    second = household_row(case_file, "debtor_2_separate")
    values["line_1_separate_households"] = yes_no(
        release, "line_1_separate_households", second is not None
    )
    if second is not None:
        household_block(release, values, case_file, second[0], second[1], problems)
        values["line_22_monthly_expenses"] = Text(
            format_money(household_expense_total(case_file, "debtor_2_separate"))
        )

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
