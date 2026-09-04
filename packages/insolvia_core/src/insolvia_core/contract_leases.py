"""The executory contract / unexpired lease record — 106G, one row each.

The form prints one repeating shape: who the contract is with (a one-line
name and an address, the creditor-matrix argument for a single name line
applying here too — counterparties are predominantly entities) and what the
contract or lease is for. `description` carries the form's "state what the
contract or lease is for and the nature of the debtor's interest, state the
remaining term, and list the contract number of any government contract" in
one narrative box, exactly as the form prints one.

Codebtors reference these rows: 106H's third column has a "Schedule G,
line __" checkbox, and `codebtor.contract_lease_ids` names contract_lease
records the same way `claim_ids` names claims — the line number itself is a
rendering, assigned when the packet prints.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import Address, narrative, parse_address, text


@dataclass(frozen=True)
class ContractLeaseBody:
    counterparty_name: str | None = None
    counterparty_address: Address = field(default_factory=Address)
    description: str | None = None


def parse_contract_lease(payload: Mapping[str, object]) -> ContractLeaseBody:
    errors: dict[str, str] = {}
    body = ContractLeaseBody(
        counterparty_name=text(
            payload.get("counterparty_name"), "counterparty_name", errors
        ),
        counterparty_address=parse_address(
            payload.get("counterparty_address"), "counterparty_address", errors
        ),
        description=narrative(payload.get("description"), "description", errors),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


CONTRACT_LEASE: EntityKind[ContractLeaseBody] = EntityKind(
    name="contract_lease",
    collection="contract_leases",
    sk_prefix="CONTRACT_LEASE",
    parse_body=parse_contract_lease,
)
