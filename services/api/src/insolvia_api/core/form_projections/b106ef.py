"""B106E/F @ 2015-12-01 (revision 12/15) — Schedule E/F's mapping.

Priority claims onto Part 1's five rows, nonpriority onto Part 2's six,
notice parties from both onto Part 3's seven — each creditor block resolved
through `claim.creditor_id`. Derived, never stored: each priority row's
total claim (priority plus nonpriority amounts) and the whole Part 4
statistical rollup, whose 6e and 6j feed 106Sum lines 3a/3b through
`priority_unsecured_total` / `nonpriority_unsecured_total`.

Part 3's "line used to identify" stays blank (packet assembly owns row
placement); its Part 1/Part 2 radio does fill — the referenced claim's
class answers it, and the projection knows the class.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

from ..claims import ClaimBody
from ..form_fill import Check, Option, Text
from ..form_templates import FormRelease
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    canonical_option,
    claims_of,
    format_date,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)

_PRIORITY_TYPE_BOXES: Final = {
    "domestic_support": "priority.type_domestic_support",
    "tax_and_government": "priority.type_taxes",
    "death_or_injury_while_intoxicated": "priority.type_intoxicated",
    "other": "priority.type_other",
}

_NONPRIORITY_TYPE_BOXES: Final = {
    "student_loan": "nonpriority.type_student_loans",
    "separation_or_divorce": "nonpriority.type_divorce",
    "pension_or_profit_sharing": "nonpriority.type_pension",
    "other": "nonpriority.type_other",
}

_FLAG_FIELDS: Final = ("contingent", "unliquidated", "disputed", "community_debt")


def _total_claim(claim: ClaimBody) -> Decimal:
    return amount(claim.priority_amount) + amount(claim.nonpriority_amount)


def priority_unsecured_total(case_file: CaseFile) -> Decimal:
    """Line 6e — every priority claim's total. Feeds 106Sum line 3a."""
    return sum(
        (_total_claim(c) for c in claims_of(case_file, "priority_unsecured")),
        Decimal("0"),
    )


def nonpriority_unsecured_total(case_file: CaseFile) -> Decimal:
    """Line 6j — every nonpriority claim's amount. Feeds 106Sum line 3b."""
    return sum(
        (amount(c.amount) for c in claims_of(case_file, "nonpriority_unsecured")),
        Decimal("0"),
    )


def priority_type_total(case_file: CaseFile, wanted: str) -> Decimal:
    """One Part 4 line: priority claims of one type, at their total claim.
    Shared with 106Sum lines 9a-9c."""
    return sum(
        (
            _total_claim(c)
            for c in claims_of(case_file, "priority_unsecured")
            if c.priority_type == wanted
        ),
        Decimal("0"),
    )


def nonpriority_type_total(case_file: CaseFile, wanted: str) -> Decimal:
    """One Part 4 line: nonpriority claims of one type. Shared with 106Sum
    lines 9d-9f."""
    return sum(
        (
            amount(c.amount)
            for c in claims_of(case_file, "nonpriority_unsecured")
            if c.nonpriority_type == wanted
        ),
        Decimal("0"),
    )


def _creditor_block(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    prefix: str,
    index: int,
    claim: ClaimBody,
    problems: list[str],
) -> None:
    creditor = case_file.creditor(claim.creditor_id)
    if creditor is None:
        return
    row_fill(
        release,
        values,
        f"{prefix}.creditor_name",
        index,
        text_or_none(creditor.name),
        problems,
    )
    parts = [
        (f"{prefix}.creditor_street", creditor.address.line1),
        (f"{prefix}.creditor_city", creditor.address.city),
        (f"{prefix}.creditor_state", creditor.address.state),
        (f"{prefix}.creditor_zip", creditor.address.postal_code),
    ]
    if prefix == "priority":  # Part 2's rows print no second street line.
        parts.insert(1, ("priority.creditor_street2", creditor.address.line2))
    for field_id, part in parts:
        row_fill(release, values, field_id, index, text_or_none(part), problems)


def _common_columns(
    release: FormRelease,
    values: FieldValues,
    prefix: str,
    index: int,
    claim: ClaimBody,
    problems: list[str],
) -> None:
    row_fill(
        release,
        values,
        f"{prefix}.account_last4",
        index,
        text_or_none(claim.account_last4),
        problems,
    )
    row_fill(
        release,
        values,
        f"{prefix}.date_incurred",
        index,
        text_or_none(claim.date_incurred and format_date(claim.date_incurred)),
        problems,
    )
    if claim.who_incurred is not None:
        field_id = f"{prefix}.who_incurred"
        spec = release.field(field_id)
        if index < len(spec.pdf_names):
            row_fill(
                release,
                values,
                field_id,
                index,
                canonical_option(
                    release, field_id, spec.pdf_names[index], claim.who_incurred
                ),
                problems,
            )
    for attr in _FLAG_FIELDS:
        if getattr(claim, attr):
            row_fill(release, values, f"{prefix}.{attr}", index, Check(), problems)
    if claim.subject_to_offset is not None:
        row_fill(
            release,
            values,
            f"{prefix}.subject_to_offset",
            index,
            Option("yes" if claim.subject_to_offset else "no"),
            problems,
        )


