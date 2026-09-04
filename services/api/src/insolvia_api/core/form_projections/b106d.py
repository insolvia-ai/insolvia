"""B106D @ 2015-12-01 (revision 12/15) — Schedule D's mapping.

Secured claims in creation order onto the five printed rows, each row's
creditor block resolved through `claim.creditor_id`. The unsecured portion
is derived per row (amount less collateral value, floored at zero — the
model refuses to store it), Column A's page subtotals and total are summed
here, and the total feeds 106Sum line 2 through `secured_total`.

Two rows carry the official PDF's broken who-owes groups (forms/README.md):
row 2.4's four options share one field exporting `On`, so the selection is
a `WidgetStates` index in the printed option order; row 2.5's options are
four independent checkboxes, picked by name. Blank by design: row numbering
and Part 2's "line used to identify" cross-references (packet assembly owns
row placement once continuation pages exist), and the fourth notice row's
account box, which is the official PDF's second widget of row 2.1's account
field — a notice party there can only repeat the first claim's last-four,
so a different stored value is an error rather than a silent misprint.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

from insolvia_core.claims import ClaimBody

from ..form_fill import Check, Text, WidgetStates
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

# The four who-owes options in the printed order — what row 2.4's
# WidgetStates indexes and row 2.5's checkbox names both key on.
_WHO_PRINT_ORDER: Final = ("debtor_1", "debtor_2", "both", "at_least_one_plus_another")

_WHO_ROW_5_BOXES: Final = {
    "debtor_1": "Debtor 1 only_5",
    "debtor_2": "Debtor 2 only_5",
    "both": "Debtor 1 and Debtor 2 only_5",
    "at_least_one_plus_another": "At least one of the debtors and another_5",
}

_LIEN_BOXES: Final = {
    "agreement": "claim.lien_agreement",
    "statutory": "claim.lien_statutory",
    "judgment": "claim.lien_judgment",
    "other": "claim.lien_other",
}

_FLAG_FIELDS: Final = (
    ("contingent", "claim.contingent"),
    ("unliquidated", "claim.unliquidated"),
    ("disputed", "claim.disputed"),
    ("community_debt", "claim.community_debt"),
)

# Which claim row each page's Column A subtotal covers: rows 2.1-2.2 print
# on page one, 2.3-2.5 on page two.
_PAGE_ROWS: Final = ((0, 1), (2, 3, 4))


def secured_total(case_file: CaseFile) -> Decimal:
    """Column A's total — copied to B106Sum line 2."""
    return sum(
        (amount(claim.amount) for claim in claims_of(case_file, "secured")),
        Decimal("0"),
    )


def _who_owes(
    release: FormRelease,
    values: FieldValues,
    index: int,
    who: str,
    problems: list[str],
) -> None:
    if index < 3:
        spec = release.field("claim.who_owes")
        row_fill(
            release,
            values,
            "claim.who_owes",
            index,
            canonical_option(release, "claim.who_owes", spec.pdf_names[index], who),
            problems,
        )
    elif index == 3:
        # Row 2.4's broken group: all four options export `On`, so the
        # selection is the widget's POSITION in the printed option order.
        values["claim.who_owes_row_2_4"] = WidgetStates(
            indexes=(_WHO_PRINT_ORDER.index(who),)
        )
    else:
        entry = values.setdefault("claim.who_owes_row_2_5", {})
        assert isinstance(entry, dict)
        entry[_WHO_ROW_5_BOXES[who]] = Check()


