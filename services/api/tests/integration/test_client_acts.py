"""The debtor's client acts through the real table (ADR 0022, PR 5).

What only this tier can answer: that the link's one-client-one-role check —
now a DynamoDB transaction with a ConditionCheck on the case's other roles,
not a read before a write — is accepted by the real table and refuses what
it should; and that a re-copy, the kept-provenance rule and an update of the
client all round-trip through the deployed store.

Scratch discipline: everything happens on the scratch case's Debtor 1 and
the scratch client, which is what that debtor is copied from. A re-copy
leaves Debtor 1 as the client's copy — the state a freshly opened scratch
case starts in — and the client is updated only with what it already holds,
so nothing drifts between runs. No Debtor 2 is created: the refused link
writes nothing, which is the point of the test.
"""

from __future__ import annotations

from typing import Any

from tests.integration.conftest import Api

_SERVER_OWNED = {
    "id",
    "case_id",
    "filing_role",
    "created_at",
    "updated_at",
    "client_id",
    "differs_from_client",
}


def _linked_debtor_1(admin: Api, case_id: str, client_id: str) -> dict[str, Any]:
    return dict(
        admin.put(
            f"/v1/cases/{case_id}/debtors/debtor_1/client",
            {"client_id": client_id},
            expect=(200, 201),
        )
    )


def test_one_client_cannot_hold_two_roles_of_a_case(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    _linked_debtor_1(admin, case_id, scratch_client["id"])

    refused = admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_2/client",
        {"client_id": scratch_client["id"]},
        expect=400,
    )

    assert "client_id" in refused["fields"]
    debtors = admin.get(f"/v1/cases/{case_id}/debtors")["debtors"]
    holders = [
        d["filing_role"] for d in debtors if d.get("client_id") == scratch_client["id"]
    ]
    assert holders == ["debtor_1"]


def test_recopy_then_an_untouched_save_keeps_client_provenance(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    client_id = scratch_client["id"]
    _linked_debtor_1(admin, case_id, client_id)

    recopied = admin.post(
        f"/v1/cases/{case_id}/debtors/debtor_1/copy-from-client", None, expect=200
    )
    assert recopied["differs_from_client"] == []
    copied = {"source": "client", "client_id": client_id}
    assert recopied["provenance"]["name.given"] == copied

    # The questionnaire's echo of what it loaded: nothing changed, so the
    # client entries are kept rather than refused or rewritten.
    echo = {k: v for k, v in recopied.items() if k not in _SERVER_OWNED}
    saved = admin.put(f"/v1/cases/{case_id}/debtors/debtor_1", echo, expect=200)
    assert saved["provenance"]["name.given"] == copied

    # And a changed value cannot keep claiming the client.
    changed = {**echo, "name": {**echo["name"], "given": "Changed"}}
    admin.put(f"/v1/cases/{case_id}/debtors/debtor_1", changed, expect=400)


def test_updating_the_client_from_the_case_round_trips(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    case_id = scratch_case["id"]
    _linked_debtor_1(admin, case_id, scratch_client["id"])
    admin.post(
        f"/v1/cases/{case_id}/debtors/debtor_1/copy-from-client", None, expect=200
    )

    updated = admin.post(
        f"/v1/cases/{case_id}/debtors/debtor_1/copy-to-client", None, expect=200
    )

    assert updated["differs_from_client"] == []
    record = admin.get(f"/v1/firm/clients/{scratch_client['id']}")
    assert record["name"] == updated["name"]
