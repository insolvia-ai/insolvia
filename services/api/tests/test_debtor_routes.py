"""The debtor endpoints (issue 8.5).

What matters most here is the same thing that mattered for cases: what these
REFUSE. A debtor record holds a person's name, addresses and phone number, and
`DebtorStore` enforces no ownership of its own by design — the case lookup is
the only thing between one firm's clients and another's. Half of this file
exists to prove that lookup is actually on every path.

Tokens are signed for real, mirroring tests/test_cases.py. Every identifier
below is obviously fake; this repo is public.
"""

from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.access_log import MemoryAccessLog
from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.firms import Firm, FirmUser, default_permissions

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
# Two firms. ALICE and DANA are colleagues; BOB administers the OTHER firm,
# which makes him the strongest caller the other tenant has.
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
DANA = "00000000-0000-4000-8000-00000000da4a"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

TYPED = {"source": "staff_typed"}

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def token_for(subject: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "sub": subject,
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "iat": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )


def auth(subject: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token_for(subject)}"}


@pytest.fixture
def access_log():
    return MemoryAccessLog()


def firm(firm_id: str, name: str) -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(subject: str, firm_id: str, **overrides: object) -> FirmUser:
    defaults: dict[str, object] = {
        "firm_id": firm_id,
        "subject": subject,
        "email": f"{subject[-4:]}@example.test",
        "first_name": "Person",
        "last_name": subject[-4:],
        "role": "attorney",
        "is_admin": True,
        "access_all_cases": False,
        "permissions": default_permissions("attorney"),
        "status": "active",
        "created_at": "2026-01-01T00:00:00.000Z",
        "updated_at": "2026-01-01T00:00:00.000Z",
    }
    return FirmUser(**{**defaults, **overrides})  # type: ignore[arg-type]


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ALICE, FIRM_A))
    # Neither an admin nor access_all_cases: a colleague who reaches a matter
    # only by being linked to it.
    store.add_user(member(DANA, FIRM_A, is_admin=False))
    store.add_user(member(BOB, FIRM_B))
    return store


@pytest.fixture
def client(access_log, firms):
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PUBLIC_KEY}),
            case_store=MemoryCaseStore(),
            firm_store=firms,
            access_log=access_log,
            debtor_store=MemoryDebtorStore(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases", json={"chapter": 7, "district": "NDCA"}, headers=auth(subject)
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def put(client, case_id, role="debtor_1", subject=ALICE, **body):
    return client.put(
        f"/v1/cases/{case_id}/debtors/{role}", json=body, headers=auth(subject)
    )


# ── Auth and ownership ──────────────────────────────────────────


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("put", "/v1/cases/any-id/debtors/debtor_1"),
        ("get", "/v1/cases/any-id/debtors"),
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, method, path):
    assert getattr(client, method)(path, json={}).status_code == 401


def test_another_firms_case_is_not_found_on_write(client):
    case_id = open_case(client, ALICE)
    assert put(client, case_id, subject=BOB).status_code == 404


def test_another_firms_case_is_not_found_on_read(client):
    case_id = open_case(client, ALICE)
    response = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(BOB))
    assert response.status_code == 404


def test_a_case_that_does_not_exist_is_indistinguishable(client):
    # Same status and body as someone else's case — otherwise this endpoint is
    # an oracle for case ids.
    missing = client.get("/v1/cases/no-such-case/debtors", headers=auth(ALICE))
    case_id = open_case(client, ALICE)
    foreign = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(BOB))
    assert missing.status_code == foreign.status_code == 404
    assert missing.get_json() == foreign.get_json()


def test_a_debtor_written_by_one_firm_is_invisible_to_another(client):
    case_id = open_case(client, ALICE)
    put(client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED})
    assert (
        client.get(f"/v1/cases/{case_id}/debtors", headers=auth(BOB)).status_code == 404
    )


# ── Writing ─────────────────────────────────────────────────────


def test_creating_a_debtor_answers_201_and_updating_answers_200(client):
    case_id = open_case(client)
    first = put(
        client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED}
    )
    assert first.status_code == 201
    second = put(
        client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED}
    )
    assert second.status_code == 200


def test_a_repeated_save_keeps_the_same_debtor_id(client):
    # Provenance paths on other records may already name it.
    case_id = open_case(client)
    first = put(
        client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED}
    )
    second = put(
        client, case_id, name={"given": "Augusta"}, provenance={"name.given": TYPED}
    )
    assert first.get_json()["id"] == second.get_json()["id"]
    assert second.get_json()["name"] == {"given": "Augusta"}


def test_an_empty_body_saves(client):
    # Progressive intake: opening the questionnaire and typing nothing is a
    # legitimate save, not an error.
    assert put(client, open_case(client)).status_code == 201


def test_an_unknown_filing_role_is_rejected(client):
    assert put(client, open_case(client), role="debtor_3").status_code == 400


def test_a_value_without_provenance_is_rejected_with_its_field(client):
    response = put(client, open_case(client), name={"given": "Ada"})
    assert response.status_code == 400
    assert "provenance.name.given" in response.get_json()["fields"]


def test_an_unconfirmed_extraction_is_rejected(client):
    response = put(
        client,
        open_case(client),
        name={"given": "Ada"},
        provenance={"name.given": {"source": "ai_extracted"}},
    )
    assert response.status_code == 400
    assert "provenance.name.given" in response.get_json()["fields"]