def _notice_rows(
    release: FormRelease,
    values: FieldValues,
    secured: list[ClaimBody],
    problems: list[str],
) -> None:
    parties = [(claim, party) for claim in secured for party in claim.notice_parties]
    account_names = release.field("notify.account_last4").pdf_names
    for index, (_claim, party) in enumerate(parties):
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
        if party.account_last4 is not None:
            if index == 3:
                # The fourth row's account box is the official PDF's second
                # widget of claim row 2.1's account field — it always mirrors
                # that value and cannot hold its own.
                first = secured[0].account_last4 if secured else None
                if party.account_last4 != first:
                    problems.append(
                        "notify.account_last4: the fourth notice row's box is "
                        "the PDF's second widget of claim row 2.1's account "
                        "field and can only repeat its value; "
                        f"{party.account_last4!r} differs"
                    )
                continue
            # The five dedicated boxes serve notice rows 1-3 and 5-6.
            position = index if index < 3 else index - 1
            if position >= len(account_names):
                problems.append(
                    f"notify.account_last4: row {index + 1} does not exist — "
                    f"the form prints 6 rows"
                )
                continue
            entry = values.setdefault("notify.account_last4", {})
            assert isinstance(entry, dict)
            entry[account_names[position]] = Text(party.account_last4)


def project_b106d_1215(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    secured = claims_of(case_file, "secured")
    values["line_1_any_secured_claims"] = yes_no(
        release, "line_1_any_secured_claims", bool(secured)
    )

    for index, claim in enumerate(secured):
        creditor = case_file.creditor(claim.creditor_id)
        if creditor is not None:
            row_fill(
                release,
                values,
                "claim.creditor_name",
                index,
                text_or_none(creditor.name),
                problems,
            )
            for field_id, part in (
                ("claim.creditor_street", creditor.address.line1),
                ("claim.creditor_street2", creditor.address.line2),
                ("claim.creditor_city", creditor.address.city),
                ("claim.creditor_state", creditor.address.state),
                ("claim.creditor_zip", creditor.address.postal_code),
            ):
                row_fill(release, values, field_id, index, text_or_none(part), problems)

        row_fill(
            release,
            values,
            "claim.collateral_description",
            index,
            text_or_none(claim.collateral_description),
            problems,
        )
        row_fill(
            release,
            values,
            "claim.amount",
            index,
            text_or_none(claim.amount and format_money(claim.amount)),
            problems,
        )
        row_fill(
            release,
            values,
            "claim.collateral_value",
            index,
            text_or_none(
                claim.collateral_value and format_money(claim.collateral_value)
            ),
            problems,
        )
        # The unsecured portion: amount less collateral value, floored at
        # zero — derived, never stored (case-data-model.md).
        if claim.amount is not None and claim.collateral_value is not None:
            unsecured = max(
                amount(claim.amount) - amount(claim.collateral_value), Decimal("0")
            )
            row_fill(
                release,
                values,
                "claim.unsecured_portion",
                index,
                Text(format_money(unsecured)),
                problems,
            )
        for attr, field_id in _FLAG_FIELDS:
            if getattr(claim, attr):
                row_fill(release, values, field_id, index, Check(), problems)
        for member in claim.lien_nature:
            row_fill(release, values, _LIEN_BOXES[member], index, Check(), problems)
        row_fill(
            release,
            values,
            "claim.lien_other_specify",
            index,
            text_or_none(claim.lien_nature_other),
            problems,
        )
        if claim.who_incurred is not None and index < len(
            release.field("claim.creditor_name").pdf_names
        ):
            _who_owes(release, values, index, claim.who_incurred, problems)
        row_fill(
            release,
            values,
            "claim.date_incurred",
            index,
            text_or_none(claim.date_incurred and format_date(claim.date_incurred)),
            problems,
        )
        row_fill(
            release,
            values,
            "claim.account_last4",
            index,
            text_or_none(claim.account_last4),
            problems,
        )

    if secured:
        subtotal_names = release.field("part1_page_subtotal").pdf_names
        for page_index, rows_on_page in enumerate(_PAGE_ROWS):
            on_page = [secured[row] for row in rows_on_page if row < len(secured)]
            if on_page:
                entry = values.setdefault("part1_page_subtotal", {})
                assert isinstance(entry, dict)
                entry[subtotal_names[page_index]] = Text(
                    format_money(sum((amount(c.amount) for c in on_page), Decimal("0")))
                )
        values["part1_total"] = Text(format_money(secured_total(case_file)))

    _notice_rows(release, values, secured, problems)

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
