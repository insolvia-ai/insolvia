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
from .income import (
    EMPLOYMENT,
    INCOME_SUMMARY,
    OTHER_INCOME_RECORD,
    PAY_PERIOD_RECORD,
)
from .means_test_inputs import MEANS_TEST_INPUT
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
        # The dated income history behind the means test (§101(10A)) —
        # per-paycheck records written by pay-stub extraction (8.8) through
        # the review flow, and dated non-wage receipts (issue #100) —
        # docs/reference/case-data-model.md, "106I is not the income model".
        PAY_PERIOD_RECORD,
        OTHER_INCOME_RECORD,
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
        # B122A-2's entered figures (issue #101), one per case like the
        # petition — the gate owns the cardinality.
        MEANS_TEST_INPUT,
    )
}

# Every SK namespace in a case's partition, INCLUDING the non-generic ones —
# the invariant tests/test_case_entities.py checks: a new prefix that collides
# with (or is a prefix of) an existing one would hand one collection's items
# to another's begins_with query.
#
# CANDIDATE is the MCP service's proposal rows (services/mcp, issue #262):
# agent-written records awaiting human review, stored in the case's partition
# but outside the case data proper — accepting one is what writes a real case
# record. Registered here because this tuple is the one cross-service ledger
# of who owns which SK prefix in that partition.
RESERVED_SK_NAMESPACES: Final = (
    "META",
    "ASSIGNEE",
    "DEBTOR",
    "DOCUMENT",
    "JOB",
    "PACKET",
    "CANDIDATE",
)
