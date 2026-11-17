"""The seeded firm clients through the real tables (ADR 0022 — the spec
#409 deferred until a fixture seeded clients).

What only a seeded environment can answer: that a client the loader wrote
into the real firm table reads back through `/v1/firm/clients`, that the
case table's debtor copies name it, that the copy still agrees with it
(`differs_from_client == []`), and that the `by-client` index — a GSI only
the deployed table has — lists the case under the client.

READ-ONLY, like test_fixture_cases.py: the fixture's clients are found by
following the fixture cases' Debtor 1 and Debtor 2, never created here, so a
run leaves the directory as the seed left it.
"""

from __future__ import annotations

import time
from typing import Any

import pytest

from tests.integration.conftest import Api
from tests.integration.test_fixture_cases import _find, _fixture_cases

#: A GSI is eventually consistent. The seed ran before the suite, so this is
#: only ever spent on a missing index — bounded so that fails rather than hangs.
_INDEX_WAIT_SECONDS = 10.0


@pytest.fixture(scope="module")
def seeded_debtors(fixture: dict[str, Any], admin: Api) -> list[tuple[str, dict]]:
    """(case id, debtor) for every Debtor 1 and Debtor 2 of every fixture case."""
    found: list[tuple[str, dict]] = []
    for spec in _fixture_cases(fixture):
        case_id = _find(admin, spec)
        if case_id is None:
            pytest.fail(f"fixture case '{spec.get('handle')}' is not in this target")
        for debtor in admin.get(f"/v1/cases/{case_id}/debtors")["debtors"]:
            if debtor["filing_role"] in ("debtor_1", "debtor_2"):
                found.append((case_id, debtor))
    if not found:
        pytest.skip("this target's fixture seeds no debtors")
    return found


def test_every_seeded_debtor_is_a_copy_of_a_client_it_still_agrees_with(
    admin: Api, seeded_debtors: list[tuple[str, dict]]
) -> None:
    for case_id, debtor in seeded_debtors:
        client_id = debtor.get("client_id")
        assert client_id, f"{case_id} {debtor['filing_role']} names no client"
        client = admin.get(f"/v1/firm/clients/{client_id}")
        assert client["status"] == "active"
        assert client["name"] == debtor["name"]
        assert debtor["differs_from_client"] == []
        copied = [
            entry
            for path, entry in debtor["provenance"].items()
            if path.split(".")[0].split("[")[0] == "name"
        ]
        assert copied
        assert all(
            entry == {"source": "client", "client_id": client_id} for entry in copied
        )


def test_the_seeded_clients_are_in_the_firms_directory(
    admin: Api, seeded_debtors: list[tuple[str, dict]]
) -> None:
    listed = {c["id"] for c in admin.get("/v1/firm/clients")["clients"]}
    assert {debtor["client_id"] for _, debtor in seeded_debtors} <= listed


def test_each_client_lists_its_fixture_case_in_the_role_it_holds(
    admin: Api, seeded_debtors: list[tuple[str, dict]]
) -> None:
    for case_id, debtor in seeded_debtors:
        client_id = debtor["client_id"]
        deadline = time.monotonic() + _INDEX_WAIT_SECONDS
        while True:
            entries = admin.get(f"/v1/firm/clients/{client_id}/cases")["cases"]
            roles = {e["case"]["id"]: e["filing_role"] for e in entries}
            if case_id in roles or time.monotonic() > deadline:
                break
            time.sleep(0.5)
        assert roles.get(case_id) == debtor["filing_role"], (
            f"case {case_id} is not in client {client_id}'s list — is the "
            "by-client index applied to this environment's case table?"
        )
