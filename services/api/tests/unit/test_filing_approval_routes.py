"""`/v1/cases/<id>/filing-approval` (ADR 0024 build PR 6, guardrail 1).

What the ROUTES add over test_filing_approval.py's domain tests: the gates
run in front of the rule (no token, no `electronic_filing`, another firm's
case), the `auth_time` the check reads is the one in a real signed token,
the screen is served what it shows and posts back, a change after approval
shows as voided on the next read, and each refusal leaves the queue empty.

The case is the assembled reference case in the Middle District of Florida
(test_filing_approval's world, composed into the app). Tokens are signed for
real. Every identifier is fake.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.filing_approval import (
    APPROVAL_SIGN_IN_MAX_AGE_SECONDS,
    FILING_JOB_KEYS,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.firms import (
    ADD_EDIT,
    ELECTRONIC_FILING,
    Firm,
    FirmUser,
    default_permissions,
)

from tests.unit.test_filing_approval import (
    ATTORNEY,
    CASE_ID,
    COLLEAGUE,
    World,
    make_world,
)
from tests.unit.test_filing_credential_routes import (
    _PUBLIC_KEY,
    CLIENT_ID,
    ISSUER,
    KID,
    auth,
)

PARALEGAL = "00000000-0000-4000-8000-0000000094a1"
# A paralegal an admin HAS granted the feature to — refused by the rule, not
# only by the flag.
GRANTED_PARALEGAL = "00000000-0000-4000-8000-0000000094a2"
OUTSIDER = "00000000-0000-4000-8000-00000000077e"
OTHER_FIRM = "firm-0002"
PATH = f"/v1/cases/{CASE_ID}/filing-approval"


def user(
    subject: str,
    *,
    firm_id: str,
    role: str = "attorney",
    feature: str | None = ADD_EDIT,
) -> FirmUser:
    permissions = default_permissions(role)
    if feature is not None:
        permissions[ELECTRONIC_FILING] = feature
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role=role,
        is_admin=False,
        access_all_cases=True,
        permissions=permissions,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


class App:
    def __init__(self, world: World, *, queue: bool = True) -> None:
        self.world = world
        firms = MemoryFirmStore()
        for firm_id in (world.firm_id, OTHER_FIRM):
            firms.create_firm(
                Firm(
                    id=firm_id,
                    name=f"Firm {firm_id}",
                    status="active",
                    created_at="2026-01-01T00:00:00.000Z",
                    updated_at="2026-01-01T00:00:00.000Z",
                )
            )
        firms.add_user(user(ATTORNEY, firm_id=world.firm_id))
        firms.add_user(user(COLLEAGUE, firm_id=world.firm_id))
        firms.add_user(
            user(PARALEGAL, firm_id=world.firm_id, role="paralegal", feature=None)
        )
        firms.add_user(user(GRANTED_PARALEGAL, firm_id=world.firm_id, role="paralegal"))
        firms.add_user(user(OUTSIDER, firm_id=OTHER_FIRM))
        deps = world.deps
        self.client = create_app(
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
                case_store=deps.case_store,
                firm_store=firms,
                access_log=world.log,
                debtor_store=deps.debtor_store,
                case_entity_store=deps.entity_store,
                packet_store=deps.packet_store,
                tax_id_store=deps.tax_id_store,
                tax_id_cipher=LocalTaxIdCipher(),
                filing_credential_store=world.credentials,
                filing_authorization_store=world.authorizations,
                filing_approval_store=world.approvals,
                filing_store=world.filings,
                document_store=world.documents,
                filing_queue=world.queue if queue else None,
            )
        ).test_client()

    def read(self, subject: str = ATTORNEY):
        return self.client.get(PATH, headers=auth(subject))

    def approve(self, subject: str = ATTORNEY, *, signed_in_ago=0, digest=None, **body):
        if digest is None:
            digest = self.read(subject).get_json()["basis"]["digest"]
        return self.client.post(
            PATH,
            json={"digest": digest, **body},
            headers=auth(subject, signed_in_ago=signed_in_ago),
        )


@pytest.fixture
def app() -> App:
    return App(make_world())


def test_an_unauthenticated_caller_is_refused(app):
    assert app.client.get(PATH).status_code == 401
    assert app.client.post(PATH, json={}).status_code == 401


def test_a_paralegal_without_the_feature_is_refused_by_the_flag(app):
    assert app.read(PARALEGAL).status_code == 403
    response = app.client.post(PATH, json={"digest": "0" * 64}, headers=auth(PARALEGAL))
    assert response.status_code == 403
    assert app.world.queue.messages == []


def test_a_paralegal_granted_the_feature_is_refused_by_the_rule(app):
    response = app.approve(GRANTED_PARALEGAL)
    assert response.status_code == 403
    assert response.get_json()["error"] == "ForbiddenError"
    assert app.world.queue.messages == []


def test_another_firms_case_is_not_found(app):
    assert app.read(OUTSIDER).status_code == 404


def test_the_screen_is_served_what_it_will_approve(app):
    response = app.read()
    assert response.status_code == 200
    assert response.headers["Cache-Control"] == "no-store"
    body = response.get_json()
    assert "approval" not in body
    basis = body["basis"]
    assert basis["ready"] is True
    assert basis["court"]["code"] == "flmb"
    assert basis["signInMaxAgeSeconds"] == APPROVAL_SIGN_IN_MAX_AGE_SECONDS
    assert basis["documents"][0]["key"] == "form/b101"
    assert all(
        len(d["file"]["sha256"]) == 64
        for d in basis["documents"]
        if d["source"] == "packet"
    )
    assert basis["digest"] == app.world.basis().digest


def test_a_fresh_sign_in_approves_and_enqueues_exactly_one_job(app):
    response = app.approve(signed_in_ago=APPROVAL_SIGN_IN_MAX_AGE_SECONDS - 30)
    assert response.status_code == 201, response.get_json()
    approval = response.get_json()["approval"]
    assert approval["status"] == "pending"
    assert approval["approvedBy"] == ATTORNEY
    assert len(app.world.queue.messages) == 1
    message = app.world.queue.messages[0]
    assert set(message) == FILING_JOB_KEYS
    assert message["approval_id"] == approval["id"]
    assert app.read().get_json()["approval"]["id"] == approval["id"]


@pytest.mark.parametrize("signed_in_ago", [APPROVAL_SIGN_IN_MAX_AGE_SECONDS + 60, None])
def test_a_stale_session_is_refused(app, signed_in_ago):
    response = app.approve(signed_in_ago=signed_in_ago)
    assert response.status_code == 403
    assert response.get_json()["error"] == "ReauthenticationRequired"
    assert app.world.queue.messages == []
    assert "approval" not in app.read().get_json()
    assert app.world.actions()[-1] == (
        "filing.approve",
        "denied",
        "reauthentication_required",
    )


def test_an_attorney_who_does_not_own_a_login_for_the_court_is_refused(app):
    app.world.sign(COLLEAGUE)
    response = app.approve(COLLEAGUE)
    assert response.status_code == 403
    assert app.world.queue.messages == []


def test_a_digest_the_screen_did_not_show_is_refused(app):
    response = app.approve(digest="a" * 64)
    assert response.status_code == 409
    assert app.world.queue.messages == []


def test_a_missing_digest_is_a_400(app):
    response = app.client.post(PATH, json={}, headers=auth(ATTORNEY))
    assert response.status_code == 400


def test_a_document_reassembled_after_approval_shows_voided(app):
    assert app.approve().status_code == 201
    app.world.assemble()
    approval = app.read().get_json()["approval"]
    assert approval["status"] == "voided"
    assert approval["voidReason"] == "changed"


def test_a_debtor_edit_after_approval_shows_voided(app):
    assert app.approve().status_code == 201
    app.world.edit_debtor_phone("555-0102")
    approval = app.read(COLLEAGUE).get_json()["approval"]
    assert (approval["status"], approval["voidReason"]) == ("voided", "changed")


def test_cancelling_voids_the_pending_approval(app):
    assert app.approve().status_code == 201
    response = app.client.delete(PATH, headers=auth(COLLEAGUE))
    assert response.status_code == 200
    assert response.get_json()["approval"]["voidReason"] == "cancelled"
    assert app.client.delete(PATH, headers=auth(COLLEAGUE)).status_code == 409


def test_a_case_with_a_blocker_answers_which(app):
    app.world.deps.packet_store.packets.clear()
    body = app.read().get_json()["basis"]
    assert body["ready"] is False
    assert "packet" in body["blockers"]
    response = app.approve(digest=body["digest"])
    assert response.status_code == 409
    assert response.get_json()["error"] == "FilingSetNotReady"
    assert "packet" in response.get_json()["blockers"]


def test_without_a_filing_queue_nothing_is_approved():
    app = App(make_world(), queue=False)
    digest = app.read().get_json()["basis"]["digest"]
    response = app.approve(digest=digest)
    assert response.status_code == 503
    assert app.world.approvals.current(CASE_ID) is None


def test_a_filed_case_cannot_be_approved(app):
    case = app.world.case()
    app.world.deps.case_store.cases[CASE_ID] = replace(case, status="filed")
    body = app.read().get_json()["basis"]
    assert "filed" in body["blockers"]
    assert app.approve(digest=body["digest"]).status_code == 409
