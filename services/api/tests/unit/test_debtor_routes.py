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
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.firms import Firm, FirmUser, default_permissions

from tests.unit.opening import add_client, with_client

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
    # One debtor store for both: opening a case writes its debtors through
    # the case store (ADR 0022), and the debtor routes must see them.
    debtors = MemoryDebtorStore()
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
            case_store=MemoryCaseStore(debtor_store=debtors),
            firm_store=firms,
            access_log=access_log,
            debtor_store=debtors,
            tax_id_store=MemoryTaxIdStore(),
            tax_id_cipher=LocalTaxIdCipher(),
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    response = client.post(
        "/v1/cases",
        json=with_client(
            client, auth(subject), {"chapter": 7, "court": "flmb", "division": "tampa"}
        ),
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def link(client, case_id, role, client_id, subject=ALICE):
    return client.put(
        f"/v1/cases/{case_id}/debtors/{role}/client",
        json={"client_id": client_id},
        headers=auth(subject),
    )


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
    # The non-filing spouse: the one role a questionnaire save may still
    # start from nothing (ADR 0022 — they need not be the firm's client).
    case_id = open_case(client)
    first = put(
        client,
        case_id,
        role="non_filing_spouse",
        name={"given": "Ada"},
        provenance={"name.given": TYPED},
    )
    assert first.status_code == 201
    second = put(
        client,
        case_id,
        role="non_filing_spouse",
        name={"given": "Ada"},
        provenance={"name.given": TYPED},
    )
    assert second.status_code == 200


def test_debtor_1_already_exists_when_the_case_is_opened(client):
    saved = put(
        client,
        open_case(client),
        name={"given": "Ada"},
        provenance={"name.given": TYPED},
    )
    assert saved.status_code == 200


def test_a_second_debtor_is_linked_never_minted_by_a_save(client):
    """Debtor 2 is the firm's client too: a questionnaire save cannot create
    them from nothing, and says what to do instead."""
    case_id = open_case(client)
    refused = put(
        client,
        case_id,
        role="debtor_2",
        name={"given": "Sam"},
        provenance={"name.given": TYPED},
    )
    assert refused.status_code == 400
    assert "client_id" in refused.get_json()["fields"]
    listed = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    assert [d["filing_role"] for d in listed.get_json()["debtors"]] == ["debtor_1"]


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
    assert put(client, open_case(client)).status_code == 200


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


# ── The tax id (issue 13.12 / #382) ─────────────────────────────
# 987-65-4321 is from the SSA's never-issued advertising block — the fixture
# value insolvia_core.tax_ids accepts on purpose. This repo is public.

TAX_ID = {"kind": "ssn", "value": "987-65-4321"}
TAX_ID_PROVENANCE = {"tax_id": TYPED}


def test_a_tax_id_is_stored_and_only_its_last_four_ever_comes_back(client):
    case_id = open_case(client)
    saved = put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    assert saved.status_code == 200
    assert saved.get_json()["tax_id"] == {"kind": "ssn", "last_four": "4321"}
    listed = client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    (debtor,) = listed.get_json()["debtors"]
    assert debtor["tax_id"] == {"kind": "ssn", "last_four": "4321"}
    # Neither the digits nor the sealed item's reference reach any response.
    for body in (saved.get_json(), listed.get_json()):
        assert "987654321" not in str(body)
        assert "987-65-4321" not in str(body)
    assert set(saved.get_json()["tax_id"]) == {"kind", "last_four"}
    assert set(debtor["tax_id"]) == {"kind", "last_four"}


def test_the_digits_are_sealed_not_stored(client):
    case_id = open_case(client)
    put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    deps: ApiDependencies = client.application.extensions["insolvia_api_dependencies"]
    debtor = deps.debtor_store.get(case_id, filing_role="debtor_1")  # type: ignore[union-attr]
    assert debtor is not None
    assert debtor.tax_id is not None
    sealed = deps.tax_id_store.get(case_id, debtor.tax_id.ref)  # type: ignore[union-attr]
    assert sealed is not None
    assert "987654321" not in str(sealed)
    assert sealed.firm_id == FIRM_A


def test_a_tax_id_needs_provenance_at_the_one_path(client):
    response = put(client, open_case(client), tax_id=TAX_ID)
    assert response.status_code == 400
    assert "provenance.tax_id" in response.get_json()["fields"]


def test_a_malformed_tax_id_is_refused_with_its_path(client):
    response = put(
        client,
        open_case(client),
        tax_id={"kind": "ssn", "value": "000-00-0000"},
        provenance=TAX_ID_PROVENANCE,
    )
    assert response.status_code == 400
    assert "tax_id.value" in response.get_json()["fields"]


def test_echoing_the_last_four_keeps_the_stored_number(client):
    # The autosave sends the whole record and only ever holds the view.
    case_id = open_case(client)
    put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    kept = put(
        client,
        case_id,
        name={"given": "Ada"},
        tax_id={"kind": "ssn", "last_four": "4321"},
        provenance={"name.given": TYPED, **TAX_ID_PROVENANCE},
    )
    assert kept.status_code == 200
    assert kept.get_json()["tax_id"] == {"kind": "ssn", "last_four": "4321"}


def test_an_echo_that_does_not_match_is_a_400(client):
    case_id = open_case(client)
    put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    response = put(
        client,
        case_id,
        tax_id={"kind": "ssn", "last_four": "9999"},
        provenance=TAX_ID_PROVENANCE,
    )
    assert response.status_code == 400
    assert "tax_id" in response.get_json()["fields"]


def test_leaving_the_tax_id_out_clears_it(client):
    case_id = open_case(client)
    put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    cleared = put(client, case_id)
    assert cleared.status_code == 200
    assert "tax_id" not in cleared.get_json()


def test_saving_a_tax_id_never_performs_the_full_value_read(client, access_log):
    case_id = open_case(client)
    put(client, case_id, tax_id=TAX_ID, provenance=TAX_ID_PROVENANCE)
    client.get(f"/v1/cases/{case_id}/debtors", headers=auth(ALICE))
    assert not any(event.action == "taxid.read" for event in access_log.events)


# ── Reading ─────────────────────────────────────────────────────


def test_a_new_case_lists_its_client_as_debtor_1_and_nobody_else(client):
    response = client.get(f"/v1/cases/{open_case(client)}/debtors", headers=auth(ALICE))
    assert response.status_code == 200
    assert [d["filing_role"] for d in response.get_json()["debtors"]] == ["debtor_1"]


def test_debtors_list_in_the_order_the_forms_print_them(client):
    case_id = open_case(client)
    # Written out of order on purpose.
    put(client, case_id, role="non_filing_spouse")
    link(client, case_id, "debtor_2", add_client(client, auth(ALICE)))
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
        client,
        case_id,
        role="non_filing_spouse",
        name={"given": "Ada"},
        provenance={"name.given": TYPED},
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
            client,
            case_id,
            role="non_filing_spouse",
            name={"given": "Augusta"},
            provenance={"name.given": TYPED},
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
    assert saved.status_code == 200
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


# ── Linking a client (ADR 0022) ─────────────────────────────────


def debtors_of(client, case_id, subject=ALICE):
    return client.get(f"/v1/cases/{case_id}/debtors", headers=auth(subject)).get_json()[
        "debtors"
    ]


def test_linking_a_client_to_an_empty_role_copies_them_in(client):
    case_id = open_case(client)
    sam = add_client(
        client, auth(ALICE), name={"given": "Sam"}, email="sam@example.test"
    )
    response = link(client, case_id, "debtor_2", sam)
    assert response.status_code == 201
    body = response.get_json()
    assert (body["filing_role"], body["client_id"]) == ("debtor_2", sam)
    assert body["email"] == "sam@example.test"
    assert body["provenance"]["email"] == {"source": "client", "client_id": sam}
    assert body["differs_from_client"] == []


def test_re_linking_moves_the_link_and_never_the_case_facts(client):
    case_id = open_case(client)
    [original] = debtors_of(client, case_id)
    other = add_client(client, auth(ALICE), name={"given": "Robin", "surname": "Other"})
    response = link(client, case_id, "debtor_1", other)
    assert response.status_code == 200
    body = response.get_json()
    assert body["id"] == original["id"]
    assert body["client_id"] == other
    # What the case says is unchanged; how it now differs is shown.
    assert body["name"] == original["name"]
    assert body["differs_from_client"] == ["name.given", "name.surname"]


def test_one_client_holds_one_role_per_case(client):
    case_id = open_case(client)
    [debtor_1] = debtors_of(client, case_id)
    response = link(client, case_id, "debtor_2", debtor_1["client_id"])
    assert response.status_code == 400
    assert "client_id" in response.get_json()["fields"]
    assert [d["filing_role"] for d in debtors_of(client, case_id)] == ["debtor_1"]


def test_another_firms_client_cannot_be_linked(client):
    case_id = open_case(client)
    bobs = add_client(client, auth(BOB))
    foreign = link(client, case_id, "debtor_2", bobs)
    unknown = link(client, case_id, "debtor_2", "no-such-client")
    assert foreign.status_code == unknown.status_code == 400
    assert foreign.get_json() == unknown.get_json()


def test_linking_needs_a_client_id(client):
    response = link(client, open_case(client), "debtor_2", "  ")
    assert response.status_code == 400
    assert "client_id" in response.get_json()["fields"]


def test_linking_needs_the_client_directory(client, firms):
    case_id = open_case(client)
    sam = add_client(client, auth(ALICE))
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "clients": "hidden"},
    )
    assert link(client, case_id, "debtor_2", sam).status_code == 403


