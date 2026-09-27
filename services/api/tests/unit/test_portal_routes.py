"""The client principal (ADR 0023): the portal routes, the firm's invitation
routes, and the disjointness that keeps the two principal classes apart.

What this file exists to hold down, in the ADR's own words for PR 1:

  - a portal token reaches /v1/portal/me and 401s on /v1/me;
  - a staff token 401s on /v1/portal/me;
  - a revoked binding 403s with a `denied` access-log row;
  - two bindings cannot both hold debtor_2;

and, walked over the WHOLE URL map rather than a hand-picked list, that every
/v1/portal/ route admits the client class alone and no other route admits it
at all — so a route added next month is covered without anyone remembering.

Tokens are signed for real, as everywhere else. Every identifier below is
obviously fake. This repo is public.
"""

from __future__ import annotations

import re
import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.auth import CLIENT, PRINCIPAL_CLASS_ATTRIBUTE
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.client_binding_store import (
    MemoryClientBindingStore,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.firms import (
    ADD_EDIT,
    CLIENT_PORTAL,
    Firm,
    FirmUser,
    default_permissions,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
STAFF_CLIENT_ID = "exampleappclientid000000"
PORTAL_CLIENT_ID = "exampleportalclientid000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"  # firm A admin
BOB = "00000000-0000-4000-8000-00000000b0b0"  # firm A paralegal, no client_portal
DANA = "00000000-0000-4000-8000-00000000da4a"  # firm A paralegal, granted it
CAROL = "00000000-0000-4000-8000-0000000ca201"  # firm B admin

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


def staff(subject: str) -> dict[str, str]:
    return token(subject, STAFF_CLIENT_ID)


def portal(subject: str) -> dict[str, str]:
    return token(subject, PORTAL_CLIENT_ID)


def firm(firm_id: str, name: str, status: str = "active") -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status=status,
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def member(
    subject: str,
    firm_id: str = FIRM_A,
    *,
    is_admin: bool = False,
    grants: dict[str, str] | None = None,
) -> FirmUser:
    permissions = {**default_permissions("paralegal"), **(grants or {})}
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="paralegal",
        is_admin=is_admin,
        access_all_cases=True,
        permissions=permissions,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def firms():
    store = MemoryFirmStore()
    store.create_firm(firm(FIRM_A, "Example & Partners"))
    store.create_firm(firm(FIRM_B, "Other Firm LLP"))
    store.add_user(member(ALICE, is_admin=True))
    store.add_user(member(BOB))
    store.add_user(member(DANA, grants={CLIENT_PORTAL: ADD_EDIT}))
    store.add_user(member(CAROL, FIRM_B, is_admin=True))
    return store


@pytest.fixture
def bindings():
    return MemoryClientBindingStore()


@pytest.fixture
def directory():
    return MemoryUserDirectory()


@pytest.fixture
def access_log():
    return MemoryAccessLog()


@pytest.fixture
def mailer():
    return InMemoryMailerClient()


def build(firms, bindings, directory, access_log, mailer, **config: str):
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": STAFF_CLIENT_ID,
                    "AUTH_PORTAL_CLIENT_ID": PORTAL_CLIENT_ID,
                    **config,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=mailer,
            jwks_provider=StaticJwksProvider({KID: _PRIVATE_KEY.public_key()}),
            case_store=MemoryCaseStore(),
            access_log=access_log,
            firm_store=firms,
            user_directory=directory,
            client_binding_store=bindings,
        )
    )
    return app


@pytest.fixture
def app(firms, bindings, directory, access_log, mailer):
    return build(firms, bindings, directory, access_log, mailer)


@pytest.fixture
def client(app):
    return app.test_client()


def open_case(client, subject: str = ALICE) -> str:
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "court": "flmb", "division": "tampa"},
        headers=staff(subject),
    )
    assert response.status_code == 201
    return str(response.get_json()["id"])


def invite(client, case_id: str, *, email: str, roles=None, caller: str = ALICE):
    body: dict[str, object] = {"email": email, "displayName": "Pat Example"}
    if roles is not None:
        body["roles"] = roles
    return client.post(
        f"/v1/cases/{case_id}/portal/invitation", json=body, headers=staff(caller)
    )


