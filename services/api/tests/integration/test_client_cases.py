"""A client's cases through the real table (ADR 0022, PR 2): the debtor item
feeds the `by-client` index, and `GET /v1/firm/clients/<id>/cases` reads it.

What only this tier can answer: that the deployed table HAS the index (a
Terraform change the unit tier cannot see), that the item the store writes
carries keys the index accepts, and that the API role may query it.

Scratch discipline: the scratch case is linked to the scratch client as
Debtor 1 — idempotently, whichever of them already existed — and nothing else
is created. Linking moves no copied field, so the intake spec's writes to
that debtor are unaffected.
"""

from __future__ import annotations

import time
from typing import Any

from tests.integration.conftest import Api

#: A GSI is eventually consistent; a just-written entry normally appears in
#: well under a second. Bounded, so a missing index fails rather than hangs.
_INDEX_WAIT_SECONDS = 10.0


def test_a_case_linked_to_a_client_is_in_that_clients_list(
    admin: Api, scratch_client: dict[str, Any], scratch_case: dict[str, Any]
):
    client_id = scratch_client["id"]
    case_id = scratch_case["id"]
    linked = admin.put(
        f"/v1/cases/{case_id}/debtors/debtor_1/client",
        {"client_id": client_id},
        expect=(200, 201),
    )
    assert linked["client_id"] == client_id
    assert isinstance(linked["differs_from_client"], list)

    deadline = time.monotonic() + _INDEX_WAIT_SECONDS
    while True:
        entries = admin.get(f"/v1/firm/clients/{client_id}/cases")["cases"]
        roles = {e["case"]["id"]: e["filing_role"] for e in entries}
        if case_id in roles or time.monotonic() > deadline:
            break
        time.sleep(0.5)
    assert roles.get(case_id) == "debtor_1", (
        "the scratch case is not in its client's list — is the by-client "
        "index applied to this environment's case table?"
    )


def test_another_firms_client_is_not_found(as_user, people, scratch_client):
    """Cross-tenant, where the fixture has a second firm; a one-firm fixture
    cannot express this and says so by returning."""
    own = people["admin"]["firmName"]
    outsider = next(
        (handle for handle, person in people.items() if person["firmName"] != own),
        None,
    )
    if outsider is None:
        return
    as_user(outsider).get(f"/v1/firm/clients/{scratch_client['id']}/cases", expect=404)