def test_a_questionnaire_save_keeps_the_client_link(client):
    case_id = open_case(client)
    [before] = debtors_of(client, case_id)
    saved = put(
        client,
        case_id,
        client_id="someone-else",
        name={"given": "Typed"},
        provenance={"name.given": TYPED},
    )
    assert saved.status_code == 200
    assert saved.get_json()["client_id"] == before["client_id"]
    # The client still says Jordan Example; the case now says Typed.
    assert saved.get_json()["differs_from_client"] == ["name.given", "name.surname"]


def test_a_caller_without_the_client_directory_is_not_told_how_a_client_differs(
    client, firms
):
    case_id = open_case(client)
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "clients": "hidden"},
    )
    [debtor] = debtors_of(client, case_id)
    assert "client_id" in debtor
    assert "differs_from_client" not in debtor


# ── Provenance survives an autosave (ADR 0022 PR 5) ─────────────


def echo(debtor):
    """The questionnaire's whole-record save of `debtor` as loaded — every
    field and every provenance entry sent back as it came."""
    server_owned = {
        "id",
        "case_id",
        "filing_role",
        "created_at",
        "updated_at",
        "client_id",
        "differs_from_client",
    }
    return {key: value for key, value in debtor.items() if key not in server_owned}


def test_an_untouched_copied_field_keeps_client_provenance_across_a_save(client):
    case_id = open_case(client)
    [loaded] = debtors_of(client, case_id)
    body = echo(loaded)
    # The preparer changes the given name and nothing else.
    body["name"] = {**body["name"], "given": "Jordy"}
    body["provenance"] = {**body["provenance"], "name.given": TYPED}

    saved = put(client, case_id, **body)

    assert saved.status_code == 200, saved.get_json()
    provenance = saved.get_json()["provenance"]
    assert provenance["name.given"] == TYPED
    assert provenance["name.surname"] == {
        "source": "client",
        "client_id": loaded["client_id"],
    }


