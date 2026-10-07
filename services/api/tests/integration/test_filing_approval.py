"""The per-filing approval over HTTP (ADR 0024 PR 6, guardrail 1), against the
running API, the real pool and the real case table.

What this tier proves, on the suite's scratch case: the route answers the
basis the screen renders (court, documents in docket order, checklist, fee
handling, digest) from the real stores; the `auth_time` a REAL pool token
carries is what the fresh-sign-in check reads — a sign-in older than the
window is refused before anything else is looked at; a filing set with a
blocker is refused by name; and, on staging where they are seeded, a
paralegal (no `electronic_filing`) is refused and another firm's attorney
gets the undistinguishing 404.

WHAT IT CANNOT DO, AND WHERE THAT IS PROVED INSTEAD. A successful approval
needs a filing set with no blocker — a complete case and an assembled,
measured packet — and this tier can make neither: assembly is a pipeline
job whose worker the tier does not start, and the fixture cases that are
complete must stay as the seed left them (test_fixture_cases). So approve →
one message on the real queue → consume once → a second consume refused →
a re-assembly or a debtor edit voids it is proved against this machine's
real case table, bucket, vault and filing queue by
services/api/scripts/dev-filing-approval-proof.sh, and over the same routes
in-process by tests/unit/test_filing_approval_routes.py. The consume is the
filing worker's act (PR 7) and has no route, deliberately.

The scratch case is only ever read here, and POSTs that are refused write
nothing but their access rows.
"""

from __future__ import annotations

import time
from typing import Any

import jwt
import pytest

from tests.integration.conftest import Api

# Mirrors core/filing_approval.APPROVAL_SIGN_IN_MAX_AGE_SECONDS; the
# response carries it too (`signInMaxAgeSeconds`), and the test reads that.
WINDOW_FALLBACK = 300


def _path(case: dict[str, Any]) -> str:
    return f"/v1/cases/{case['id']}/filing-approval"


@pytest.fixture
def fresh_admin(fresh_as_user) -> Api:
    """The seeded admin, signed in just now — admins hold every feature,
    `electronic_filing` included. Not named `admin`: the session-scoped
    scratch case is opened by conftest's `admin`, and a function-scoped
    override of that name would be a scope mismatch."""
    return fresh_as_user("admin")


def test_the_scratch_case_is_served_what_an_approval_would_cover(
    fresh_admin: Api, scratch_case: dict[str, Any]
):
    view = fresh_admin.get(_path(scratch_case))

    basis = view["basis"]
    assert basis["scheme"] == "insolvia-filing-approval/1"
    assert len(basis["digest"]) == 64
    assert basis["court"]["code"] == scratch_case["court"]
    assert basis["fee"]["handling"] == "hand_back_at_payment"
    assert [d["position"] for d in basis["documents"]] == list(
        range(1, len(basis["documents"]) + 1)
    )
    assert basis["documents"][0]["key"] == "form/b101"
    # The scratch case is never complete and never assembled.
    assert basis["ready"] is False
    assert "packet" in basis["blockers"]
    # The same read twice is the same digest: it is computed, not minted.
    assert fresh_admin.get(_path(scratch_case))["basis"]["digest"] == basis["digest"]


def test_a_filing_set_with_a_blocker_is_refused_by_name(
    fresh_admin: Api, scratch_case: dict[str, Any]
):
    digest = fresh_admin.get(_path(scratch_case))["basis"]["digest"]

    refused = fresh_admin.post(_path(scratch_case), {"digest": digest}, expect=409)

    assert refused["error"] == "FilingSetNotReady"
    assert "packet" in refused["blockers"]
    assert "approval" not in fresh_admin.get(_path(scratch_case))


def test_nothing_pending_means_nothing_to_cancel(
    fresh_admin: Api, scratch_case: dict[str, Any]
):
    fresh_admin.delete(_path(scratch_case), expect=409)


def test_a_sign_in_older_than_the_window_is_refused_first(
    as_user, fresh_admin: Api, tokens: dict[str, str], scratch_case: dict[str, Any]
):
    """The session-scoped token was minted when the run began. The refusal
    is what a real stale sign-in looks like, so this waits — at most the
    window — until that token's `auth_time` is older than it, rather than
    skipping on a quick run. The stale check runs before the blocker check,
    so the scratch case serves."""
    window = int(
        fresh_admin.get(_path(scratch_case))["basis"].get(
            "signInMaxAgeSeconds", WINDOW_FALLBACK
        )
    )
    claims = jwt.decode(tokens["admin"], options={"verify_signature": False})
    age = time.time() - claims["auth_time"]
    if age <= window + 5:
        time.sleep(window + 5 - age)
    stale = as_user("admin")
    digest = stale.get(_path(scratch_case))["basis"]["digest"]

    refused = stale.post(_path(scratch_case), {"digest": digest}, expect=403)

    assert refused["error"] == "ReauthenticationRequired"


def test_an_anonymous_caller_is_refused(anonymous: Api, scratch_case: dict[str, Any]):
    anonymous.get(_path(scratch_case), expect=401)


def test_a_paralegal_is_refused(
    people: dict[str, dict[str, Any]], as_user, scratch_case: dict[str, Any]
):
    if "paralegal" not in people:
        pytest.skip("no paralegal in this target's seed (seeds/dev.json has one admin)")
    paralegal = as_user("paralegal")

    paralegal.get(_path(scratch_case), expect=403)
    paralegal.post(_path(scratch_case), {"digest": "0" * 64}, expect=403)


def test_another_firms_attorney_cannot_see_it(
    people: dict[str, dict[str, Any]], as_user, scratch_case: dict[str, Any]
):
    if "outsider" not in people:
        pytest.skip("this target's seed has no second firm (seeds/dev.json has one)")

    as_user("outsider").get(_path(scratch_case), expect=(403, 404))
