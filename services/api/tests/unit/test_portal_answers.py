"""The portal questionnaire's answers (ADR 0023 PR 4 / #363), in-process.

The ADR's done-when, through the real routes and the memory adapters: a
client answers, the answer is a pending candidate and NOTHING ELSE — the
case record is untouched — and a person at the firm confirms it through the
review queue, which writes the case field with `client_answered`
provenance. Around it: edit and withdraw while pending, the one-live-answer
rule, a switched-off section, another person's answer, and a staff save
that tries to claim the client said something.

Tokens are signed for real, as in test_portal_routes.py. Every identifier
and value below is obviously fake. This repo is public.
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
from insolvia_core.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.firms import Firm, FirmUser, default_permissions

from tests.unit.opening import with_client

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
STAFF_CLIENT_ID = "exampleappclientid000000"
PORTAL_CLIENT_ID = "exampleportalclientid000"
KID = "test-key-1"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"  # firm A admin

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)


def token(subject: str, client_id: str) -> dict[str, str]:
    now = int(time.time())
    encoded = jwt.encode(
        {
            "iss": ISSUER,
            "client_id": client_id,
            "token_use": "access",
            "sub": subject,
            "username": subject,
            "iat": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )
    return {"Authorization": f"Bearer {encoded}"}


def staff(subject: str = ALICE) -> dict[str, str]:
    return token(subject, STAFF_CLIENT_ID)


def portal(subject: str) -> dict[str, str]:
    return token(subject, PORTAL_CLIENT_ID)


@pytest.fixture
def stores():
    debtors = MemoryDebtorStore()
    firms = MemoryFirmStore()
    firms.create_firm(
        Firm(
            id=FIRM_A,
            name="Example & Partners",
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    firms.add_user(
        FirmUser(
            firm_id=FIRM_A,
            subject=ALICE,
            email="a11c@example.test",
            first_name="Person",
            last_name="A11c",
            role="attorney",
            is_admin=True,
            access_all_cases=True,
            permissions=default_permissions("attorney"),
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    return {
        "firm_store": firms,
        "debtor_store": debtors,
        "case_store": MemoryCaseStore(debtor_store=debtors),
        "candidate_store": MemoryCandidateStore(),
        "case_entity_store": MemoryCaseEntityStore(),
        "client_binding_store": MemoryClientBindingStore(),
        "access_log": MemoryAccessLog(),
        # Composed only because the staff debtor read requires them; no
        # answer carries a tax id.
        "tax_id_store": MemoryTaxIdStore(),
        "tax_id_cipher": LocalTaxIdCipher(),
    }


@pytest.fixture
def client(stores):
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": STAFF_CLIENT_ID,
                    "AUTH_PORTAL_CLIENT_ID": PORTAL_CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PRIVATE_KEY.public_key()}),
            user_directory=MemoryUserDirectory(),
            **stores,
        )
    )
    return app.test_client()


@pytest.fixture
def case_id(client) -> str:
    response = client.post(
        "/v1/cases",
        json=with_client(
            client, staff(), {"chapter": 7, "court": "flmb", "division": "tampa"}
        ),
        headers=staff(),
    )
    assert response.status_code == 201, response.get_json()
    return str(response.get_json()["id"])


def invite(client, case_id: str, email: str, **body) -> str:
    response = client.post(
        f"/v1/cases/{case_id}/portal/invitation",
        json={"email": email, "displayName": "Pat Example", **body},
        headers=staff(),
    )
    assert response.status_code == 201, response.get_json()
    return str(response.get_json()["subject"])


@pytest.fixture
def pat(client, case_id) -> str:
    return invite(client, case_id, "pat@example.test")


LEGAL_NAME = {
    "questionId": "personal_information.legal_name",
    "value": {"given": "Patricia", "surname": "Example"},
}
VEHICLE = {
    "questionId": "property.vehicle",
    "value": {"year": 2099, "make": "Examplecar", "value_entire": "1200"},
}


def answer(client, subject: str, body: dict[str, object]):
    return client.post("/v1/portal/answers", json=body, headers=portal(subject))


def queue(client, case_id: str) -> list[dict[str, object]]:
    response = client.get(f"/v1/cases/{case_id}/extraction/candidates", headers=staff())
    assert response.status_code == 200, response.get_json()
    return response.get_json()["candidates"]


def review(client, case_id: str, candidate_id: str, **body):
    return client.post(
        f"/v1/cases/{case_id}/extraction/candidates/{candidate_id}/review",
        json={"action": "accept", **body},
        headers=staff(),
    )


def debtor_1(client, case_id: str) -> dict[str, object]:
    response = client.get(f"/v1/cases/{case_id}/debtors", headers=staff())
    assert response.status_code == 200, response.get_json()
    [first] = [
        d for d in response.get_json()["debtors"] if d["filing_role"] == "debtor_1"
    ]
    return first


# ── The done-when ───────────────────────────────────────────────────


def test_an_answer_is_a_pending_candidate_and_does_not_touch_the_case(
    client, case_id, pat
):
    before = debtor_1(client, case_id)

    response = answer(client, pat, LEGAL_NAME)

    assert response.status_code == 201, response.get_json()
    body = response.get_json()
    assert body["status"] == "pending"
    assert body["value"] == {"given": "Patricia", "surname": "Example"}
    assert debtor_1(client, case_id) == before


def test_staff_see_the_answer_from_the_client_with_its_question(client, case_id, pat):
    answer(client, pat, LEGAL_NAME)

    [row] = queue(client, case_id)

    assert row["origin"]["channel"] == "client"
    assert row["client"] == {"displayName": "Pat Example"}
    assert row["question"] == {
        "id": "personal_information.legal_name",
        "sectionId": "personal_information",
        "sectionTitle": "Personal information",
        "text": "What is your full legal name?",
        "filingRole": "debtor_1",
    }


def test_accepting_writes_the_debtor_field_with_client_answered_provenance(
    client, case_id, pat
):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]

    response = review(client, case_id, answer_id)

    assert response.status_code == 200, response.get_json()
    debtor = debtor_1(client, case_id)
    assert debtor["name"] == {"given": "Patricia", "surname": "Example"}
    entry = debtor["provenance"]["name.given"]
    assert entry["source"] == "client_answered"
    assert entry["confirmed_by"] == ALICE
    assert entry["confirmed_at"]
    assert entry["extraction_id"] == answer_id
    assert "locator" not in entry


def test_an_accepted_debtor_answer_leaves_every_other_field_as_it_was(
    client, case_id, pat
):
    before = debtor_1(client, case_id)
    answer_id = answer(
        client,
        pat,
        {"questionId": "personal_information.phone", "value": {"phone": "555-0100"}},
    ).get_json()["id"]

    review(client, case_id, answer_id)

    after = debtor_1(client, case_id)
    assert after["phone"] == "555-0100"
    assert after["name"] == before["name"]
    assert after["provenance"]["name.given"] == before["provenance"]["name.given"]


def test_a_corrected_answer_is_staff_typed_where_the_reviewer_changed_it(
    client, case_id, pat
):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]

    review(
        client,
        case_id,
        answer_id,
        correctedPayload={"name": {"given": "Patricia", "surname": "Examples"}},
    )

    provenance = debtor_1(client, case_id)["provenance"]
    assert provenance["name.given"]["source"] == "client_answered"
    assert provenance["name.surname"]["source"] == "staff_typed"


def test_an_appended_other_name_is_added_not_overwritten(client, case_id, pat):
    for surname in ("Formerly", "Earlier"):
        answer_id = answer(
            client,
            pat,
            {
                "questionId": "personal_information.other_names",
                "value": {"surname": surname},
            },
        ).get_json()["id"]
        assert review(client, case_id, answer_id).status_code == 200

    names = debtor_1(client, case_id)["other_names_used"]
    assert [name["surname"] for name in names] == ["Formerly", "Earlier"]


def test_a_record_answer_becomes_a_new_record_with_client_answered_provenance(
    client, case_id, pat
):
    answer_id = answer(client, pat, VEHICLE).get_json()["id"]

    response = review(client, case_id, answer_id)

    assert response.status_code == 200, response.get_json()
    record = response.get_json()["record"]
    assert record["category"] == "vehicle"
    assert record["make"] == "Examplecar"
    assert record["provenance"]["make"]["source"] == "client_answered"
    assert record["provenance"]["category"]["source"] == "client_answered"


def test_an_employment_answer_is_linked_to_the_debtor_who_gave_it(client, case_id, pat):
    answer_id = answer(
        client,
        pat,
        {"questionId": "income.employment", "value": {"employer_name": "Example Co"}},
    ).get_json()["id"]

    record = review(client, case_id, answer_id).get_json()["record"]

    assert record["debtor_id"] == debtor_1(client, case_id)["id"]
    assert record["provenance"]["debtor_id"]["source"] == "staff_typed"


def test_the_client_reads_back_the_review_status(client, case_id, pat):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]
    review(client, case_id, answer_id)

    response = client.get("/v1/portal/answers", headers=portal(pat))

    [mine] = response.get_json()["answers"]
    assert mine["id"] == answer_id
    assert mine["status"] == "accepted"
    assert "confirmedBy" not in mine


# ── Edit while pending ──────────────────────────────────────────────


def test_a_pending_answer_can_be_changed(client, case_id, pat):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]

    response = client.put(
        f"/v1/portal/answers/{answer_id}",
        json={"value": {"given": "Pat", "surname": "Example"}},
        headers=portal(pat),
    )

    assert response.status_code == 200, response.get_json()
    [row] = queue(client, case_id)
    assert row["payload"] == {"name": {"given": "Pat", "surname": "Example"}}


def test_a_pending_answer_can_be_withdrawn_and_leaves_the_queue_pending_list(
    client, case_id, pat
):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]

    response = client.delete(f"/v1/portal/answers/{answer_id}", headers=portal(pat))

    assert response.status_code == 200
    assert response.get_json()["status"] == "withdrawn"
    [row] = queue(client, case_id)
    assert row["status"] == "withdrawn"


@pytest.mark.parametrize("method", ["put", "delete"])
def test_a_reviewed_answer_is_immutable(client, case_id, pat, method):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]
    review(client, case_id, answer_id)

    response = getattr(client, method)(
        f"/v1/portal/answers/{answer_id}",
        json={"value": {"given": "Other", "surname": "Name"}},
        headers=portal(pat),
    )

    assert response.status_code == 409


def test_a_second_live_answer_to_a_one_answer_question_is_a_conflict(
    client, case_id, pat
):
    answer(client, pat, LEGAL_NAME)

    assert answer(client, pat, LEGAL_NAME).status_code == 409


def test_after_review_the_same_question_takes_a_new_answer(client, case_id, pat):
    first = answer(client, pat, LEGAL_NAME).get_json()["id"]
    review(client, case_id, first)

    assert answer(client, pat, LEGAL_NAME).status_code == 201


# ── What a client cannot do ─────────────────────────────────────────


def test_a_switched_off_sections_questions_are_not_answerable(client, case_id, pat):
    sections = [
        {"id": s, "enabled": s != "property"}
        for s in (
            "personal_information",
            "property",
            "debts",
            "income",
            "expenses",
            "other",
        )
    ]
    saved = client.put(
        "/v1/firm/questionnaire", json={"sections": sections}, headers=staff()
    )
    assert saved.status_code == 200, saved.get_json()

    response = answer(client, pat, VEHICLE)

    assert response.status_code == 400
    assert "questionId" in response.get_json()["fields"]


def test_switching_a_section_off_hides_its_answers_from_the_client(
    client, case_id, pat
):
    answer_id = answer(client, pat, VEHICLE).get_json()["id"]
    sections = [
        {"id": s, "enabled": s != "property"}
        for s in (
            "personal_information",
            "property",
            "debts",
            "income",
            "expenses",
            "other",
        )
    ]
    client.put("/v1/firm/questionnaire", json={"sections": sections}, headers=staff())

    listed = client.get("/v1/portal/answers", headers=portal(pat)).get_json()
    deleted = client.delete(f"/v1/portal/answers/{answer_id}", headers=portal(pat))

    assert listed["answers"] == []
    assert deleted.status_code == 404
    # Staff keep it: a firm that takes a section in-house still reviews it.
    assert [row["id"] for row in queue(client, case_id)] == [answer_id]


def test_a_client_cannot_answer_for_the_other_debtor(client, case_id, pat):
    response = answer(client, pat, {**LEGAL_NAME, "filingRole": "debtor_2"})

    assert response.status_code == 400
    assert "filingRole" in response.get_json()["fields"]


def test_another_clients_answer_is_not_found(client, stores, case_id, pat):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]
    # A second case, a second client, same firm.
    other_case = client.post(
        "/v1/cases",
        json=with_client(
            client, staff(), {"chapter": 7, "court": "flmb", "division": "tampa"}
        ),
        headers=staff(),
    ).get_json()["id"]
    sam = invite(client, other_case, "sam@example.test")

    response = client.put(
        f"/v1/portal/answers/{answer_id}",
        json={"value": {"given": "X", "surname": "Y"}},
        headers=portal(sam),
    )

    assert response.status_code == 404
    assert client.get("/v1/portal/answers", headers=portal(sam)).get_json() == {
        "answers": []
    }


def test_the_origin_is_the_tokens_not_the_bodys(client, case_id, pat):
    answer(
        client,
        pat,
        {**LEGAL_NAME, "origin": {"channel": "extraction", "subject": ALICE}},
    )

    [row] = queue(client, case_id)

    assert row["origin"] == {
        "channel": "client",
        "clientId": PORTAL_CLIENT_ID,
        "subject": pat,
    }


def test_a_staff_token_cannot_answer(client, case_id):
    response = client.post("/v1/portal/answers", json=LEGAL_NAME, headers=staff())

    assert response.status_code == 401


def test_every_answer_request_is_on_the_access_log_as_the_client(
    client, stores, case_id, pat
):
    answer_id = answer(client, pat, LEGAL_NAME).get_json()["id"]
    client.put(
        f"/v1/portal/answers/{answer_id}",
        json={"value": {"given": "Pat", "surname": "Example"}},
        headers=portal(pat),
    )
    client.get("/v1/portal/answers", headers=portal(pat))

    portal_rows = [
        (event.action, event.principal)
        for event in stores["access_log"].events
        if event.action.startswith("portal.answer") or event.principal == pat
    ]
    assert ("portal.answer", pat) in portal_rows
    assert portal_rows.count(("portal.answer", pat)) == 2
    assert ("portal.read", pat) in portal_rows


# ── A staff save cannot claim the client said it ────────────────────


def test_a_staff_save_cannot_mint_client_answered_provenance(client, case_id):
    response = client.post(
        f"/v1/cases/{case_id}/assets",
        json={
            "category": "vehicle",
            "make": "Examplecar",
            "provenance": {
                "category": {"source": "staff_typed"},
                "make": {
                    "source": "client_answered",
                    "confirmed_by": ALICE,
                    "confirmed_at": "2099-01-01T00:00:00.000000Z",
                },
            },
        },
        headers=staff(),
    )

    assert response.status_code == 400
    assert "provenance.make" in response.get_json()["fields"]


def test_a_staff_save_keeps_an_accepted_answer_it_did_not_touch(client, case_id, pat):
    answer_id = answer(client, pat, VEHICLE).get_json()["id"]
    record = review(client, case_id, answer_id).get_json()["record"]
    body = {
        key: value
        for key, value in record.items()
        if key not in ("id", "case_id", "created_at", "updated_at")
    }

    response = client.put(
        f"/v1/cases/{case_id}/assets/{record['id']}",
        json={
            **body,
            "model": "Roadster",
            "provenance": {**record["provenance"], "model": {"source": "staff_typed"}},
        },
        headers=staff(),
    )

    assert response.status_code == 200, response.get_json()
    assert response.get_json()["provenance"]["make"]["source"] == "client_answered"
