"""B121 @ 2015-12-01 (revision 12/15) — the Statement About Your Social
Security Numbers.

The one form in the packet whose purpose is the FULL tax identifier. The
number reaches this projection through `CaseFile.tax_ids`, which only the
logged full-value read fills (insolvia_core.tax_ids.read_tax_id — a
`taxid.read` row naming who, which case, which debtor and `b121`), and
which packet assembly and the single-form preview perform only when this
form is being rendered. There is no other way in: the Debtor record carries
the kind and the last four, never the digits, so a CaseFile built without
the read prints lines 2-3 blank rather than wrong — which is also what the
review worker's re-assembly and every other caller that never asked get.

Which line prints follows the KIND: an SSN lands on line 2 as
`000-00-0000`; an ITIN on line 3 as the ten characters after the box's
pre-printed 9 (`tax_ids.format_for_b121` owns both spellings, from
forms/specs/b121.json). Each line's SECOND row ("all numbers you have
used") stays blank because the model holds one tax id per debtor.

Two things this mapping deliberately never infers: the "You do not have a
Social Security number" / "You do not have an ITIN" boxes are the debtor's
explicit statement, not the absence of a stored number, so a blank never
becomes a tick.

The form is submitted to the court separately from the public case file;
the packet carries it and the clerk keeps it out of the public record.
"""

from __future__ import annotations

from insolvia_core.tax_ids import format_for_b121

from ..form_fill import Text
from ..form_templates import FormRelease
from .shared import CaseFile, FieldValues, format_date, rows


def project_b121_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
    values: FieldValues = {}
    problems: list[str] = []

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

        digits = case_file.tax_ids.get(role)
        if debtor.tax_id is not None and digits is not None:
            line = "line_2" if debtor.tax_id.kind == "ssn" else "line_3"
            box = "ssn" if debtor.tax_id.kind == "ssn" else "itin"
            # Row 1 of the two printed rows; row 2 is the "other numbers"
            # row the model has nothing for.
            rows(
                release,
                values,
                f"{line}_{prefix}_{box}",
                [format_for_b121(debtor.tax_id.kind, digits)],
                problems,
            )

    assert not problems  # one value into a two-row field cannot overflow
    return values