def invited_subject(client, case_id: str, email: str = "pat@example.test", **kw) -> str:
    response = invite(client, case_id, email=email, **kw)
    assert response.status_code == 201, response.get_json()
    return str(response.get_json()["subject"])


def actions(access_log: MemoryAccessLog) -> list[tuple[str, str, str]]:
    return [(e.action, e.outcome, e.principal) for e in access_log.events]


# ── The ADR's done-when ─────────────────────────────────────────


def test_a_portal_token_reaches_portal_me_with_the_firms_name(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    response = client.get("/v1/portal/me", headers=portal(pat))

    assert response.status_code == 200
    assert response.get_json() == {
        "subject": pat,
        "displayName": "Pat Example",
        "roles": ["debtor_1"],
        "firm": {"name": "Example & Partners"},
    }


def test_a_portal_token_is_refused_by_the_staff_me_route(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    assert client.get("/v1/me", headers=portal(pat)).status_code == 401


def test_a_staff_token_is_refused_by_the_portal(client):
    assert client.get("/v1/portal/me", headers=staff(ALICE)).status_code == 401


def test_a_revoked_binding_is_refused_and_the_refusal_is_logged(client, access_log):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)
    assert client.get("/v1/portal/me", headers=portal(pat)).status_code == 200
    revoked = client.delete(
        f"/v1/cases/{case_id}/portal/clients/{pat}", headers=staff(ALICE)
    )
    assert revoked.status_code == 200

    response = client.get("/v1/portal/me", headers=portal(pat))

    assert response.status_code == 403
    denied = access_log.events[-1]
    assert (denied.case_id, denied.principal, denied.action, denied.outcome) == (
        case_id,
        pat,
        "portal.read",
        "denied",
    )


def test_two_bindings_cannot_both_hold_debtor_2(client):
    case_id = open_case(client)
    invited_subject(client, case_id, "pat@example.test", roles=["debtor_2"])

    response = invite(client, case_id, email="sam@example.test", roles=["debtor_2"])

    assert response.status_code == 409
    listed = client.get(
        f"/v1/cases/{case_id}/portal/clients", headers=staff(ALICE)
    ).get_json()["clients"]
    assert [c["roles"] for c in listed] == [["debtor_2"]]


# ── Resolution ──────────────────────────────────────────────────


def test_the_first_portal_request_activates_the_invitation(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    client.get("/v1/portal/me", headers=portal(pat))

    [listed] = client.get(
        f"/v1/cases/{case_id}/portal/clients", headers=staff(ALICE)
    ).get_json()["clients"]
    assert listed["status"] == "active"


def test_a_portal_request_is_on_the_access_log_as_the_client(client, access_log):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    client.get("/v1/portal/me", headers=portal(pat))

    assert actions(access_log)[-1] == ("portal.read", "allowed", pat)


def test_a_pool_user_with_no_binding_is_refused_without_a_case_to_log(
    client, access_log, directory
):
    stranger = directory.create_user("stranger@example.test")

    response = client.get("/v1/portal/me", headers=portal(stranger))

    assert response.status_code == 403
    assert access_log.events == []


def test_a_suspended_firms_clients_are_suspended_with_it(client, firms, access_log):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)
    firms.update_firm(firm(FIRM_A, "Example & Partners", status="suspended"))

    response = client.get("/v1/portal/me", headers=portal(pat))

    assert response.status_code == 403
    assert actions(access_log)[-1] == ("portal.read", "denied", pat)


def test_a_client_signed_in_to_the_staff_app_resolves_no_firm(client):
    """A pool account can sign in through ANY app client of the pool. A
    debtor who does so through the web client gets a staff-profile token —
    and their binding must not read as a firm user."""
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    me = client.get("/v1/me", headers=staff(pat))

    assert me.status_code == 200
    assert "firm" not in me.get_json()
    assert client.get("/v1/cases", headers=staff(pat)).status_code == 403


@pytest.mark.parametrize(
    "config",
    [
        {"AUTH_PORTAL_CLIENT_ID": ""},
        {"AUTH_PORTAL_CLIENT_ID": STAFF_CLIENT_ID},
    ],
    ids=["unset", "same-as-staff"],
)
def test_the_portal_fails_closed_unless_its_client_is_disjoint(
    firms, bindings, directory, access_log, mailer, config
):
    app = build(firms, bindings, directory, access_log, mailer, **config)
    client = app.test_client()
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    assert client.get("/v1/portal/me", headers=staff(pat)).status_code == 401
    assert client.get("/v1/portal/me", headers=portal(pat)).status_code == 401


# ── Invitations ─────────────────────────────────────────────────


def test_an_invitation_mints_the_account_and_sends_a_secret_free_context_mail(
    client, directory, mailer, access_log
):
    case_id = open_case(client)

    response = invite(client, case_id, email="Pat@Example.TEST")

    assert response.status_code == 201
    body = response.get_json()
    assert body["subject"] == directory.subjects["pat@example.test"]
    assert (body["email"], body["status"], body["roles"]) == (
        "pat@example.test",
        "invited",
        ["debtor_1"],
    )
    [(email, _key)] = mailer.sent
    assert email.category == "client_invitation"
    assert email.to_address == "pat@example.test"
    assert "Example & Partners" in email.text_body
    assert "http://localhost:3000/portal" in email.text_body
    assert actions(access_log)[-1] == ("client.invite", "allowed", ALICE)


def test_one_login_for_both_spouses_is_recorded_as_the_firms_choice(client, access_log):
    case_id = open_case(client)

    invited_subject(client, case_id, roles=["debtor_1", "debtor_2"])

    assert access_log.events[-1].roles == "debtor_1,debtor_2"


def test_inviting_the_second_spouse_narrows_the_first(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id, roles=["debtor_1", "debtor_2"])

    sam = invited_subject(client, case_id, "sam@example.test", roles=["debtor_2"])

    listed = client.get(
        f"/v1/cases/{case_id}/portal/clients", headers=staff(ALICE)
    ).get_json()["clients"]
    assert {c["subject"]: c["roles"] for c in listed} == {
        pat: ["debtor_1"],
        sam: ["debtor_2"],
    }


def test_a_role_conflict_is_refused_before_any_account_is_minted(client, directory):
    case_id = open_case(client)
    invited_subject(client, case_id, roles=["debtor_1"])

    invite(client, case_id, email="sam@example.test", roles=["debtor_1"])

    assert "sam@example.test" not in directory.subjects


def test_an_address_with_an_account_and_no_binding_of_ours_is_a_conflict(
    client, directory
):
    directory.create_user("colleague@example.test")
    case_id = open_case(client)

    response = invite(client, case_id, email="colleague@example.test")

    assert response.status_code == 409


def test_a_revoked_client_is_rebound_to_a_refiled_case_as_the_same_person(client):
    first_case = open_case(client)
    pat = invited_subject(client, first_case)
    client.delete(f"/v1/cases/{first_case}/portal/clients/{pat}", headers=staff(ALICE))
    refiled = open_case(client)

    response = invite(client, refiled, email="pat@example.test")

    assert response.status_code == 201
    assert response.get_json()["subject"] == pat


def test_a_client_with_live_access_cannot_be_invited_to_a_second_case(client):
    first_case = open_case(client)
    invited_subject(client, first_case)
    second_case = open_case(client)

    response = invite(client, second_case, email="pat@example.test")

    assert response.status_code == 409


@pytest.mark.parametrize(
    ("caller", "status"),
    [(BOB, 403), (DANA, 201), (ALICE, 201)],
    ids=["not-granted", "granted", "admin"],
)
def test_inviting_needs_the_client_portal_feature(client, caller, status):
    case_id = open_case(client)

    assert (
        invite(client, case_id, email="pat@example.test", caller=caller).status_code
        == status
    )


def test_another_firms_case_is_a_404(client):
    case_id = open_case(client)

    response = invite(client, case_id, email="pat@example.test", caller=CAROL)

    assert response.status_code == 404


# ── Resend and revoke ───────────────────────────────────────────


def test_an_unused_invitation_is_resent_through_cognito_and_the_mailer(
    client, directory, mailer
):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)

    response = client.post(
        f"/v1/cases/{case_id}/portal/clients/{pat}/resend", headers=staff(ALICE)
    )

    assert response.status_code == 204
    assert directory.resent == ["pat@example.test"]
    assert len(mailer.sent) == 2