def test_a_tax_id_is_refused_with_a_reason(client):
    response = put(client, open_case(client), tax_id={"kind": "ssn", "value": "x"})
    assert response.status_code == 400
    assert "encryption" in response.get_json()["fields"]["tax_id"]


# ── Reading ─────────────────────────────────────────────────────


def test_a_case_with_no_debtors_lists_none(client):
    response = client.get(f"/v1/cases/{open_case(client)}/debtors", headers=auth(ALICE))
    assert response.status_code == 200
    assert response.get_json() == {"debtors": []}


def test_debtors_list_in_the_order_the_forms_print_them(client):
    case_id = open_case(client)
    # Written out of order on purpose.
    put(client, case_id, role="non_filing_spouse")
    put(client, case_id, role="debtor_2")
    put(client, case_id, role="debtor_1")
    response = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    roles = [debtor["filing_role"] for debtor in response.get_json()["debtors"]]
    assert roles == ["debtor_1", "debtor_2", "non_filing_spouse"]


def test_a_listed_debtor_carries_its_provenance(client):
    case_id = open_case(client)
    put(client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED})
    response = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    assert response.get_json()["debtors"][0]["provenance"] == {
        "name.given": {"source": "staff_typed"}
    }


# ── The access log ──────────────────────────────────────────────


def test_a_save_is_recorded_against_the_case(client, access_log):
    case_id = open_case(client)
    put(client, case_id)
    assert [event.action for event in access_log.events][-1] == "case.update"


def test_a_list_is_recorded_as_a_read(client, access_log):
    case_id = open_case(client)
    client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    assert [event.action for event in access_log.events][-1] == "case.read"


def test_a_refused_read_is_recorded_as_denied(client, access_log):
    # Someone walking case ids is exactly what this log should show.
    case_id = open_case(client, ALICE)
    client.get(f"/v1/cases/{case_id}/debtors", headers=auth(BOB))
    last = access_log.events[-1]
    assert last.outcome == "denied"
    assert last.principal == BOB


def test_a_rejected_body_records_nothing(client, access_log):
    # The body never reached the case, so the log must not claim it did.
    case_id = open_case(client)
    before = len(access_log.events)
    put(client, case_id, name={"given": "Ada"})
    assert len(access_log.events) == before


def test_a_second_first_save_cannot_erase_the_first_ones_id(client):
    """Two overlapping creates for the same role. The loser must not mint a
    second id: the winner's is already on its way to a client, and provenance
    paths elsewhere may name it."""

    case_id = open_case(client)
    first = put(
        client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED}
    )
    assert first.status_code == 201

    # The race, exactly: the FIRST read misses (as it would if it ran before
    # the other request's write landed), the conditional create is refused, and
    # the re-read then finds the winner.
    app = client.application
    deps: ApiDependencies = app.extensions["insolvia_api_dependencies"]
    store = deps.debtor_store
    real_get = store.get
    misses = [True]

    def get_once_stale(*args: object, **kwargs: object):
        if misses:
            misses.pop()
            return None
        return real_get(*args, **kwargs)  # type: ignore[arg-type]

    store.get = get_once_stale  # type: ignore[method-assign]
    try:
        second = put(
            client, case_id, name={"given": "Augusta"}, provenance={"name.given": TYPED}
        )
    finally:
        store.get = real_get  # type: ignore[method-assign]

    # Refused as a create, retried as a replace — same id, same created_at.
    assert second.status_code == 200
    assert second.get_json()["id"] == first.get_json()["id"]
    assert second.get_json()["created_at"] == first.get_json()["created_at"]
    assert second.get_json()["name"] == {"given": "Augusta"}


# ── What firms changed here ─────────────────────────────────────


def test_a_colleague_on_the_matter_can_run_its_intake(client):
    """WHAT THE OLD MODEL COULD NOT DO. Alice opens a matter and Dana, linked
    to it, saves the debtor. Under `owner_principal` she got a 404 on her own
    firm's case — which for intake meant two people could not split the work
    on one filing at all.

    The debtor routes learned nothing new to allow this: they resolve the case,
    and the case's rule changed underneath them. That is the argument for
    `_reachable_case_or_404` being the only check they make.
    """
    case_id = open_case(client, ALICE)
    assert (
        client.get(f"/v1/cases/{case_id}/debtors", headers=auth(DANA)).status_code
        == 404
    )

    assert (
        client.put(
            f"/v1/cases/{case_id}/assignees/{DANA}", headers=auth(ALICE)
        ).status_code
        == 204
    )

    saved = put(
        client,
        case_id,
        subject=DANA,
        name={"given": "Ada"},
        provenance={"name.given": TYPED},
    )
    assert saved.status_code == 201
    assert (
        client.get(f"/v1/cases/{case_id}/debtors", headers=auth(DANA)).status_code
        == 200
    )


def test_intake_hidden_means_403_not_404(client, firms):
    """The per-feature layer, on a case the caller CAN see. 404 would be a lie
    their client cannot act on — the matter is in their own listing."""
    case_id = open_case(client, ALICE)
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "intake": "hidden"},
    )
    assert (
        client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE)).status_code
        == 403
    )


def test_view_only_intake_can_read_but_not_save(client, firms):
    """Staff get `intake: view_only` by default (core/firms), so this is the
    shape of a real firm's clerk rather than an invented case."""
    case_id = open_case(client, ALICE)
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "intake": "view_only"},
    )
    assert (
        client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE)).status_code
        == 200
    )
    assert (
        put(
            client, case_id, name={"given": "Ada"}, provenance={"name.given": TYPED}
        ).status_code
        == 403
    )
