"""The registry of case collections — every EntityKind, addressable by its URL
segment.

One place on purpose: the routes, the stores and the tests all iterate THIS
map, so adding an entity type is one module plus one line here, and a kind
cannot exist half-wired — reachable in a store but not over the API, or
vice versa.

`debtor` and `document` are deliberately absent. They are case-scoped but not
generic collections: a debtor is keyed by filing role, a document by the
upload flow — each has its own module, port and routes.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any, Final

from .assets import ASSET
from .case_entities import EntityKind
from .claims import CLAIM
from .codebtors import CODEBTOR, COMMUNITY_HOUSEHOLD_MEMBER
from .contract_leases import CONTRACT_LEASE
from .creditors import CREDITOR
from .exemption_claims import EXEMPTION
from .expenses import DEPENDENT, EXPENSE, HOUSEHOLD
from .income import EMPLOYMENT, INCOME_SUMMARY
from .petitions import (
    FILING_PROFESSIONAL,
    PETITION,
    PRIOR_CASE,
    RELATED_CASE,
    SOLE_PROPRIETORSHIP,
)
from .sofa import SOFA_ENTRY

# `EntityKind[Any]` rather than a union of the ten body types: callers of this
# registry are generic (routes, stores) and never see a body except through
# the kind's own parser, so a union here would be forty lines of ceremony
# proving nothing the per-kind constants don't already prove.
COLLECTIONS: Final[Mapping[str, EntityKind[Any]]] = {
    kind.collection: kind
    for kind in (
        CREDITOR,
        CLAIM,
        ASSET,
        EMPLOYMENT,
        INCOME_SUMMARY,
        HOUSEHOLD,
        EXPENSE,
        DEPENDENT,
        CODEBTOR,
        SOFA_ENTRY,
        # B101's entities (issue #93): the petition answers, its repeating
        # rows, and the Part 7 signer block.
        PETITION,
        PRIOR_CASE,
        RELATED_CASE,
        SOLE_PROPRIETORSHIP,
        FILING_PROFESSIONAL,
        # The schedules' remaining entities (issue #289): 106C's claimed
        # exemptions, 106G's contracts and leases, and 106H's
        # community-property household member.
        EXEMPTION,
        CONTRACT_LEASE,
        COMMUNITY_HOUSEHOLD_MEMBER,
    )
}

# Every SK namespace in a case's partition, INCLUDING the non-generic ones —
# the invariant tests/test_case_entities.py checks: a new prefix that collides
# with (or is a prefix of) an existing one would hand one collection's items
# to another's begins_with query.
RESERVED_SK_NAMESPACES: Final = ("META", "ASSIGNEE", "DEBTOR", "DOCUMENT")
