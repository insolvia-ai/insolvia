"""B2030 @ 2025-12-01 (revision 12/25) — the attorney's compensation
disclosure (§ 329(a), Rule 2016(b)).

The Director's Form is published FLAT, so every value here lands on an
overlay box (core/form_templates.OverlayBox) the fill engine draws rather
than a widget it sets; the mapping is otherwise the same shape as any
other. It reads the ATTORNEY `filing_professional` (the first with
`role == "attorney"` — a petition preparer files Form 119, not this) and
the compensation fields issue #351 added to that record:

- lines 1's three amounts: `compensation_agreed`, `compensation_received`,
  and the balance due DERIVED as their difference (the model refuses to
  store arithmetic) — a received amount above the agreed one is an error,
  not a negative balance;
- items 2 and 3: `compensation_source_paid` / `compensation_source_to_be_paid`
  (`debtor` or `other`, with the `_other` narrative on the specify line);
- item 4: `compensation_shared` picks the "have not agreed" or "have agreed"
  box — an unanswered question ticks neither;
- 5e and 6: `services_other` / `services_excluded`, wrapped across the
  form's blank lines by Helvetica's own metrics, overflow being an error;
- the certification: `signature_date` and `firm_name`; the signature line
  itself stays blank, as every signature line does.

The caption splits `case.district` around "District of" onto the two
printed blanks, prints both debtors under "In re", and the chapter.
Whether the form files at all — only when an attorney is on the case — is
packet assembly's call (`packet_form_series`).
"""

from __future__ import annotations

import re
from decimal import Decimal
from typing import Final

from insolvia_core.petitions import FilingProfessionalBody

from ..form_fill import Check, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    format_date,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    wrap_width,
)

_DISTRICT_RE: Final = re.compile(r"^(.*?)\s*District of\s+(.+)$", re.IGNORECASE)


def attorney_of(case_file: CaseFile) -> FilingProfessionalBody | None:
    """The filing professional this form is about — the first attorney."""
    return next(
        (p for p in case_file.filing_professionals if p.role == "attorney"), None
    )


def _source_boxes(
    values: FieldValues,
    source: str | None,
    other: str | None,
    *,
    debtor_box: str,
    other_box: str,
    specify: str,
) -> None:
    if source == "debtor":
        values[debtor_box] = Check()
    elif source == "other":
        values[other_box] = Check()
        if other:
            values[specify] = Text(other)


def project_b2030_1225(release: FormRelease, case_file: CaseFile) -> FieldValues:
    problems: list[str] = []
    values: FieldValues = {}
    case = case_file.case

    match = _DISTRICT_RE.match(case.district)
    parts = (match.group(1), match.group(2)) if match else (case.district, None)
    for index, part in enumerate(parts):
        row_fill(
            release, values, "caption.district", index, text_or_none(part), problems
        )
    for role, field_id in (
        ("debtor_1", "caption.debtor1_name"),
        ("debtor_2", "caption.debtor2_name"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and (name := full_name(debtor.name)):
            values[field_id] = Text(name)
    values["caption.chapter"] = Text(str(case.chapter))

    attorney = attorney_of(case_file)
    if attorney is None:
        if problems:
            raise FormProjectionError(sorted(problems))
        return values

    agreed = attorney.compensation_agreed
    received = attorney.compensation_received
    if agreed is not None:
        values["line_1_fee_agreed"] = Text(format_money(agreed))
    if received is not None:
        values["line_1_fee_received"] = Text(format_money(received))
    if agreed is not None and received is not None:
        balance = Decimal(agreed) - Decimal(received)
        if balance < 0:
            problems.append(
                "line_1_balance_due: the amount received exceeds the amount "
                "agreed — the form cannot print a negative balance"
            )
        else:
            values["line_1_balance_due"] = Text(format_money(balance))

    _source_boxes(
        values,
        attorney.compensation_source_paid,
        attorney.compensation_source_paid_other,
        debtor_box="line_2_source_paid_debtor",
        other_box="line_2_source_paid_other",
        specify="line_2_source_paid_other_specify",
    )
    _source_boxes(
        values,
        attorney.compensation_source_to_be_paid,
        attorney.compensation_source_to_be_paid_other,
        debtor_box="line_3_source_to_be_paid_debtor",
        other_box="line_3_source_to_be_paid_other",
        specify="line_3_source_to_be_paid_other_specify",
    )
    if attorney.compensation_shared is False:
        values["line_4_not_shared"] = Check()
    elif attorney.compensation_shared is True:
        values["line_4_shared"] = Check()

    for field_id, narrative in (
        ("line_5e_other_services", attorney.services_other),
        ("line_6_excluded_services", attorney.services_excluded),
    ):
        if not narrative:
            continue
        spec = release.field(field_id)
        lines = wrap_width(
            narrative,
            points=min(release.boxes[name].w for name in spec.pdf_names),
            lines=len(spec.pdf_names),
            where=field_id,
            problems=problems,
        )
        for index, line in enumerate(lines):
            row_fill(release, values, field_id, index, Text(line), problems)

    if attorney.signature_date:
        values["certification.date"] = Text(format_date(attorney.signature_date))
    if attorney.firm_name:
        values["certification.firm_name"] = Text(attorney.firm_name)

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
