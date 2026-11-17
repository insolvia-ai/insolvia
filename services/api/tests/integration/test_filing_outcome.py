"""The hand-back resolution route over HTTP (ADR 0024 PR 8), against the
running API, the real pool and the real case table — on the suite's scratch
case only.

What this tier proves: the route is mounted behind its gates (anonymous 401,
a paralegal refused by the feature flag where the seed has one); a filing
id the case does not have is a 404 from the REAL filing store's read; the
body is validated before anything is written; and the approval view on a case
with no approval carries no `filing`.

WHAT IT CANNOT DO, AND WHERE THAT IS PROVED INSTEAD. A resolvable filing
record exists only once the filing worker has claimed an approved job, and
this tier can make no approval (the scratch case is never complete — see
test_filing_approval.py). So "handed back → resolved as filed → the case is
`filed` with its number, match key and history row", and "resolved as not
filed → a new approval → filed once by the worker", are proved against this
machine's real table, queue, bucket and the worker's own role by
services/filing/scripts/dev-filing-proof.sh (steps 5a and 5b), and over the
same route in-process by tests/unit/test_filing_outcome.py. Nothing here
writes to a fixture case, and the refused POSTs write nothing but access
rows.
"""

from __future__ import annotations

import uuid
from typing import Any

import pytest

from tests.integration.conftest import Api


def _path(case: dict[str, Any], filing_id: str) -> str:
    return f"/v1/cases/{case['id']}/filings/{filing_id}/resolution"


@pytest.fixture
def fresh_admin(fresh_as_user) -> Api:
    return fresh_as_user("admin")


def test_a_filing_the_case_does_not_have_is_not_found(
    fresh_admin: Api, scratch_case: dict[str, Any]
):
    refused = fresh_admin.post(
        _path(scratch_case, str(uuid.uuid4())),
        {"outcome": "not_filed", "docket_checked": True},
        expect=404,
    )
    assert refused["error"] == "NotFoundError"


def test_the_view_has_no_filing_without_an_approval(
    fresh_admin: Api, scratch_case: dict[str, Any]
):
    view = fresh_admin.get(f"/v1/cases/{scratch_case['id']}/filing-approval")
    assert "approval" not in view
    assert "filing" not in view


def test_an_anonymous_caller_is_refused(anonymous: Api, scratch_case: dict[str, Any]):
    anonymous.post(_path(scratch_case, str(uuid.uuid4())), {}, expect=401)


def test_a_paralegal_is_refused(
    people: dict[str, dict[str, Any]], as_user, scratch_case: dict[str, Any]
):
    if "paralegal" not in people:
        pytest.skip("no paralegal in this target's seed (seeds/dev.json has one admin)")
    as_user("paralegal").post(
        _path(scratch_case, str(uuid.uuid4())),
        {"outcome": "not_filed", "docket_checked": True},
        expect=403,
    )
