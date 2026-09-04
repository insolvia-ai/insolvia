"""B107 @ 2025-04-01 (revision 04/25) — the Statement of Financial Affairs.

The SOFA is one typed-entry table (core/sofa.py); this mapping fans the
entries back out by `entry_type`, one question at a time, with each
question's gate derived from whether entries of its type exist. The
per-question helpers below keep the landing rules readable: a question's
entries take its printed rows in creation order, and overflow is an error
like every schedule's.

Readings this revision forces, each argued where it happens:

- **Q4/Q5** split `income_by_period` by kind: wages and business rows
  bucket into the three printed periods by their period-start year against
  the case's creation year (the filing-date stand-in, as 106C uses);
  `other` entries take Q5's flat source rows.
- **Q6's gates** answer from petition.debt_character (the same fact B101
  line 16 prints) and from whether creditor_payment entries exist — the
  dollar floors themselves live with the effective constant sets, and only
  the branch the consumer answer selects is answered.
- **Q26** is the PDF's merged gate+status group: a No is a plain option,
  a Yes must set the yes box AND the status box by appearance state.
- **Shared-widget defects** (Q18/Q19's date, Q20's row-two ZIP, Q21's
  access-holder ZIP) can only repeat their master box's value; a stored
  fact that differs is an error, never a silent misprint.

Blank until their owners land: the amended caption, wet signatures, the
court's case number, Q4's checkboxes-with-no-yes-widget quirks aside, the
attached-pages question (packet assembly owns continuation sheets), and
Q27's accountant column (nothing structured records it).
"""

from __future__ import annotations

from collections.abc import Iterable, Sequence
from decimal import Decimal
from typing import Final, TypeVar

from ..fields import Address
from ..form_fill import Check, FieldFill, Option, Text, WidgetStates
from ..form_templates import FormRelease
from ..sofa import (
    BusinessConnection,
    CharitableContribution,
    ClosedAccount,
    ConsultantPayment,
    CreditorAssistancePayment,
    CreditorPayment,
    Gift,
    HeldForAnother,
    InsiderBenefitPayment,
    InsiderPayment,
    Lawsuit,
    Loss,
    MaritalStatus,
    PriorAddress,
    PropertyTransfer,
    Repossession,
    SelfSettledTrust,
    Setoff,
    StorageUnit,
)
from .shared import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    amount,
    format_date,
    format_money,
    full_name,
    row_fill,
    text_or_none,
    yes_no,
)

_MARITAL_EXPORTS: Final = {"married": "married", "not_married": "yes"}

_PAYMENT_PURPOSE_BOXES: Final = {
    "mortgage": "q6_was_payment_for_mortgage",
    "car": "q6_was_payment_for_car",
    "credit_card": "q6_was_payment_for_credit_card",
    "loan_repayment": "q6_was_payment_for_loan",
    "suppliers_or_vendors": "q6_was_payment_for_vendors",
    "other": "q6_was_payment_for_other",
}

_REPOSSESSION_BOXES: Final = {
    "repossessed": "q10_happened_repossessed",
    "foreclosed": "q10_happened_foreclosed",
    "garnished": "q10_happened_garnished",
    "attached": "q10_happened_attached",
}

_ACCOUNT_TYPE_BOXES: Final = {
    "checking": "q20_type_checking",
    "savings": "q20_type_savings",
    "money_market": "q20_type_money_market",
    "brokerage": "q20_type_brokerage",
    "other": "q20_type_other",
}

_CONNECTION_BOXES: Final = {
    "sole_proprietor": "q27_connection_sole_proprietor",
    "llc_member": "q27_connection_llc_member",
    "partner": "q27_connection_partner",
    "officer_or_director": "q27_connection_officer",
    "owner_of_5_percent": "q27_connection_owner_5pct",
}

PayloadT = TypeVar("PayloadT")


def _entries(case_file: CaseFile, payload_type: type[PayloadT]) -> list[PayloadT]:
    return [
        entry.payload
        for entry in case_file.sofa_entries
        if isinstance(entry.payload, payload_type)
    ]


def _payload(case_file: CaseFile, payload_type: type[PayloadT]) -> PayloadT | None:
    found = _entries(case_file, payload_type)
    return found[0] if found else None


