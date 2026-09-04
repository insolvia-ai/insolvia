"""The claim record — one entity spanning 106D and both parts of 106E/F.

One `claim` discriminated by `class` rather than three per-schedule tables
(docs/reference/case-data-model.md, "Creditors and claims"): the class is the
one fact that decides which schedule a claim prints on, and a claim moves
between schedules during intake — "actually, that judgment is secured" — which
under three tables would be a delete-and-recreate that orphaned provenance.

The class-specific members are validated for shape but NOT gated on the class,
and that is progressive intake rather than looseness: the class itself is
optional (it may be the last thing decided), so a collateral description typed
before the class is chosen must persist. Which members a given class actually
prints is the forms engine's mapping.

Two amounts are arithmetic and deliberately absent: the unsecured portion of a
secured claim (amount less collateral value) and a priority claim's total
(priority plus nonpriority). Storing either would mean owning a reconciliation
bug.

`creditor_id` names a creditor record but is not checked against the creditor
collection here — storage validates shape and type only, and a claim typed
before its creditor is saved must persist. Dangling references are the
completeness gate's to flag (9.6).
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Final

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import (
    DEBTOR_ATTRIBUTION,
    Address,
    boolean,
    choice,
    choice_list,
    form_date,
    mapping,
    money,
    parse_address,
    text,
)
from .provenance import ADDRESSABLE_ID_RE

CLAIM_CLASSES: Final = ("secured", "priority_unsecured", "nonpriority_unsecured")

# 106D line 2's "check all that apply". `other` carries `lien_nature_other`.
LIEN_NATURES: Final = ("agreement", "statutory", "judgment", "other")

# Only the three categories PRINTED on 106E/F are enumerated; the fuller §507
# taxonomy lives in the instruction booklet and belongs to the forms engine's
# mapping, not here. `other` carries `priority_type_other`.
PRIORITY_TYPES: Final = (
    "domestic_support",
    "tax_and_government",
    "death_or_injury_while_intoxicated",
    "other",
)

# 106E/F Part 2's type line. `other` carries `nonpriority_type_other`.
NONPRIORITY_TYPES: Final = (
    "student_loan",
    "separation_or_divorce",
    "pension_or_profit_sharing",
    "other",
)


@dataclass(frozen=True)
class NoticeParty:
    """106D Part 2 / 106E/F Part 3: others to be notified about a debt.
    Carries its own `id` so provenance addresses it as
    `notice_parties[<id>].name` — position would reattach on reorder."""

    id: str
    name: str | None = None
    address: Address = field(default_factory=Address)
    account_last4: str | None = None


@dataclass(frozen=True)
class ClaimBody:
    creditor_id: str | None = None
    claim_class: str | None = None
    account_last4: str | None = None
    date_incurred: str | None = None
    amount: str | None = None
    contingent: bool | None = None
    unliquidated: bool | None = None
    disputed: bool | None = None
    subject_to_offset: bool | None = None
    who_incurred: str | None = None
    community_debt: bool | None = None
    notice_parties: tuple[NoticeParty, ...] = ()
    # class: secured
    collateral_description: str | None = None
    collateral_value: str | None = None
    lien_nature: tuple[str, ...] = ()
    lien_nature_other: str | None = None
    # class: priority_unsecured
    priority_amount: str | None = None
    nonpriority_amount: str | None = None
    priority_type: str | None = None
    priority_type_other: str | None = None
    # class: nonpriority_unsecured
    nonpriority_type: str | None = None
    nonpriority_type_other: str | None = None


def _last4(value: object, path: str, errors: dict[str, str]) -> str | None:
    """The last-four box. Deliberately NOT a full account number: the forms ask
    for the last four, and a full number stored here would be PII this record
    has no protection for — the same argument that keeps `tax_id` out of the
    debtor record until field-level encryption exists."""
    last4 = text(value, path, errors, limit=4)
    if last4 is not None and not last4.isdigit():
        errors[path] = "Must be up to four digits."
        return None
    return last4


def _parse_notice_parties(
    value: object, errors: dict[str, str]
) -> tuple[NoticeParty, ...]:
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors["notice_parties"] = "Must be a list."
        return ()
    parties: list[NoticeParty] = []
    seen: set[str] = set()
    for index, raw in enumerate(value):
        path = f"notice_parties[{index}]"
        entry = mapping(raw, path, errors)
        # Client-chosen, REQUIRED — the same contract as a debtor's
        # other_names_used, and for the same reason: provenance for this row's
        # fields must name an id the client already knows.
        given_id = entry.get("id")
        if not isinstance(given_id, str) or not ADDRESSABLE_ID_RE.match(given_id):
            errors[f"{path}.id"] = (
                "Required, and must be letters, digits, hyphen or underscore — "
                "generate one per row so provenance can name it."
            )
            continue
        if given_id in seen:
            errors[f"{path}.id"] = "Duplicate id."
            continue
        seen.add(given_id)
        parties.append(
            NoticeParty(
                id=given_id,
                name=text(entry.get("name"), f"{path}.name", errors),
                address=parse_address(entry.get("address"), f"{path}.address", errors),
                account_last4=_last4(
                    entry.get("account_last4"), f"{path}.account_last4", errors
                ),
            )
        )
    return tuple(parties)


def parse_claim(payload: Mapping[str, object]) -> ClaimBody:
    errors: dict[str, str] = {}
    body = ClaimBody(
        creditor_id=text(payload.get("creditor_id"), "creditor_id", errors, limit=64),
        claim_class=choice(
            payload.get("claim_class"), CLAIM_CLASSES, "claim_class", errors
        ),
        account_last4=_last4(payload.get("account_last4"), "account_last4", errors),
        date_incurred=form_date(payload.get("date_incurred"), "date_incurred", errors),
        amount=money(payload.get("amount"), "amount", errors),
        contingent=boolean(payload.get("contingent"), "contingent", errors),
        unliquidated=boolean(payload.get("unliquidated"), "unliquidated", errors),
        disputed=boolean(payload.get("disputed"), "disputed", errors),
        subject_to_offset=boolean(
            payload.get("subject_to_offset"), "subject_to_offset", errors
        ),
        who_incurred=choice(
            payload.get("who_incurred"), DEBTOR_ATTRIBUTION, "who_incurred", errors
        ),
        community_debt=boolean(payload.get("community_debt"), "community_debt", errors),
        notice_parties=_parse_notice_parties(payload.get("notice_parties"), errors),
        collateral_description=text(
            payload.get("collateral_description"),
            "collateral_description",
            errors,
            limit=500,
        ),
        collateral_value=money(
            payload.get("collateral_value"), "collateral_value", errors
        ),
        lien_nature=choice_list(
            payload.get("lien_nature"), LIEN_NATURES, "lien_nature", errors
        ),
        lien_nature_other=text(
            payload.get("lien_nature_other"), "lien_nature_other", errors
        ),
        priority_amount=money(
            payload.get("priority_amount"), "priority_amount", errors
        ),
        nonpriority_amount=money(
            payload.get("nonpriority_amount"), "nonpriority_amount", errors
        ),
        priority_type=choice(
            payload.get("priority_type"), PRIORITY_TYPES, "priority_type", errors
        ),
        priority_type_other=text(
            payload.get("priority_type_other"), "priority_type_other", errors
        ),
        nonpriority_type=choice(
            payload.get("nonpriority_type"),
            NONPRIORITY_TYPES,
            "nonpriority_type",
            errors,
        ),
        nonpriority_type_other=text(
            payload.get("nonpriority_type_other"), "nonpriority_type_other", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


CLAIM: EntityKind[ClaimBody] = EntityKind(
    name="claim",
    collection="claims",
    sk_prefix="CLAIM",
    parse_body=parse_claim,
)