def test_a_used_invitation_is_not_resent(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)
    client.get("/v1/portal/me", headers=portal(pat))

    response = client.post(
        f"/v1/cases/{case_id}/portal/clients/{pat}/resend", headers=staff(ALICE)
    )

    assert response.status_code == 409


def test_revoking_is_logged_with_the_roles_and_frees_them(client, access_log):
    case_id = open_case(client)
    pat = invited_subject(client, case_id, roles=["debtor_2"])

    response = client.delete(
        f"/v1/cases/{case_id}/portal/clients/{pat}", headers=staff(ALICE)
    )

    assert response.get_json()["status"] == "revoked"
    assert (access_log.events[-1].action, access_log.events[-1].roles) == (
        "client.revoke",
        "debtor_2",
    )
    assert (
        invite(
            client, case_id, email="sam@example.test", roles=["debtor_2"]
        ).status_code
        == 201
    )


def test_a_client_on_another_case_is_a_404_on_this_one(client):
    case_id = open_case(client)
    pat = invited_subject(client, case_id)
    other = open_case(client)

    response = client.delete(
        f"/v1/cases/{other}/portal/clients/{pat}", headers=staff(ALICE)
    )

    assert response.status_code == 404


# ── Disjointness, over the whole URL map ────────────────────────

# Routes that authenticate nobody by design — each has its reason in its own
# module docstring. They answer a portal bearer exactly as they answer no
# bearer at all, which is the point: nothing here reads the token.
PUBLIC_RULES = {
    "/health",
    "/v1/waitlist",
    "/v1/unsubscribe",
}

