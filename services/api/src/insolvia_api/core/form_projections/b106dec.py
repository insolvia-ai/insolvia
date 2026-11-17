"""B106Dec @ 2015-12-01 (revision 12/15) — the schedules declaration.

Almost the whole form is the debtors' own act: the signature lines stay
wet, and only their dates print, from `debtor.signed_at`. The one derived
answer is whether a paid non-attorney preparer helped — a
`filing_professional` with the bankruptcy-petition-preparer role — whose
printed name fills the box when one exists.
"""

from __future__ import annotations

from ..form_fill import Text
from ..form_templates import FormRelease
from .shared import CaseFile, FieldValues, format_date, full_name, yes_no


def project_b106dec_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)

    preparer = next(
        (
            p
            for p in case_file.filing_professionals
            if p.role == "bankruptcy_petition_preparer"
        ),
        None,
    )
    values["paid_nonattorney_preparer"] = yes_no(
        release, "paid_nonattorney_preparer", preparer is not None
    )
    if preparer is not None and (name := full_name(preparer.name)):
        values["preparer_name"] = Text(name)

    for role, field_id in (
        ("debtor_1", "debtor1_signature_date"),
        ("debtor_2", "debtor2_signature_date"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at:
            values[field_id] = Text(format_date(debtor.signed_at))

    return values
