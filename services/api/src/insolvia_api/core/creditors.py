"""The creditor record — one deduplicated name-and-address for the matrix.

`creditor` and `claim` are separate on purpose (docs/reference/
case-data-model.md, "Creditors and claims"): the creditor matrix wants one
deduplicated name-and-address per creditor, a debtor may owe the same creditor
twice, and credit-report extraction routinely yields several claims naming one
issuer. There is no reliable external key to dedupe on — the IEPD's creditor
identifier is optional and consumer credit reports mask account numbers — so
the match key is name plus structured address, and matching is a *suggestion to
a human*, never an automatic merge. Nothing in this module dedupes.

The name is ONE line, not a PersonName: creditors are predominantly entities —
banks, servicers, hospitals — and the matrix prints a name line, not four name
parts. A creditor who happens to be a person still fits on the line.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import Address, parse_address, text


@dataclass(frozen=True)
class CreditorBody:
    name: str | None = None
    address: Address = field(default_factory=Address)


def parse_creditor(payload: Mapping[str, object]) -> CreditorBody:
    errors: dict[str, str] = {}
    body = CreditorBody(
        name=text(payload.get("name"), "name", errors),
        address=parse_address(payload.get("address"), "address", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


CREDITOR: EntityKind[CreditorBody] = EntityKind(
    name="creditor",
    collection="creditors",
    sk_prefix="CREDITOR",
    parse_body=parse_creditor,
)
