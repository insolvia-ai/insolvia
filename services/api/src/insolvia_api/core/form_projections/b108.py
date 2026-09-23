"""B108 @ 2015-12-01 (revision 12/15) — the Statement of Intention's mapping.

§ 521(a)(2)'s statement, in two lists the schedules already hold the facts
for (issue #351):

- **Part 1** is Schedule D's secured claims, one printed row each in the
  same creation order 106D rows them: the creditor's name resolves through
  `creditor_id`, the collateral's description through `core/liens.py` —
  the typed override, else the linked asset's description, exactly what
  Column B of 106D prints — and the four intention boxes come from the
  claim's own `intention` (`insolvia_core.claims.INTENTIONS`), spelled
  through the spec's `maps_to_value` annotations. "Did you claim the
  property as exempt on Schedule C?" is DERIVED: whether any exemption
  record names the asset the claim is secured by. The explanation lines
  print only under "Retain the property and [explain]" — that is the one
  option the form gives them to; a note stored beside another intention is
  the preparer's own and has no printed home here.
- **Part 2** is the Schedule G rows the preparer flagged
  `list_on_statement_of_intention` (a lease is a Statement item only once
  someone says so — a real-estate lease never is), each with its
  assume/reject answer from `contract_lease.intention`.

Four Part 1 rows and seven Part 2 rows are what the form prints; the
instruction is "attach a separate sheet", and a case past those rows is an
error here, as on every other schedule, until continuation sheets exist.
Whether the form files at all — it is required only when either list has a
row — is packet assembly's call (`packet_form_series`).
"""

from __future__ import annotations

from typing import Final

from ..form_fill import Text
from ..form_templates import FormRelease
from ..liens import derive_liens
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    canonical_option,
    format_date,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)

# The explanation prints on two lines of very different length: the first
# sits at the end of "Retain the property and [explain]:" (about ten
# characters), the second is the full-width line beneath it.
_EXPLAIN_FIRST: Final = 10
_EXPLAIN_SECOND: Final = 34


def _explain_lines(value: str) -> tuple[str, str] | None:
    """Split an explanation across the short first line and the full second
    line, or None when it cannot fit — greedy by word, so a word longer than
    the first line simply starts the second."""
    words = value.split()
    first: list[str] = []
    while words and len(" ".join([*first, words[0]])) <= _EXPLAIN_FIRST:
        first.append(words.pop(0))
    second = " ".join(words)
    if len(second) > _EXPLAIN_SECOND:
        return None
    return " ".join(first), second


def project_b108_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    # Part 1 — the secured claims, in Schedule D's row order.
    liens = derive_liens(case_file)
    secured = [
        (claim_id, body)
        for claim_id, body in case_file.claims
        if body.claim_class == "secured"
    ]
    intention_names = release.field("line_1_intention").pdf_names
    for index, (claim_id, claim) in enumerate(secured):
        creditor = case_file.creditor(claim.creditor_id)
        row_fill(
            release,
            values,
            "line_1_creditor_name",
            index,
            text_or_none(creditor.name if creditor is not None else None),
            problems,
        )
        lien = liens.claim(claim_id)
        row_fill(
            release,
            values,
            "line_1_property_description",
            index,
            text_or_none(lien.collateral_description if lien is not None else None),
            problems,
        )
        if claim.intention is not None and index < len(intention_names):
            row_fill(
                release,
                values,
                "line_1_intention",
                index,
                canonical_option(
                    release, "line_1_intention", intention_names[index], claim.intention
                ),
                problems,
            )
            if claim.intention == "retain_other" and claim.intention_explanation:
                lines = _explain_lines(claim.intention_explanation)
                if lines is None:
                    problems.append(
                        f"line_1_intention_explanation: row {index + 1}'s "
                        "explanation does not fit the two printed lines"
                    )
                else:
                    for field_id, line in (
                        ("line_1_intention_explanation_line1", lines[0]),
                        ("line_1_intention_explanation_line2", lines[1]),
                    ):
                        row_fill(
                            release,
                            values,
                            field_id,
                            index,
                            text_or_none(line),
                            problems,
                        )
        elif claim.intention is not None:
            problems.append(
                f"line_1_intention: row {index + 1} does not exist — the form "
                f"prints {len(intention_names)} rows"
            )
        claimed_exempt = claim.asset_id is not None and any(
            exemption.asset_id == claim.asset_id for exemption in case_file.exemptions
        )
        row_fill(
            release,
            values,
            "line_1_claimed_exempt",
            index,
            yes_no(release, "line_1_claimed_exempt", claimed_exempt),
            problems,
        )

    # Part 2 — the leases flagged for the statement, in Schedule G's order.
    leases = [
        body
        for _, body in case_file.contract_leases
        if body.list_on_statement_of_intention
    ]
    assumed_names = release.field("line_2_assumed").pdf_names
    for index, lease in enumerate(leases):
        row_fill(
            release,
            values,
            "line_2_lessor_name",
            index,
            text_or_none(lease.counterparty_name),
            problems,
        )
        row_fill(
            release,
            values,
            "line_2_property_description",
            index,
            text_or_none(lease.description),
            problems,
        )
        if lease.intention is not None and index < len(assumed_names):
            row_fill(
                release,
                values,
                "line_2_assumed",
                index,
                canonical_option(
                    release, "line_2_assumed", assumed_names[index], lease.intention
                ),
                problems,
            )
        elif lease.intention is not None:
            problems.append(
                f"line_2_assumed: row {index + 1} does not exist — the form "
                f"prints {len(assumed_names)} rows"
            )

    for role, field_id in (
        ("debtor_1", "debtor1_signature_date"),
        ("debtor_2", "debtor2_signature_date"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at:
            values[field_id] = Text(format_date(debtor.signed_at))

    if problems:
        raise FormProjectionError(sorted(set(problems)))
    return values
