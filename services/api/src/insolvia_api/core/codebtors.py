"""The codebtor record — 106H Part 2: who else is liable on the debtor's debts.

The form's second column asks WHICH debt, as "Schedule D, line __" /
"Schedule E/F, line __". Line numbers are a rendering, so the model stores the
fact instead: `claim_ids` naming the claim records the codebtor is on. The
forms engine turns those into line references when it prints, and a claim
reordering does not silently point a codebtor at somebody else's debt.

`claim_ids` is a plain string list, attributed whole by provenance (the
`employer_ids` precedent), and unchecked against the claims collection — the
usual progressive-intake rule; dangling ids are the completeness gate's to
flag.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field

from insolvia_core.errors import FieldValidationError

from .case_entities import EntityKind
from .fields import Address, parse_address, string_list, text


@dataclass(frozen=True)
class CodebtorBody:
    name: str | None = None
    address: Address = field(default_factory=Address)
    claim_ids: tuple[str, ...] = ()


def parse_codebtor(payload: Mapping[str, object]) -> CodebtorBody:
    errors: dict[str, str] = {}
    body = CodebtorBody(
        name=text(payload.get("name"), "name", errors),
        address=parse_address(payload.get("address"), "address", errors),
        claim_ids=string_list(payload.get("claim_ids"), "claim_ids", errors, limit=64),
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