def test_client_provenance_on_a_changed_value_is_refused(client):
    case_id = open_case(client)
    [loaded] = debtors_of(client, case_id)
    body = echo(loaded)
    body["name"] = {**body["name"], "surname": "Retyped"}

    saved = put(client, case_id, **body)

    assert saved.status_code == 400
    assert list(saved.get_json()["fields"]) == ["provenance.name.surname"]


def test_client_provenance_the_record_never_had_cannot_be_claimed(client):
    case_id = open_case(client)
    [loaded] = debtors_of(client, case_id)
    body = echo(loaded)
    body["phone"] = "555-0100"
    body["provenance"] = {
        **body["provenance"],
        "phone": {"source": "client", "client_id": loaded["client_id"]},
    }

    saved = put(client, case_id, **body)

    assert saved.status_code == 400
    assert list(saved.get_json()["fields"]) == ["provenance.phone"]


# ── Re-copy from client, update client from this case ───────────


def recopy(client, case_id, role="debtor_1", subject=ALICE):
    return client.post(
        f"/v1/cases/{case_id}/debtors/{role}/copy-from-client", headers=auth(subject)
    )


def copy_to_client(client, case_id, role="debtor_1", subject=ALICE):
    return client.post(
        f"/v1/cases/{case_id}/debtors/{role}/copy-to-client", headers=auth(subject)
    )