def project_b106ef_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    priority = claims_of(case_file, "priority_unsecured")
    nonpriority = claims_of(case_file, "nonpriority_unsecured")

    # Part 1 — priority unsecured claims.
    values["line_1_any_priority_claims"] = yes_no(
        release, "line_1_any_priority_claims", bool(priority)
    )
    for index, claim in enumerate(priority):
        _creditor_block(release, values, case_file, "priority", index, claim, problems)
        _common_columns(release, values, "priority", index, claim, problems)
        if claim.priority_amount is not None or claim.nonpriority_amount is not None:
            row_fill(
                release,
                values,
                "priority.total_claim",
                index,
                Text(format_money(_total_claim(claim))),
                problems,
            )
        row_fill(
            release,
            values,
            "priority.priority_amount",
            index,
            text_or_none(claim.priority_amount and format_money(claim.priority_amount)),
            problems,
        )
        row_fill(
            release,
            values,
            "priority.nonpriority_amount",
            index,
            text_or_none(
                claim.nonpriority_amount and format_money(claim.nonpriority_amount)
            ),
            problems,
        )
        if claim.priority_type is not None:
            row_fill(
                release,
                values,
                _PRIORITY_TYPE_BOXES[claim.priority_type],
                index,
                Check(),
                problems,
            )
        row_fill(
            release,
            values,
            "priority.type_other_specify",
            index,
            text_or_none(claim.priority_type_other),
            problems,
        )

    # Part 2 — nonpriority unsecured claims.
    values["line_3_any_nonpriority_claims"] = yes_no(
        release, "line_3_any_nonpriority_claims", bool(nonpriority)
    )
    for index, claim in enumerate(nonpriority):
        _creditor_block(
            release, values, case_file, "nonpriority", index, claim, problems
        )
        _common_columns(release, values, "nonpriority", index, claim, problems)
        row_fill(
            release,
            values,
            "nonpriority.amount",
            index,
            text_or_none(claim.amount and format_money(claim.amount)),
            problems,
        )
        if claim.nonpriority_type is not None:
            row_fill(
                release,
                values,
                _NONPRIORITY_TYPE_BOXES[claim.nonpriority_type],
                index,
                Check(),
                problems,
            )
        row_fill(
            release,
            values,
            "nonpriority.type_other_specify",
            index,
            text_or_none(claim.nonpriority_type_other),
            problems,
        )

    # Part 3 — others to be notified, from both parts' claims in case order.
    parties = [
        (claim, party)
        for claim in priority + nonpriority
        for party in claim.notice_parties
    ]
    for index, (claim, party) in enumerate(parties):
        row_fill(
            release, values, "notify.name", index, text_or_none(party.name), problems
        )
        for field_id, part in (
            ("notify.street", party.address.line1),
            ("notify.street2", party.address.line2),
            ("notify.city", party.address.city),
            ("notify.state", party.address.state),
            ("notify.zip", party.address.postal_code),
        ):
            row_fill(release, values, field_id, index, text_or_none(part), problems)
        row_fill(
            release,
            values,
            "notify.account_last4",
            index,
            text_or_none(party.account_last4),
            problems,
        )
        # Part 1 vs Part 2: the referenced claim's class answers it; the
        # line NUMBER stays blank for packet assembly.
        row_fill(
            release,
            values,
            "notify.referenced_part",
            index,
            Option("Part 1" if claim.claim_class == "priority_unsecured" else "yes"),
            problems,
        )

    # Part 4 — the statistical rollup, derived line by line.
    values["line_6a_total"] = Text(
        format_money(priority_type_total(case_file, "domestic_support"))
    )
    values["line_6b_total"] = Text(
        format_money(priority_type_total(case_file, "tax_and_government"))
    )
    values["line_6c_total"] = Text(
        format_money(
            priority_type_total(case_file, "death_or_injury_while_intoxicated")
        )
    )
    values["line_6d_total"] = Text(
        format_money(priority_type_total(case_file, "other"))
    )
    values["line_6e_total"] = Text(format_money(priority_unsecured_total(case_file)))
    values["line_6f_total"] = Text(
        format_money(nonpriority_type_total(case_file, "student_loan"))
    )
    values["line_6g_total"] = Text(
        format_money(nonpriority_type_total(case_file, "separation_or_divorce"))
    )
    values["line_6h_total"] = Text(
        format_money(nonpriority_type_total(case_file, "pension_or_profit_sharing"))
    )
    values["line_6i_total"] = Text(
        format_money(nonpriority_type_total(case_file, "other"))
    )
    values["line_6j_total"] = Text(format_money(nonpriority_unsecured_total(case_file)))

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
