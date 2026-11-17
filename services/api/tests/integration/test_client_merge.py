"""Merging two clients through the real tables (ADR 0022, PR 7).

What only this tier can answer: that the merge's three kinds of conditional
write — the claim over both client rows (a TransactWriteItems of two
UpdateItems on the FIRM table), the per-case re-point (an UpdateItem plus
ConditionChecks on the CASE table), and the finishing archive — are
accepted by the real tables under the running role, and that the
`by-client` index follows the re-point.

Scratch discipline: the one thing created per run is a second client, which
the merge archives (clients cannot be deleted — ADR 0022). The scratch
case's Debtor 1 is linked to it and then merged back into the scratch
client, so the debtor ends linked where the conftest left it, its copied
fields untouched.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import SCRATCH_COURT, Api

# The fields that describe the link or the write, not what the case says.
_NOT_COPIED = {"client_id", "updated_at", "differs_from_client"}


def _debtor_1(admin: Api, case_id: str) -> dict[str, Any]:
    debtors = admin.get(f"/v1/cases/{case_id}/debtors")["debtors"]
    return next(d for d in debtors if d["filing_role"] == "debtor_1")


def test_a_merge_repoints_the_case_and_archives_the_merged_client(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    survivor = scratch_client["id"]
    duplicate = admin.post(
        "/v1/firm/clients",
        {"name": {"given": "Merge", "surname": "Duplicate (integration)"}},
        expect=201,
    )
    admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_1/client",
        {"client_id": duplicate["id"]},
        expect=200,
    )
    before = _debtor_1(admin, case_id)

    merged = admin.post(
        f"/v1/firm/clients/{survivor}/merge",
        {"merged_client_id": duplicate["id"]},
        expect=200,
    )

    assert merged["client"]["id"] == survivor
    assert merged["merged"]["status"] == "archived"
    assert merged["merged"]["merged_into"] == survivor
    after = _debtor_1(admin, case_id)
    assert after["client_id"] == survivor
    assert {k: v for k, v in after.items() if k not in _NOT_COPIED} == {
        k: v for k, v in before.items() if k not in _NOT_COPIED
    }
    listed = admin.get(f"/v1/firm/clients/{survivor}/cases")["cases"]
    assert case_id in [entry["case"]["id"] for entry in listed]

    # A merged client is done: not merged again, not opened for a case.
    admin.post(
        f"/v1/firm/clients/{survivor}/merge",
        {"merged_client_id": duplicate["id"]},
        expect=409,
    )
    refused = admin.post(
        "/v1/cases",
        {"chapter": 7, **SCRATCH_COURT, "client_ids": [duplicate["id"]]},
        expect=400,
    )
    assert "client_ids" in refused["fields"]