def _address_pairs(
    address: Address, street: str, street2: str | None, city: str, state: str, zip_: str
) -> list[tuple[str, str | None]]:
    pairs: list[tuple[str, str | None]] = [(street, address.line1)]
    if street2 is not None:
        pairs.append((street2, address.line2))
    pairs.extend(
        [(city, address.city), (state, address.state), (zip_, address.postal_code)]
    )
    return pairs


def _put_row(
    release: FormRelease,
    values: FieldValues,
    index: int,
    pairs: Iterable[tuple[str, str | None]],
    problems: list[str],
) -> None:
    for field_id, value in pairs:
        row_fill(release, values, field_id, index, text_or_none(value), problems)


def _named(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    pdf_name: str,
    fill: FieldFill,
) -> None:
    """Land one instance by its PDF NAME — for the fields whose row identity
    lives in a letter suffix ('Date1 13a') the generic ordering cannot see."""
    spec = release.field(field_id)
    if pdf_name not in spec.pdf_names:  # pragma: no cover - template-pinned
        raise KeyError(f"{field_id} claims no PDF field {pdf_name!r}")
    if len(spec.pdf_names) == 1:
        values[field_id] = fill
        return
    entry = values.setdefault(field_id, {})
    assert isinstance(entry, dict)
    entry[pdf_name] = fill


def _date_boxes(
    release: FormRelease,
    values: FieldValues,
    field_id: str,
    row: int,
    per_row: int,
    dates: Sequence[str],
    problems: list[str],
) -> None:
    """A row's 'Dates' comb — `per_row` boxes per printed row, addressed by
    flat position. More stored dates than boxes is an error."""
    if len(dates) > per_row:
        problems.append(
            f"{field_id}: row {row + 1} prints {per_row} date boxes; "
            f"the entry holds {len(dates)} dates"
        )
    for offset, value in enumerate(dates[:per_row]):
        row_fill(
            release,
            values,
            field_id,
            row * per_row + offset,
            Text(format_date(value)),
            problems,
        )


def _money(value: str | None) -> str | None:
    return format_money(value) if value is not None else None


