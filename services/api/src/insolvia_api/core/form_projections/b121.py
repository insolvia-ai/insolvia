"""B121 @ 2015-12-01 (revision 12/15) — the Statement About Your Social
Security Numbers.

The one form in the packet whose purpose is the FULL tax identifier, and
the one whose value the store does not hold: `insolvia_core.debtors.
parse_debtor` refuses `tax_id` outright until field-level encryption
exists, and B101's projection leaves its last-four boxes blank for the same
reason. So this mapping prints everything else — the caption, each debtor's
name in its three boxes, the signature dates — and leaves lines 2 and 3
blank, exactly as B101 does, until that work lands. When it does, the
number must reach this projection through the SAME audited read that will
serve any other full-value use (case-data-model.md: "the full value behind
an explicit read that writes an audit record"), never through a bypass:
packet assembly already records its read of the case file against the
accepting principal, and the tax-id read joins that trail as its own row.

Two things this mapping deliberately never infers: the "You do not have a
Social Security number" / "You do not have an ITIN" boxes are the debtor's
explicit statement, not the absence of a stored number, so a blank never
becomes a tick; and the second row of each line ("all numbers you have
used") stays blank because the model holds one tax id per debtor.

The form is submitted to the court separately from the public case file;
the packet carries it and the clerk keeps it out of the public record.
"""

from __future__ import annotations

from ..form_fill import Text
from ..form_templates import FormRelease
from .shared import CaseFile, FieldValues, format_date


def project_b121_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}

    values["caption.district"] = Text(case_file.case.district)
    for role, prefix in (("debtor_1", "debtor1"), ("debtor_2", "debtor2")):
        debtor = case_file.debtor(role)
        if debtor is None:
            continue
        for part, field_id in (
            ("given", f"line_1_{prefix}_first_name"),
            ("middle", f"line_1_{prefix}_middle_name"),
            ("surname", f"line_1_{prefix}_last_name"),
        ):
            value = getattr(debtor.name, part, None)
            if value:
                values[field_id] = Text(value)
        if debtor.signed_at:
            values[f"{prefix}_signature_date"] = Text(format_date(debtor.signed_at))

    return values