_PARAMETER = re.compile(r"<(?:[^:<>]+:)?([^<>]+)>")


def _rules(app):
    for rule in app.url_map.iter_rules():
        if rule.endpoint == "static":
            continue
        for method in sorted(rule.methods - {"HEAD", "OPTIONS"}):
            yield rule, method


def test_every_portal_rule_admits_clients_and_no_other_rule_does(app):
    """The static half: which principal class each registered view admits,
    read off the decorator's stamp."""
    wrong: list[str] = []
    for rule, method in _rules(app):
        admits = getattr(
            app.view_functions[rule.endpoint], PRINCIPAL_CLASS_ATTRIBUTE, None
        )
        is_portal = rule.rule.startswith("/v1/portal/")
        if is_portal != (admits == CLIENT):
            wrong.append(f"{method} {rule.rule} admits {admits!r}")
    assert not wrong, "\n".join(wrong)
    assert any(rule.rule.startswith("/v1/portal/") for rule, _ in _rules(app))


def test_no_route_outside_the_portal_accepts_a_portal_token(app, client):
    """The behavioural half: a VALID portal token, for a client with a live
    binding, sent to every rule outside /v1/portal/. Each must answer 401 —
    the staff verify profile refusing the client id — except the public
    routes, which read no token at all."""
    case_id = open_case(client)
    pat = invited_subject(client, case_id)
    assert client.get("/v1/portal/me", headers=portal(pat)).status_code == 200

    admitted: list[str] = []
    for rule, method in _rules(app):
        if rule.rule.startswith("/v1/portal/") or rule.rule in PUBLIC_RULES:
            continue
        path = _PARAMETER.sub(case_id, rule.rule)
        response = client.open(path, method=method, headers=portal(pat), json={})
        if response.status_code != 401:
            admitted.append(f"{method} {rule.rule} -> {response.status_code}")
    assert not admitted, "\n".join(admitted)


def test_no_portal_route_accepts_a_staff_token(app, client):
    refused = [
        rule.rule
        for rule, method in _rules(app)
        if rule.rule.startswith("/v1/portal/")
        and client.open(
            _PARAMETER.sub("x", rule.rule), method=method, headers=staff(ALICE)
        ).status_code
        != 401
    ]

    assert not refused