def _q1_q3(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    marital = _payload(case_file, MaritalStatus)
    if marital is not None and marital.status is not None:
        values["q1_marital_status"] = Option(_MARITAL_EXPORTS[marital.status])

    addresses = _entries(case_file, PriorAddress)
    values["q2_gate"] = yes_no(release, "q2_gate", bool(addresses))
    rows = {"debtor_1": 0, "debtor_2": 0}
    for entry in addresses:
        which = entry.which_debtor or "debtor_1"
        column = "debtor1" if which in ("debtor_1", "both") else "debtor2"
        index = rows["debtor_1" if column == "debtor1" else "debtor_2"]
        rows["debtor_1" if column == "debtor1" else "debtor_2"] += 1
        _put_row(
            release,
            values,
            index,
            [
                *_address_pairs(
                    entry.address,
                    f"q2_{column}_street",
                    f"q2_{column}_street2",
                    f"q2_{column}_city",
                    f"q2_{column}_state",
                    f"q2_{column}_zip",
                ),
                (f"q2_{column}_from", entry.from_date and format_date(entry.from_date)),
                (f"q2_{column}_to", entry.to_date and format_date(entry.to_date)),
            ],
            problems,
        )
        if which == "both":
            # Debtor 2 shared the address and the dates: the row's two
            # same-as boxes say so.
            row_fill(
                release, values, "q2_debtor2_same_as_debtor1", index, Check(), problems
            )
            row_fill(release, values, "q2_debtor2_same_dates", index, Check(), problems)

    community = bool(case_file.community_household_members)
    values["q3_community_property"] = yes_no(
        release, "q3_community_property", community
    )


def _case_year(case_file: CaseFile) -> int:
    return int(case_file.case.created_at[:4])


def _q4_q5(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    from ..sofa import IncomeByPeriod

    entries = _entries(case_file, IncomeByPeriod)
    employment = [
        e
        for e in entries
        if e.kind in ("wages_and_commissions", "operating_a_business")
    ]
    other = [e for e in entries if e.kind == "other"]
    year = _case_year(case_file)
    periods = {year: "current", year - 1: "last_year", year - 2: "year_before"}

    values["q4_gate"] = yes_no(release, "q4_gate", bool(employment))
    if employment:
        values["q4_last_year_yyyy"] = Text(str(year - 1))
        values["q4_year_before_yyyy"] = Text(str(year - 2))

    buckets: dict[tuple[str, str], list[IncomeByPeriod]] = {}
    for entry in employment:
        debtors = (
            ("debtor1", "debtor2")
            if entry.which_debtor == "both"
            else ("debtor2",)
            if entry.which_debtor == "debtor_2"
            else ("debtor1",)
        )
        period = periods.get(
            int(entry.period_start[:4]) if entry.period_start else year
        )
        if period is None:
            problems.append(
                "q4: the form prints income for the filing year and the two "
                f"before it; a period starting {entry.period_start} fits none"
            )
            continue
        for debtor in debtors:
            buckets.setdefault((debtor, period), []).append(entry)

    for (debtor, period), bucket in buckets.items():
        kinds = {entry.kind for entry in bucket}
        if "wages_and_commissions" in kinds:
            values[f"q4_{debtor}_{period}_wages"] = Check()
        if "operating_a_business" in kinds:
            values[f"q4_{debtor}_{period}_business"] = Check()
        values[f"q4_{debtor}_{period}_gross"] = Text(
            format_money(
                sum((amount(entry.gross_amount) for entry in bucket), Decimal("0"))
            )
        )

    # Q5 — other income, one flat source row per entry and column. The gate
    # is the PDF's quirk: only the No box carries a widget, so a Yes leaves
    # the field untouched.
    if not other:
        values["q5_gate"] = Check()
    columns = {"debtor1": 0, "debtor2": 0}
    for entry in other:
        debtors = (
            ("debtor1", "debtor2")
            if entry.which_debtor == "both"
            else ("debtor2",)
            if entry.which_debtor == "debtor_2"
            else ("debtor1",)
        )
        for debtor in debtors:
            index = columns[debtor]
            columns[debtor] += 1
            _put_row(
                release,
                values,
                index,
                [
                    (f"q5_{debtor}_source", entry.description),
                    (f"q5_{debtor}_gross", _money(entry.gross_amount)),
                ],
                problems,
            )


def _q6(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    payments = _entries(case_file, CreditorPayment)
    petition = case_file.petition
    consumer: bool | None = None
    if petition is not None and petition.debt_character is not None:
        consumer = petition.debt_character == "consumer"
        values["q6_consumer_debts"] = yes_no(release, "q6_consumer_debts", consumer)
    # Only the branch the consumer answer selects is answered; the dollar
    # floors are the constant set's, and intake records only reportable
    # payments, so the entries' existence IS the answer.
    if consumer is True:
        values["q6_paid_600"] = yes_no(release, "q6_paid_600", bool(payments))
    elif consumer is False:
        values["q6_paid_8575"] = yes_no(release, "q6_paid_8575", bool(payments))

    for index, payment in enumerate(payments):
        _put_row(
            release,
            values,
            index,
            [
                ("q6_creditor_name", payment.creditor.name),
                *_address_pairs(
                    payment.creditor.address,
                    "q6_creditor_street",
                    "q6_creditor_street2",
                    "q6_creditor_city",
                    "q6_creditor_state",
                    "q6_creditor_zip",
                ),
                ("q6_total_paid", _money(payment.total_paid)),
                ("q6_still_owe", _money(payment.amount_still_owed)),
                ("q6_other_specify", payment.payment_for_other),
            ],
            problems,
        )
        _date_boxes(
            release, values, "q6_payment_dates", index, 3, payment.dates, problems
        )
        for purpose in payment.payment_for:
            row_fill(
                release,
                values,
                _PAYMENT_PURPOSE_BOXES[purpose],
                index,
                Check(),
                problems,
            )


def _q7_q8(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    insiders = _entries(case_file, InsiderPayment)
    values["q7_gate"] = yes_no(release, "q7_gate", bool(insiders))
    for index, payment in enumerate(insiders):
        _put_row(
            release,
            values,
            index,
            [
                ("q7_insider_name", payment.insider.name),
                *_address_pairs(
                    payment.insider.address,
                    "q7_insider_street",
                    "q7_insider_street2",
                    "q7_insider_city",
                    "q7_insider_state",
                    "q7_insider_zip",
                ),
                ("q7_total_paid", _money(payment.total_paid)),
                ("q7_still_owe", _money(payment.amount_still_owed)),
                ("q7_reason", payment.reason),
            ],
            problems,
        )
        _date_boxes(
            release, values, "q7_payment_dates", index, 3, payment.dates, problems
        )

    benefits = _entries(case_file, InsiderBenefitPayment)
    values["q8_gate"] = yes_no(release, "q8_gate", bool(benefits))
    for index, benefit in enumerate(benefits):
        # The form prints ONE name-and-address block per row (the insider's).
        # The recipient party is that block; a bare insider_name fills the
        # name box only when no recipient name competes for it.
        name = benefit.recipient.name
        if name is None:
            name = benefit.insider_name
        elif benefit.insider_name and benefit.insider_name != name:
            problems.append(
                "q8: the row prints one name box; the entry stores both a "
                f"recipient ({name!r}) and an insider "
                f"({benefit.insider_name!r})"
            )
        _put_row(
            release,
            values,
            index,
            [
                ("q8_insider_name", name),
                *_address_pairs(
                    benefit.recipient.address,
                    "q8_insider_street",
                    "q8_insider_street2",
                    "q8_insider_city",
                    "q8_insider_state",
                    "q8_insider_zip",
                ),
                ("q8_total_paid", _money(benefit.total_paid)),
                ("q8_reason", benefit.reason),
            ],
            problems,
        )
        _date_boxes(
            release, values, "q8_payment_dates", index, 3, benefit.dates, problems
        )


def _q9_q12(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    from ..sofa import Receivership

    lawsuits = _entries(case_file, Lawsuit)
    values["q9_gate"] = yes_no(release, "q9_gate", bool(lawsuits))
    for index, suit in enumerate(lawsuits):
        if index >= 2:
            problems.append("q9: the form prints 2 rows; more lawsuits overflow")
            continue
        # Every detail box carries the row letter in its NAME, and the
        # dump lists row b's before row a's — address by name throughout.
        letter = "ab"[index]
        if suit.case_title:
            _named(
                release,
                values,
                "q9_case_title",
                f"Case title1 9{letter}",
                Text(suit.case_title),
            )
        for field_id, pdf_name, value in (
            ("q9_case_number", f"Case number 9{letter}", suit.case_number),
            ("q9_case_nature", f"Case nature 9{letter}", suit.nature_of_case),
            ("q9_court_name", f"Court Name 9{letter}", suit.court.name),
            ("q9_court_street", f"Court address 9{letter}", suit.court.address.line1),
            ("q9_court_city", f"Court City 9{letter}", suit.court.address.city),
            ("q9_court_state", f"Court State 9{letter}", suit.court.address.state),
            (
                "q9_court_zip",
                f"Court ZIP Code 9{letter}",
                suit.court.address.postal_code,
            ),
        ):
            if value:
                _named(release, values, field_id, pdf_name, Text(value))
        if suit.status is not None:
            row_fill(
                release,
                values,
                "q9_status",
                index,
                Option(suit.status.replace("_", " ")),
                problems,
            )

    repossessions = _entries(case_file, Repossession)
    values["q10_gate"] = yes_no(release, "q10_gate", bool(repossessions))
    for index, event in enumerate(repossessions):
        _put_row(
            release,
            values,
            index,
            [
                ("q10_creditor_name", event.creditor.name),
                *_address_pairs(
                    event.creditor.address,
                    "q10_creditor_street",
                    "q10_creditor_street2",
                    "q10_creditor_city",
                    "q10_creditor_state",
                    "q10_creditor_zip",
                ),
                ("q10_describe_property", event.description),
                ("q10_date", event.date and format_date(event.date)),
                ("q10_value", _money(event.value)),
            ],
            problems,
        )
        if event.action is not None:
            row_fill(
                release,
                values,
                _REPOSSESSION_BOXES[event.action],
                index,
                Check(),
                problems,
            )

    setoffs = _entries(case_file, Setoff)
    values["q11_gate"] = yes_no(release, "q11_gate", bool(setoffs))
    for index, setoff in enumerate(setoffs):
        _put_row(
            release,
            values,
            index,
            [
                ("q11_creditor_name", setoff.creditor.name),
                *_address_pairs(
                    setoff.creditor.address,
                    "q11_creditor_street",
                    "q11_creditor_street2",
                    "q11_creditor_city",
                    "q11_creditor_state",
                    "q11_creditor_zip",
                ),
                ("q11_describe_action", setoff.description),
                ("q11_date", setoff.date and format_date(setoff.date)),
                ("q11_amount", _money(setoff.amount)),
            ],
            problems,
        )

    # Q12 takes only yes/no; a receivership's details go on an attached
    # sheet, which packet assembly owns.
    values["q12_assignee"] = yes_no(
        release, "q12_assignee", bool(_entries(case_file, Receivership))
    )


def _q13_q17(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    gifts = _entries(case_file, Gift)
    values["q13_gate"] = yes_no(release, "q13_gate", bool(gifts))
    for index, gift in enumerate(gifts):
        if index >= 2:
            problems.append("q13: the form prints 2 rows; more gifts overflow")
            continue
        letter = "ab"[index]
        _put_row(
            release,
            values,
            index,
            [
                ("q13_recipient_name", gift.recipient.name),
                *_address_pairs(
                    gift.recipient.address,
                    "q13_recipient_street",
                    "q13_recipient_street2",
                    "q13_recipient_city",
                    "q13_recipient_state",
                    "q13_recipient_zip",
                ),
                ("q13_relationship", gift.relationship),
                ("q13_describe", gift.description),
            ],
            problems,
        )
        if len(gift.dates) > 2:
            problems.append(
                f"q13: row {index + 1} prints 2 date boxes; the entry holds "
                f"{len(gift.dates)} dates"
            )
        for offset, value in enumerate(gift.dates[:2]):
            _named(
                release,
                values,
                "q13_dates",
                f"Date{offset + 1} 13{letter}",
                Text(format_date(value)),
            )
        if gift.value is not None:
            _named(
                release,
                values,
                "q13_values",
                f"Amount1 13{letter}",
                Text(format_money(gift.value)),
            )

    contributions = _entries(case_file, CharitableContribution)
    values["q14_gate"] = yes_no(release, "q14_gate", bool(contributions))
    for index, contribution in enumerate(contributions):
        _put_row(
            release,
            values,
            index,
            [
                ("q14_charity_name", contribution.organization.name),
                *_address_pairs(
                    contribution.organization.address,
                    "q14_charity_street",
                    "q14_charity_street2",
                    "q14_charity_city",
                    "q14_charity_state",
                    "q14_charity_zip",
                ),
                ("q14_describe", contribution.description),
            ],
            problems,
        )
        _date_boxes(
            release, values, "q14_dates", index, 2, contribution.dates, problems
        )
        if contribution.value is not None:
            row_fill(
                release,
                values,
                "q14_values",
                index * 2,
                Text(format_money(contribution.value)),
                problems,
            )

    losses = _entries(case_file, Loss)
    values["q15_gate"] = yes_no(release, "q15_gate", bool(losses))
    for index, loss in enumerate(losses):
        _put_row(
            release,
            values,
            index,
            [
                ("q15_describe_loss", loss.description),
                ("q15_describe_insurance", loss.insurance_coverage),
                ("q15_date", loss.date and format_date(loss.date)),
                ("q15_value", _money(loss.value)),
            ],
            problems,
        )

    consultants = _entries(case_file, ConsultantPayment)
    values["q16_gate"] = yes_no(release, "q16_gate", bool(consultants))
    for index, payment in enumerate(consultants):
        if index >= 2:
            problems.append("q16: the form prints 2 rows; more payments overflow")
            continue
        _put_row(
            release,
            values,
            index,
            [
                ("q16_payee_name", payment.person.name),
                *_address_pairs(
                    payment.person.address,
                    "q16_payee_street",
                    "q16_payee_street2",
                    "q16_payee_city",
                    "q16_payee_state",
                    "q16_payee_zip",
                ),
                ("q16_email", payment.email_or_website),
                ("q16_payer", payment.who_made_payment),
                ("q16_describe", payment.description),
            ],
            problems,
        )
        if index < 2:
            letter = "ab"[index]
            if payment.date is not None:
                _named(
                    release,
                    values,
                    "q16_dates",
                    f"Date1 16{letter}",
                    Text(format_date(payment.date)),
                )
            if payment.amount is not None:
                _named(
                    release,
                    values,
                    "q16_amounts",
                    f"Amount1 16{letter}",
                    Text(format_money(payment.amount)),
                )

    workouts = _entries(case_file, CreditorAssistancePayment)
    values["q17_gate"] = yes_no(release, "q17_gate", bool(workouts))
    for index, workout in enumerate(workouts):
        _put_row(
            release,
            values,
            index,
            [
                ("q17_payee_name", workout.person.name),
                *_address_pairs(
                    workout.person.address,
                    "q17_payee_street",
                    "q17_payee_street2",
                    "q17_payee_city",
                    "q17_payee_state",
                    "q17_payee_zip",
                ),
                ("q17_describe", workout.description),
            ],
            problems,
        )
        if workout.date is not None:
            row_fill(
                release,
                values,
                "q17_dates",
                index * 2,
                Text(format_date(workout.date)),
                problems,
            )
        if workout.amount is not None:
            row_fill(
                release,
                values,
                "q17_amounts",
                index * 2,
                Text(format_money(workout.amount)),
                problems,
            )


def _q18_q23(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    from ..sofa import SafeDepositBox

    transfers = _entries(case_file, PropertyTransfer)
    values["q18_gate"] = yes_no(release, "q18_gate", bool(transfers))
    for index, transfer in enumerate(transfers):
        _put_row(
            release,
            values,
            index,
            [
                ("q18_recipient_name", transfer.transferee.name),
                *_address_pairs(
                    transfer.transferee.address,
                    "q18_recipient_street",
                    "q18_recipient_street2",
                    "q18_recipient_city",
                    "q18_recipient_state",
                    "q18_recipient_zip",
                ),
                ("q18_relationship", transfer.relationship),
                ("q18_describe_property", transfer.description),
                ("q18_describe_exchange", transfer.value_received),
                ("q18_date", transfer.date and format_date(transfer.date)),
            ],
            problems,
        )

    trusts = _entries(case_file, SelfSettledTrust)
    values["q19_gate"] = yes_no(release, "q19_gate", bool(trusts))
    for index, trust in enumerate(trusts):
        _put_row(
            release,
            values,
            index,
            [
                ("q19_trust_name", trust.trust_name),
                ("q19_describe", trust.description),
            ],
            problems,
        )
        if trust.date is not None:
            # The trust's date box is the PDF's second widget of Q18 row
            # two's date field — it can only repeat that value.
            second_transfer = transfers[1] if len(transfers) > 1 else None
            mirrored = second_transfer.date if second_transfer else None
            if trust.date != mirrored:
                problems.append(
                    "q19: the transfer-date box is the PDF's second widget "
                    "of Q18 row two's date field and can only repeat its "
                    f"value; {trust.date!r} differs"
                )

    accounts = _entries(case_file, ClosedAccount)
    values["q20_gate"] = yes_no(release, "q20_gate", bool(accounts))
    for index, account in enumerate(accounts):
        pairs = [
            ("q20_institution_name", account.institution.name),
            *_address_pairs(
                account.institution.address,
                "q20_institution_street",
                "q20_institution_street2",
                "q20_institution_city",
                "q20_institution_state",
                "q20_institution_zip",
            ),
        ]
        if index == 1:
            # Row two's ZIP box is the PDF's second widget of row one's and
            # can only repeat its value.
            pairs = pairs[:-1]
            zip_code = account.institution.address.postal_code
            if zip_code and zip_code != accounts[0].institution.address.postal_code:
                problems.append(
                    "q20: row two's ZIP box is the PDF's second widget of "
                    f"row one's and can only repeat its value; {zip_code!r} "
                    "differs"
                )
        _put_row(
            release,
            values,
            index,
            [
                *pairs,
                ("q20_account_last4", account.account_last4),
                (
                    "q20_date_closed",
                    account.date_closed and format_date(account.date_closed),
                ),
                ("q20_balance", _money(account.last_balance)),
            ],
            problems,
        )
        if account.account_type is not None:
            row_fill(
                release,
                values,
                _ACCOUNT_TYPE_BOXES[account.account_type],
                index,
                Check(),
                problems,
            )

    boxes = _entries(case_file, SafeDepositBox)
    values["q21_gate"] = yes_no(release, "q21_gate", bool(boxes))
    for index, box in enumerate(boxes):
        _put_row(
            release,
            values,
            index,
            [
                ("q21_institution_name", box.institution.name),
                *_address_pairs(
                    box.institution.address,
                    "q21_institution_street",
                    "q21_institution_street2",
                    "q21_institution_city",
                    "q21_institution_state",
                    "q21_institution_zip",
                ),
                ("q21_describe", box.description),
            ],
            problems,
        )
        if len(box.who_has_access) > 1:
            problems.append(
                "q21: the form prints one access-holder block; the entry "
                f"names {len(box.who_has_access)} people"
            )
        if box.who_has_access:
            values["q21_access_name"] = Text(box.who_has_access[0])
        if box.still_have is not None:
            values["q21_still_have"] = yes_no(release, "q21_still_have", box.still_have)

    units = _entries(case_file, StorageUnit)
    values["q22_gate"] = yes_no(release, "q22_gate", bool(units))
    for index, unit in enumerate(units):
        _put_row(
            release,
            values,
            index,
            [
                ("q22_facility_name", unit.facility.name),
                *_address_pairs(
                    unit.facility.address,
                    "q22_facility_street",
                    "q22_facility_street2",
                    "q22_facility_city",
                    "q22_facility_state",
                    "q22_facility_zip",
                ),
                ("q22_describe", unit.description),
            ],
            problems,
        )
        if len(unit.who_has_access) > 1:
            problems.append(
                "q22: the form prints one access-holder block; the entry "
                f"names {len(unit.who_has_access)} people"
            )
        if unit.who_has_access:
            values["q22_access_name"] = Text(unit.who_has_access[0])
        if unit.still_have is not None:
            values["q22_still_have"] = yes_no(
                release, "q22_still_have", unit.still_have
            )

    held = _entries(case_file, HeldForAnother)
    values["q23_gate"] = yes_no(release, "q23_gate", bool(held))
    for index, item in enumerate(held):
        _put_row(
            release,
            values,
            index,
            [
                ("q23_owner_name", item.owner.name),
                *_address_pairs(
                    item.owner.address,
                    "q23_owner_street",
                    "q23_owner_street2",
                    "q23_owner_city",
                    "q23_owner_state",
                    "q23_owner_zip",
                ),
                # The location is one stored narrative; the street box's
                # first line carries it (the 106A/B street-box precedent).
                ("q23_where_street", item.location),
                ("q23_describe", item.description),
                ("q23_value", _money(item.value)),
            ],
            problems,
        )


def _q24_q28(
    release: FormRelease,
    values: FieldValues,
    case_file: CaseFile,
    problems: list[str],
) -> None:
    from ..sofa import (
        EnvironmentalNotice,
        EnvironmentalProceeding,
        FinancialStatementIssued,
    )

    notices = _entries(case_file, EnvironmentalNotice)
    for question, kind in (
        ("q24", "liability_notice_received"),
        ("q25", "release_reported"),
    ):
        matching = [n for n in notices if n.kind == kind]
        values[f"{question}_gate"] = yes_no(release, f"{question}_gate", bool(matching))
        for index, notice in enumerate(matching):
            _put_row(
                release,
                values,
                index,
                [
                    (f"{question}_site_name", notice.site.name),
                    *_address_pairs(
                        notice.site.address,
                        f"{question}_site_street",
                        f"{question}_site_street2",
                        f"{question}_site_city",
                        f"{question}_site_state",
                        f"{question}_site_zip",
                    ),
                    (f"{question}_gov_unit", notice.governmental_unit.name),
                    *_address_pairs(
                        notice.governmental_unit.address,
                        f"{question}_gov_street",
                        None,
                        f"{question}_gov_city",
                        f"{question}_gov_state",
                        f"{question}_gov_zip",
                    ),
                    (f"{question}_law", notice.environmental_law),
                    (f"{question}_date", notice.date and format_date(notice.date)),
                ],
                problems,
            )

    proceedings = _entries(case_file, EnvironmentalProceeding)
    if not proceedings:
        values["q26_gate_and_status"] = Option("no")
    else:
        # The merged gate+status group: Yes and the status are two
        # appearance states of one wrongly-exclusive field.
        states = ["yes"]
        status = proceedings[0].status
        if status is not None:
            states.append(status.replace("_", " "))
        values["q26_gate_and_status"] = WidgetStates(states=tuple(states))
        for index, proceeding in enumerate(proceedings):
            _put_row(
                release,
                values,
                index,
                [
                    ("q26_case_title", proceeding.case_title),
                    ("q26_case_number", proceeding.case_number),
                    ("q26_case_nature", proceeding.nature_of_case),
                    ("q26_court_name", proceeding.court.name),
                    *_address_pairs(
                        proceeding.court.address,
                        "q26_court_street",
                        None,
                        "q26_court_city",
                        "q26_court_state",
                        "q26_court_zip",
                    ),
                ],
                problems,
            )

    businesses = _entries(case_file, BusinessConnection)
    values["q27_gate"] = yes_no(release, "q27_gate", bool(businesses))
    connections = {member for b in businesses for member in b.connection}
    for member in connections:
        values[_CONNECTION_BOXES[member]] = Check()
    for index, business in enumerate(businesses):
        if index >= 3:
            problems.append("q27: the form prints 3 rows; more businesses overflow")
            continue
        letter = "abc"[index]
        _put_row(
            release,
            values,
            index,
            [
                ("q27_business_name", business.business.name),
                *_address_pairs(
                    business.business.address,
                    "q27_business_street",
                    "q27_business_street2",
                    "q27_business_city",
                    "q27_business_state",
                    "q27_business_zip",
                ),
                ("q27_nature", business.nature_of_business),
                (
                    "q27_date_from",
                    business.from_date and format_date(business.from_date),
                ),
            ],
            problems,
        )
        if business.ein:
            digits = "".join(ch for ch in business.ein if ch.isdigit())
            _named(
                release,
                values,
                "q27_ein",
                f"Debtor1.Employer Identification Number27{letter}",
                Text(digits),
            )
        if business.to_date is not None:
            if index == 2:
                # Row c's To box is the PDF's second widget of row b's.
                second = businesses[1].to_date if len(businesses) > 1 else None
                if business.to_date != second:
                    problems.append(
                        "q27: row three's To box is the PDF's second widget "
                        "of row two's and can only repeat its value; "
                        f"{business.to_date!r} differs"
                    )
            else:
                row_fill(
                    release,
                    values,
                    "q27_date_to",
                    index,
                    Text(format_date(business.to_date)),
                    problems,
                )

    statements = _entries(case_file, FinancialStatementIssued)
    values["q28_gate"] = yes_no(release, "q28_gate", bool(statements))
    for index, statement in enumerate(statements):
        _put_row(
            release,
            values,
            index,
            [
                ("q28_name", statement.recipient.name),
                *_address_pairs(
                    statement.recipient.address,
                    "q28_street",
                    "q28_street2",
                    "q28_city",
                    "q28_state",
                    "q28_zip",
                ),
                (
                    "q28_date_issued",
                    statement.date_issued and format_date(statement.date_issued),
                ),
            ],
            problems,
        )


def project_b107_0425(release: FormRelease, case_file: CaseFile) -> FieldValues:
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

    _q1_q3(release, values, case_file, problems)
    _q4_q5(release, values, case_file, problems)
    _q6(release, values, case_file, problems)
    _q7_q8(release, values, case_file, problems)
    _q9_q12(release, values, case_file, problems)
    _q13_q17(release, values, case_file, problems)
    _q18_q23(release, values, case_file, problems)
    _q24_q28(release, values, case_file, problems)

    # Part 12 — signatures: dates only, plus the preparer question. The
    # attached-pages box is packet assembly's (it owns continuation sheets).
    for role, field_id in (
        ("debtor_1", "sign.debtor1_date"),
        ("debtor_2", "sign.debtor2_date"),
    ):
        debtor = case_file.debtor(role)
        if debtor is not None and debtor.signed_at:
            values[field_id] = Text(format_date(debtor.signed_at))
    preparer = any(
        p.role == "bankruptcy_petition_preparer" for p in case_file.filing_professionals
    )
    values["sign.paid_preparer"] = yes_no(release, "sign.paid_preparer", preparer)
    if preparer:
        named = next(
            full_name(p.name)
            for p in case_file.filing_professionals
            if p.role == "bankruptcy_petition_preparer"
        )
        if named:
            values["sign.preparer_name"] = Text(named)

    if problems:
        raise FormProjectionError(sorted(problems))
    return values
