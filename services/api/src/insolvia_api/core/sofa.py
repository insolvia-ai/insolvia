"""The Statement of Financial Affairs (B107) as one typed-entry table.

B107 is twenty-eight questions covering some two dozen unrelated repeating
shapes. Two dozen tables sharing nothing but provenance would be a
transcription, not a model (docs/reference/case-data-model.md, "The SOFA is
one typed-entry table"), so a SOFA answer is:

    sofa_entry { id, case_id, entry_type, payload, provenance }

`entry_type` is a closed enum. `payload` is a FROZEN DATACLASS — one per entry
type, a discriminated union — produced by one parse function per type behind
the dispatch table at the bottom of this file. It is not a loose dict: an
untyped payload will not cross the core boundary under this repo's strict
typing, and the parse functions are the only thing standing between a generic
column and unvalidated data. The cost is honest — roughly two dozen
hand-written parsers, each with per-type tests — and the benefit is that the
annual form cycle adds or retires a question without a migration.

Provenance addresses payload members as `payload.<field>` (nested,
`payload.creditor.name`), exactly as any other nested record; `entry_type`
itself is case data and carries an entry like any populated field.

A payload cannot be interpreted without its type, so `payload` WITHOUT
`entry_type` is the one shape refused outright — not a completeness rule (an
entry may carry a type and no payload yet) but a shape one: there is no parser
to hand the payload to.

The types are named for what the question asks about, never numbered — B107's
question numbers are a rendering that the annual cycle reorders.
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import (
    Address,
    boolean,
    choice,
    choice_list,
    form_date,
    mapping,
    money,
    narrative,
    parse_address,
    string_list,
    text,
)

# Which debtor a per-debtor answer belongs to. Narrower than the schedules'
# DEBTOR_ATTRIBUTION: B107's per-debtor rows have no "and another" box.
SOFA_DEBTORS: Final = ("debtor_1", "debtor_2", "both")

# The status of a court matter — Q9 and Q26 print the same three boxes.
LEGAL_STATUSES: Final = ("pending", "on_appeal", "concluded")

# Q4/Q5's income sources.
INCOME_KINDS: Final = ("wages_and_commissions", "operating_a_business", "other")

# Q6's "was this payment for..." checkboxes.
PAYMENT_PURPOSES: Final = (
    "mortgage",
    "car",
    "credit_card",
    "loan_repayment",
    "suppliers_or_vendors",
    "other",
)

# Q10's four actions, one entry each.
REPOSSESSION_ACTIONS: Final = ("repossessed", "foreclosed", "garnished", "attached")

# Q20's account types.
ACCOUNT_TYPES: Final = ("checking", "savings", "money_market", "brokerage", "other")

# Q24/Q25 fold into one entry type: who told whom.
ENVIRONMENTAL_NOTICE_KINDS: Final = ("liability_notice_received", "release_reported")

# Q27's "connection to the business" checkboxes, in the printed order. The
# 04/25 revision prints five: `llc_member` is the "member of a limited
# liability company (LLC) or limited liability partnership (LLP)" box the
# original four-member vocabulary missed.
BUSINESS_CONNECTIONS: Final = (
    "sole_proprietor",
    "llc_member",
    "partner",
    "officer_or_director",
    "owner_of_5_percent",
)

# Q1's answer.
MARITAL_STATUSES: Final = ("married", "not_married")


@dataclass(frozen=True)
class Party:
    """A name-and-address block — the shape almost every B107 column prints."""

    name: str | None = None
    address: Address = field(default_factory=Address)


# ── Payloads, in the order the doc lists the shapes ─────────────


@dataclass(frozen=True)
class PriorAddress:
    """Q: other places lived in the last 3 years."""

    which_debtor: str | None = None
    address: Address = field(default_factory=Address)
    from_date: str | None = None
    to_date: str | None = None


@dataclass(frozen=True)
class IncomeByPeriod:
    """Q: income from employment, business, and other sources, by period."""

    which_debtor: str | None = None
    kind: str | None = None
    description: str | None = None
    period_start: str | None = None
    period_end: str | None = None
    gross_amount: str | None = None


@dataclass(frozen=True)
class CreditorPayment:
    """Q: payments to creditors within 90 days over the reporting floor."""

    creditor: Party = field(default_factory=Party)
    dates: tuple[str, ...] = ()
    total_paid: str | None = None
    amount_still_owed: str | None = None
    payment_for: tuple[str, ...] = ()
    payment_for_other: str | None = None


@dataclass(frozen=True)
class InsiderPayment:
    """Q: payments to insiders within 1 year."""

    insider: Party = field(default_factory=Party)
    relationship: str | None = None
    dates: tuple[str, ...] = ()
    total_paid: str | None = None
    amount_still_owed: str | None = None
    reason: str | None = None


@dataclass(frozen=True)
class InsiderBenefitPayment:
    """Q: payments or transfers on a debt that benefited an insider."""

    recipient: Party = field(default_factory=Party)
    insider_name: str | None = None
    dates: tuple[str, ...] = ()
    total_paid: str | None = None
    reason: str | None = None


@dataclass(frozen=True)
class Lawsuit:
    """Q: lawsuits and court actions within 1 year."""

    case_title: str | None = None
    case_number: str | None = None
    nature_of_case: str | None = None
    court: Party = field(default_factory=Party)
    status: str | None = None


@dataclass(frozen=True)
class Repossession:
    """Q: property repossessed, foreclosed, garnished or attached."""

    creditor: Party = field(default_factory=Party)
    action: str | None = None
    description: str | None = None
    date: str | None = None
    value: str | None = None


@dataclass(frozen=True)
class Setoff:
    """Q: setoffs by creditors within 90 days."""

    creditor: Party = field(default_factory=Party)
    description: str | None = None
    date: str | None = None
    amount: str | None = None


@dataclass(frozen=True)
class Receivership:
    """Q: property in the hands of an assignee, receiver or custodian."""

    custodian: Party = field(default_factory=Party)
    description: str | None = None
    value: str | None = None
    case_title: str | None = None
    case_number: str | None = None
    court: Party = field(default_factory=Party)
    date: str | None = None


@dataclass(frozen=True)
class Gift:
    """Q: gifts over the reporting floor within 2 years."""

    recipient: Party = field(default_factory=Party)
    relationship: str | None = None
    description: str | None = None
    dates: tuple[str, ...] = ()
    value: str | None = None


@dataclass(frozen=True)
class CharitableContribution:
    """Q: charitable contributions over the reporting floor within 2 years."""

    organization: Party = field(default_factory=Party)
    description: str | None = None
    dates: tuple[str, ...] = ()
    value: str | None = None


@dataclass(frozen=True)
class Loss:
    """Q: losses from theft, fire, disaster or gambling within 1 year."""

    description: str | None = None
    insurance_coverage: str | None = None
    date: str | None = None
    value: str | None = None


@dataclass(frozen=True)
class ConsultantPayment:
    """Q: payments for bankruptcy consultants and preparers."""

    person: Party = field(default_factory=Party)
    email_or_website: str | None = None
    who_made_payment: str | None = None
    description: str | None = None
    date: str | None = None
    amount: str | None = None


@dataclass(frozen=True)
class CreditorAssistancePayment:
    """Q: payments to anyone who promised to help deal with creditors."""

    person: Party = field(default_factory=Party)
    description: str | None = None
    date: str | None = None
    amount: str | None = None


@dataclass(frozen=True)
class PropertyTransfer:
    """Q: transfers outside the ordinary course within 2 years."""

    transferee: Party = field(default_factory=Party)
    relationship: str | None = None
    description: str | None = None
    value_received: str | None = None
    date: str | None = None


@dataclass(frozen=True)
class SelfSettledTrust:
    """Q: transfers to a self-settled trust within 10 years."""

    trust_name: str | None = None
    description: str | None = None
    date: str | None = None


@dataclass(frozen=True)
class ClosedAccount:
    """Q: accounts closed, sold or moved within 1 year."""

    institution: Party = field(default_factory=Party)
    account_last4: str | None = None
    account_type: str | None = None
    date_closed: str | None = None
    last_balance: str | None = None


@dataclass(frozen=True)
class SafeDepositBox:
    """Q: safe deposit boxes within 1 year."""

    institution: Party = field(default_factory=Party)
    who_has_access: tuple[str, ...] = ()
    description: str | None = None
    still_have: bool | None = None


@dataclass(frozen=True)
class StorageUnit:
    """Q: storage units within 1 year."""

    facility: Party = field(default_factory=Party)
    who_has_access: tuple[str, ...] = ()
    description: str | None = None
    still_have: bool | None = None


@dataclass(frozen=True)
class HeldForAnother:
    """Q: property held or controlled for someone else."""

    owner: Party = field(default_factory=Party)
    location: str | None = None
    description: str | None = None
    value: str | None = None


@dataclass(frozen=True)
class EnvironmentalNotice:
    """Q: environmental-law notices, in either direction — `kind` says who
    notified whom."""

    kind: str | None = None
    site: Party = field(default_factory=Party)
    governmental_unit: Party = field(default_factory=Party)
    environmental_law: str | None = None
    date: str | None = None


@dataclass(frozen=True)
class EnvironmentalProceeding:
    """Q: judicial or administrative proceedings under environmental law."""

    case_title: str | None = None
    case_number: str | None = None
    court: Party = field(default_factory=Party)
    nature_of_case: str | None = None
    status: str | None = None


@dataclass(frozen=True)
class BusinessConnection:
    """Q: businesses connected to the debtor within 4 years."""

    business: Party = field(default_factory=Party)
    nature_of_business: str | None = None
    ein: str | None = None
    from_date: str | None = None
    to_date: str | None = None
    connection: tuple[str, ...] = ()


@dataclass(frozen=True)
class FinancialStatementIssued:
    """Q: financial statements given about the debtor within 2 years."""

    recipient: Party = field(default_factory=Party)
    date_issued: str | None = None


@dataclass(frozen=True)
class MaritalStatus:
    """Q: current marital status — a singleton, not a repeating row."""

    status: str | None = None


@dataclass(frozen=True)
class CommunityPropertyResidence:
    """Q: community property states lived in with a spouse within 8 years."""

    state: str | None = None


@dataclass(frozen=True)
class ConsumerDebtDeclaration:
    """Q: are the debts primarily consumer debts? — the gate on the
    payments-to-creditors question, and a singleton."""

    primarily_consumer_debts: bool | None = None


SofaPayload = (
    PriorAddress
    | IncomeByPeriod
    | CreditorPayment
    | InsiderPayment
    | InsiderBenefitPayment
    | Lawsuit
    | Repossession
    | Setoff
    | Receivership
    | Gift
    | CharitableContribution
    | Loss
    | ConsultantPayment
    | CreditorAssistancePayment
    | PropertyTransfer
    | SelfSettledTrust
    | ClosedAccount
    | SafeDepositBox
    | StorageUnit
    | HeldForAnother
    | EnvironmentalNotice
    | EnvironmentalProceeding
    | BusinessConnection
    | FinancialStatementIssued
    | MaritalStatus
    | CommunityPropertyResidence
    | ConsumerDebtDeclaration
)


# ── The parsers, one per type ───────────────────────────────────


def _party(value: object, path: str, errors: dict[str, str]) -> Party:
    raw = mapping(value, path, errors)
    return Party(
        name=text(raw.get("name"), f"{path}.name", errors),
        address=parse_address(raw.get("address"), f"{path}.address", errors),
    )


def _date_list(value: object, path: str, errors: dict[str, str]) -> tuple[str, ...]:
    """A "Dates" box holding one or more calendar dates. Attributed whole by
    provenance — plain strings carry no ids to address elements by."""
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors[path] = "Must be a list of dates."
        return ()
    dates: list[str] = []
    for index, raw in enumerate(value):
        parsed = form_date(raw, f"{path}[{index}]", errors)
        if parsed is not None:
            dates.append(parsed)
    return tuple(dates)


def _parse_prior_address(raw: Mapping[str, object], e: dict[str, str]) -> PriorAddress:
    return PriorAddress(
        which_debtor=choice(
            raw.get("which_debtor"), SOFA_DEBTORS, "payload.which_debtor", e
        ),
        address=parse_address(raw.get("address"), "payload.address", e),
        from_date=form_date(raw.get("from_date"), "payload.from_date", e),
        to_date=form_date(raw.get("to_date"), "payload.to_date", e),
    )


def _parse_income_by_period(
    raw: Mapping[str, object], e: dict[str, str]
) -> IncomeByPeriod:
    return IncomeByPeriod(
        which_debtor=choice(
            raw.get("which_debtor"), SOFA_DEBTORS, "payload.which_debtor", e
        ),
        kind=choice(raw.get("kind"), INCOME_KINDS, "payload.kind", e),
        description=text(raw.get("description"), "payload.description", e),
        period_start=form_date(raw.get("period_start"), "payload.period_start", e),
        period_end=form_date(raw.get("period_end"), "payload.period_end", e),
        gross_amount=money(raw.get("gross_amount"), "payload.gross_amount", e),
    )


def _parse_creditor_payment(
    raw: Mapping[str, object], e: dict[str, str]
) -> CreditorPayment:
    return CreditorPayment(
        creditor=_party(raw.get("creditor"), "payload.creditor", e),
        dates=_date_list(raw.get("dates"), "payload.dates", e),
        total_paid=money(raw.get("total_paid"), "payload.total_paid", e),
        amount_still_owed=money(
            raw.get("amount_still_owed"), "payload.amount_still_owed", e
        ),
        payment_for=choice_list(
            raw.get("payment_for"), PAYMENT_PURPOSES, "payload.payment_for", e
        ),
        payment_for_other=text(
            raw.get("payment_for_other"), "payload.payment_for_other", e
        ),
    )


def _parse_insider_payment(
    raw: Mapping[str, object], e: dict[str, str]
) -> InsiderPayment:
    return InsiderPayment(
        insider=_party(raw.get("insider"), "payload.insider", e),
        relationship=text(raw.get("relationship"), "payload.relationship", e),
        dates=_date_list(raw.get("dates"), "payload.dates", e),
        total_paid=money(raw.get("total_paid"), "payload.total_paid", e),
        amount_still_owed=money(
            raw.get("amount_still_owed"), "payload.amount_still_owed", e
        ),
        reason=narrative(raw.get("reason"), "payload.reason", e),
    )


def _parse_insider_benefit_payment(
    raw: Mapping[str, object], e: dict[str, str]
) -> InsiderBenefitPayment:
    return InsiderBenefitPayment(
        recipient=_party(raw.get("recipient"), "payload.recipient", e),
        insider_name=text(raw.get("insider_name"), "payload.insider_name", e),
        dates=_date_list(raw.get("dates"), "payload.dates", e),
        total_paid=money(raw.get("total_paid"), "payload.total_paid", e),
        reason=narrative(raw.get("reason"), "payload.reason", e),
    )


def _parse_lawsuit(raw: Mapping[str, object], e: dict[str, str]) -> Lawsuit:
    return Lawsuit(
        case_title=text(raw.get("case_title"), "payload.case_title", e),
        case_number=text(raw.get("case_number"), "payload.case_number", e, limit=64),
        nature_of_case=text(raw.get("nature_of_case"), "payload.nature_of_case", e),
        court=_party(raw.get("court"), "payload.court", e),
        status=choice(raw.get("status"), LEGAL_STATUSES, "payload.status", e),
    )


def _parse_repossession(raw: Mapping[str, object], e: dict[str, str]) -> Repossession:
    return Repossession(
        creditor=_party(raw.get("creditor"), "payload.creditor", e),
        action=choice(raw.get("action"), REPOSSESSION_ACTIONS, "payload.action", e),
        description=narrative(raw.get("description"), "payload.description", e),
        date=form_date(raw.get("date"), "payload.date", e),
        value=money(raw.get("value"), "payload.value", e),
    )


def _parse_setoff(raw: Mapping[str, object], e: dict[str, str]) -> Setoff:
    return Setoff(
        creditor=_party(raw.get("creditor"), "payload.creditor", e),
        description=narrative(raw.get("description"), "payload.description", e),
        date=form_date(raw.get("date"), "payload.date", e),
        amount=money(raw.get("amount"), "payload.amount", e),
    )


def _parse_receivership(raw: Mapping[str, object], e: dict[str, str]) -> Receivership:
    return Receivership(
        custodian=_party(raw.get("custodian"), "payload.custodian", e),
        description=narrative(raw.get("description"), "payload.description", e),
        value=money(raw.get("value"), "payload.value", e),
        case_title=text(raw.get("case_title"), "payload.case_title", e),
        case_number=text(raw.get("case_number"), "payload.case_number", e, limit=64),
        court=_party(raw.get("court"), "payload.court", e),
        date=form_date(raw.get("date"), "payload.date", e),
    )


def _parse_gift(raw: Mapping[str, object], e: dict[str, str]) -> Gift:
    return Gift(
        recipient=_party(raw.get("recipient"), "payload.recipient", e),
        relationship=text(raw.get("relationship"), "payload.relationship", e),
        description=narrative(raw.get("description"), "payload.description", e),
        dates=_date_list(raw.get("dates"), "payload.dates", e),
        value=money(raw.get("value"), "payload.value", e),
    )


def _parse_charitable_contribution(
    raw: Mapping[str, object], e: dict[str, str]
) -> CharitableContribution:
    return CharitableContribution(
        organization=_party(raw.get("organization"), "payload.organization", e),
        description=narrative(raw.get("description"), "payload.description", e),
        dates=_date_list(raw.get("dates"), "payload.dates", e),
        value=money(raw.get("value"), "payload.value", e),
    )


def _parse_loss(raw: Mapping[str, object], e: dict[str, str]) -> Loss:
    return Loss(
        description=narrative(raw.get("description"), "payload.description", e),
        insurance_coverage=narrative(
            raw.get("insurance_coverage"), "payload.insurance_coverage", e
        ),
        date=form_date(raw.get("date"), "payload.date", e),
        value=money(raw.get("value"), "payload.value", e),
    )


def _parse_consultant_payment(
    raw: Mapping[str, object], e: dict[str, str]
) -> ConsultantPayment:
    return ConsultantPayment(
        person=_party(raw.get("person"), "payload.person", e),
        email_or_website=text(
            raw.get("email_or_website"), "payload.email_or_website", e
        ),
        who_made_payment=text(
            raw.get("who_made_payment"), "payload.who_made_payment", e
        ),
        description=narrative(raw.get("description"), "payload.description", e),
        date=form_date(raw.get("date"), "payload.date", e),
        amount=money(raw.get("amount"), "payload.amount", e),
    )


def _parse_creditor_assistance_payment(
    raw: Mapping[str, object], e: dict[str, str]
) -> CreditorAssistancePayment:
    return CreditorAssistancePayment(
        person=_party(raw.get("person"), "payload.person", e),
        description=narrative(raw.get("description"), "payload.description", e),
        date=form_date(raw.get("date"), "payload.date", e),
        amount=money(raw.get("amount"), "payload.amount", e),
    )


def _parse_property_transfer(
    raw: Mapping[str, object], e: dict[str, str]
) -> PropertyTransfer:
    return PropertyTransfer(
        transferee=_party(raw.get("transferee"), "payload.transferee", e),
        relationship=text(raw.get("relationship"), "payload.relationship", e),
        description=narrative(raw.get("description"), "payload.description", e),
        value_received=narrative(
            raw.get("value_received"), "payload.value_received", e
        ),
        date=form_date(raw.get("date"), "payload.date", e),
    )


def _parse_self_settled_trust(
    raw: Mapping[str, object], e: dict[str, str]
) -> SelfSettledTrust:
    return SelfSettledTrust(
        trust_name=text(raw.get("trust_name"), "payload.trust_name", e),
        description=narrative(raw.get("description"), "payload.description", e),
        date=form_date(raw.get("date"), "payload.date", e),
    )


def _parse_closed_account(
    raw: Mapping[str, object], e: dict[str, str]
) -> ClosedAccount:
    account_last4 = text(raw.get("account_last4"), "payload.account_last4", e, limit=4)
    if account_last4 is not None and not account_last4.isdigit():
        e["payload.account_last4"] = "Must be up to four digits."
        account_last4 = None
    return ClosedAccount(
        institution=_party(raw.get("institution"), "payload.institution", e),
        account_last4=account_last4,
        account_type=choice(
            raw.get("account_type"), ACCOUNT_TYPES, "payload.account_type", e
        ),
        date_closed=form_date(raw.get("date_closed"), "payload.date_closed", e),
        last_balance=money(raw.get("last_balance"), "payload.last_balance", e),
    )


def _parse_safe_deposit_box(
    raw: Mapping[str, object], e: dict[str, str]
) -> SafeDepositBox:
    return SafeDepositBox(
        institution=_party(raw.get("institution"), "payload.institution", e),
        who_has_access=string_list(
            raw.get("who_has_access"), "payload.who_has_access", e
        ),
        description=narrative(raw.get("description"), "payload.description", e),
        still_have=boolean(raw.get("still_have"), "payload.still_have", e),
    )


def _parse_storage_unit(raw: Mapping[str, object], e: dict[str, str]) -> StorageUnit:
    return StorageUnit(
        facility=_party(raw.get("facility"), "payload.facility", e),
        who_has_access=string_list(
            raw.get("who_has_access"), "payload.who_has_access", e
        ),
        description=narrative(raw.get("description"), "payload.description", e),
        still_have=boolean(raw.get("still_have"), "payload.still_have", e),
    )


def _parse_held_for_another(
    raw: Mapping[str, object], e: dict[str, str]
) -> HeldForAnother:
    return HeldForAnother(
        owner=_party(raw.get("owner"), "payload.owner", e),
        location=narrative(raw.get("location"), "payload.location", e),
        description=narrative(raw.get("description"), "payload.description", e),
        value=money(raw.get("value"), "payload.value", e),
    )


def _parse_environmental_notice(
    raw: Mapping[str, object], e: dict[str, str]
) -> EnvironmentalNotice:
    return EnvironmentalNotice(
        kind=choice(raw.get("kind"), ENVIRONMENTAL_NOTICE_KINDS, "payload.kind", e),
        site=_party(raw.get("site"), "payload.site", e),
        governmental_unit=_party(
            raw.get("governmental_unit"), "payload.governmental_unit", e
        ),
        environmental_law=text(
            raw.get("environmental_law"), "payload.environmental_law", e
        ),
        date=form_date(raw.get("date"), "payload.date", e),
    )


def _parse_environmental_proceeding(
    raw: Mapping[str, object], e: dict[str, str]
) -> EnvironmentalProceeding:
    return EnvironmentalProceeding(
        case_title=text(raw.get("case_title"), "payload.case_title", e),
        case_number=text(raw.get("case_number"), "payload.case_number", e, limit=64),
        court=_party(raw.get("court"), "payload.court", e),
        nature_of_case=text(raw.get("nature_of_case"), "payload.nature_of_case", e),
        status=choice(raw.get("status"), LEGAL_STATUSES, "payload.status", e),
    )


def _parse_business_connection(
    raw: Mapping[str, object], e: dict[str, str]
) -> BusinessConnection:
    return BusinessConnection(
        business=_party(raw.get("business"), "payload.business", e),
        nature_of_business=text(
            raw.get("nature_of_business"), "payload.nature_of_business", e
        ),
        ein=text(raw.get("ein"), "payload.ein", e, limit=20),
        from_date=form_date(raw.get("from_date"), "payload.from_date", e),
        to_date=form_date(raw.get("to_date"), "payload.to_date", e),
        connection=choice_list(
            raw.get("connection"), BUSINESS_CONNECTIONS, "payload.connection", e
        ),
    )


def _parse_financial_statement_issued(
    raw: Mapping[str, object], e: dict[str, str]
) -> FinancialStatementIssued:
    return FinancialStatementIssued(
        recipient=_party(raw.get("recipient"), "payload.recipient", e),
        date_issued=form_date(raw.get("date_issued"), "payload.date_issued", e),
    )


def _parse_marital_status(
    raw: Mapping[str, object], e: dict[str, str]
) -> MaritalStatus:
    return MaritalStatus(
        status=choice(raw.get("status"), MARITAL_STATUSES, "payload.status", e)
    )


def _parse_community_property_residence(
    raw: Mapping[str, object], e: dict[str, str]
) -> CommunityPropertyResidence:
    return CommunityPropertyResidence(
        state=text(raw.get("state"), "payload.state", e, limit=40)
    )


def _parse_consumer_debt_declaration(
    raw: Mapping[str, object], e: dict[str, str]
) -> ConsumerDebtDeclaration:
    return ConsumerDebtDeclaration(
        primarily_consumer_debts=boolean(
            raw.get("primarily_consumer_debts"), "payload.primarily_consumer_debts", e
        )
    )


# The closed enum and the dispatch table, in one structure so a type cannot be
# added to one and not the other. dict order is the order the questions run on
# the current revision, which nothing reads — entries are listed per case in
# creation order like every other collection.
PAYLOAD_PARSERS: Final[
    Mapping[str, Callable[[Mapping[str, object], dict[str, str]], SofaPayload]]
] = {
    "marital_status": _parse_marital_status,
    "prior_address": _parse_prior_address,
    "community_property_residence": _parse_community_property_residence,
    "income_by_period": _parse_income_by_period,
    "consumer_debt_declaration": _parse_consumer_debt_declaration,
    "creditor_payment": _parse_creditor_payment,
    "insider_payment": _parse_insider_payment,
    "insider_benefit_payment": _parse_insider_benefit_payment,
    "lawsuit": _parse_lawsuit,
    "repossession": _parse_repossession,
    "setoff": _parse_setoff,
    "receivership": _parse_receivership,
    "gift": _parse_gift,
    "charitable_contribution": _parse_charitable_contribution,
    "loss": _parse_loss,
    "consultant_payment": _parse_consultant_payment,
    "creditor_assistance_payment": _parse_creditor_assistance_payment,
    "property_transfer": _parse_property_transfer,
    "self_settled_trust": _parse_self_settled_trust,
    "closed_account": _parse_closed_account,
    "safe_deposit_box": _parse_safe_deposit_box,
    "storage_unit": _parse_storage_unit,
    "held_for_another": _parse_held_for_another,
    "environmental_notice": _parse_environmental_notice,
    "environmental_proceeding": _parse_environmental_proceeding,
    "business_connection": _parse_business_connection,
    "financial_statement_issued": _parse_financial_statement_issued,
}

SOFA_ENTRY_TYPES: Final = tuple(PAYLOAD_PARSERS)


@dataclass(frozen=True)
class SofaEntryBody:
    entry_type: str | None = None
    payload: SofaPayload | None = None


def parse_sofa_entry(payload: Mapping[str, object]) -> SofaEntryBody:
    errors: dict[str, str] = {}
    entry_type = choice(
        payload.get("entry_type"), SOFA_ENTRY_TYPES, "entry_type", errors
    )

    raw_payload = payload.get("payload")
    parsed_payload: SofaPayload | None = None
    if raw_payload is not None:
        if entry_type is None:
            # The one shape refused outright: without a type there is no parser
            # to hand this to, and storing it untyped is exactly what the
            # dispatch table exists to prevent.
            errors.setdefault(
                "entry_type", "Required when a payload is sent — it names the parser."
            )
        else:
            parsed_payload = PAYLOAD_PARSERS[entry_type](
                mapping(raw_payload, "payload", errors), errors
            )

    if errors:
        raise FieldValidationError(errors)
    return SofaEntryBody(entry_type=entry_type, payload=parsed_payload)


SOFA_ENTRY: EntityKind[SofaEntryBody] = EntityKind(
    name="sofa_entry",
    collection="sofa_entries",
    sk_prefix="SOFA_ENTRY",
    parse_body=parse_sofa_entry,
)
