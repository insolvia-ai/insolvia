"""106H's two record types: the codebtor rows, and the community-property
household member the form's Part 1 asks about.

The form's second column asks WHICH debt, as "Schedule D, line __" /
"Schedule E/F, line __" / "Schedule G, line __". Line numbers are a
rendering, so the model stores the fact instead: `claim_ids` naming the
claim records the codebtor is on, and `contract_lease_ids` naming the
Schedule G rows. The forms engine turns those into line references when it
prints, and a claim reordering does not silently point a codebtor at
somebody else's debt.

`claim_ids` and `contract_lease_ids` are plain string lists, attributed
whole by provenance (the `employer_ids` precedent), and unchecked against
their collections — the usual progressive-intake rule; dangling ids are the
completeness gate's to flag.

`community_household_member` is 106H line 2's spouse-or-former-spouse in a
community property state (and B107 Q3's same fact): who they are, where they
live, which community state, and whether they lived with the debtor. It is
its own record rather than fields on the case because the 8-year lookback
can name more than one person; 106H prints one block, and overflow past it
is the projection's error to raise.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import Address, boolean, parse_address, string_list, text


@dataclass(frozen=True)
class CodebtorBody:
    name: str | None = None
    address: Address = field(default_factory=Address)
    claim_ids: tuple[str, ...] = ()
    contract_lease_ids: tuple[str, ...] = ()


def parse_codebtor(payload: Mapping[str, object]) -> CodebtorBody:
    errors: dict[str, str] = {}
    body = CodebtorBody(
        name=text(payload.get("name"), "name", errors),
        address=parse_address(payload.get("address"), "address", errors),
        claim_ids=string_list(payload.get("claim_ids"), "claim_ids", errors, limit=64),
        contract_lease_ids=string_list(
            payload.get("contract_lease_ids"), "contract_lease_ids", errors, limit=64
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


CODEBTOR: EntityKind[CodebtorBody] = EntityKind(
    name="codebtor",
    collection="codebtors",
    sk_prefix="CODEBTOR",
    parse_body=parse_codebtor,
)


@dataclass(frozen=True)
class CommunityHouseholdMemberBody:
    name: str | None = None
    address: Address = field(default_factory=Address)
    # The community property state lived in — a fact about the residence, so
    # a two-letter state code like every stored state.
    community_state: str | None = None
    lived_with_debtor: bool | None = None


def parse_community_household_member(
    payload: Mapping[str, object],
) -> CommunityHouseholdMemberBody:
    errors: dict[str, str] = {}
    body = CommunityHouseholdMemberBody(
        name=text(payload.get("name"), "name", errors),
        address=parse_address(payload.get("address"), "address", errors),
        community_state=text(
            payload.get("community_state"), "community_state", errors, limit=2
        ),
        lived_with_debtor=boolean(
            payload.get("lived_with_debtor"), "lived_with_debtor", errors
        ),
    )
    if errors:
        raise FieldValidationError(errors)
    return body


COMMUNITY_HOUSEHOLD_MEMBER: EntityKind[CommunityHouseholdMemberBody] = EntityKind(
    name="community_household_member",
    collection="community_household_members",
    sk_prefix="COMMUNITY_HOUSEHOLD_MEMBER",
    parse_body=parse_community_household_member,
)