def diverged_case(client):
    """A case whose Debtor 1 was retyped after the copy, and which carries a
    venue answer of its own — the field re-copy must not touch."""
    case_id = open_case(client)
    [loaded] = debtors_of(client, case_id)
    body = echo(loaded)
    body["name"] = {**body["name"], "given": "Jordy"}
    body["venue"] = {"basis": "lived_longest_180_days"}
    body["provenance"] = {
        **body["provenance"],
        "name.given": TYPED,
        "venue.basis": TYPED,
    }
    saved = put(client, case_id, **body)
    assert saved.get_json()["differs_from_client"] == ["name.given"]
    return case_id, saved.get_json()


def file_case(client, case_id):
    filed = client.patch(
        f"/v1/cases/{case_id}", json={"status": "filed"}, headers=auth(ALICE)
    )
    assert filed.status_code == 200, filed.get_json()


def test_recopy_restores_the_clients_values_with_client_provenance(client):
    case_id, before = diverged_case(client)

    response = recopy(client, case_id)

    assert response.status_code == 200
    body = response.get_json()
    assert body["id"] == before["id"]
    assert body["name"]["given"] == "Jordan"
    assert body["provenance"]["name.given"] == {
        "source": "client",
        "client_id": before["client_id"],
    }
    assert body["differs_from_client"] == []
    # The case's own answer, and where it came from, are untouched.
    assert body["venue"] == {"basis": "lived_longest_180_days"}
    assert body["provenance"]["venue.basis"] == TYPED


def test_recopy_empties_a_field_the_client_does_not_hold(client):
    case_id, loaded = diverged_case(client)
    body = echo(loaded)
    body["email"] = "typed@example.test"
    body["provenance"] = {**body["provenance"], "email": TYPED}
    assert put(client, case_id, **body).status_code == 200

    response = recopy(client, case_id)

    assert "email" not in response.get_json()
    assert "email" not in response.get_json()["provenance"]


def test_recopy_is_refused_on_a_filed_case(client):
    case_id, _ = diverged_case(client)
    file_case(client, case_id)

    response = recopy(client, case_id)

    assert response.status_code == 409
    [debtor] = debtors_of(client, case_id)
    assert debtor["name"]["given"] == "Jordy"


def test_recopy_needs_a_linked_client(client):
    case_id = open_case(client)
    assert put(client, case_id, role="non_filing_spouse").status_code == 201
    assert recopy(client, case_id, role="non_filing_spouse").status_code == 409


def test_update_client_copies_the_cases_values_onto_the_client(client):
    case_id, before = diverged_case(client)

    response = copy_to_client(client, case_id)

    assert response.status_code == 200
    body = response.get_json()
    assert body["differs_from_client"] == []
    # The debtor is only read: its values and provenance are as they were.
    assert body["name"] == before["name"]
    assert body["provenance"] == before["provenance"]
    record = client.get(
        f"/v1/firm/clients/{before['client_id']}", headers=auth(ALICE)
    ).get_json()
    assert record["name"] == {"given": "Jordy", "surname": "Example"}


def test_update_client_is_allowed_on_a_filed_case(client):
    case_id, _ = diverged_case(client)
    file_case(client, case_id)
    assert copy_to_client(client, case_id).status_code == 200


def test_update_client_is_recorded_as_a_client_update(client, access_log):
    case_id, _ = diverged_case(client)
    copy_to_client(client, case_id)
    last = access_log.events[-1]
    assert (last.action, last.outcome) == ("client.update", "allowed")


def test_update_client_needs_clients_add_edit(client, firms):
    case_id, _ = diverged_case(client)
    firms.users[(FIRM_A, ALICE)] = member(
        ALICE,
        FIRM_A,
        is_admin=False,
        access_all_cases=True,
        permissions={**default_permissions("attorney"), "clients": "view_only"},
    )
    assert copy_to_client(client, case_id).status_code == 403
