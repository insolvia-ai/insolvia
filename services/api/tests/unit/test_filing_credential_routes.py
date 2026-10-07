"""The attorney's own CM/ECF credential (ADR 0024, guardrail 3):
`/v1/me/filing-credentials` — enrol, status, revoke.

What this file holds down:

  1. ONLY THE OWNER. No attorney id in any path or body; one attorney's
     credential is invisible to, and unrevocable by, another — an admin
     included — and answers the same 404 as one that never existed.
  2. NOTHING SECRET COMES BACK. Not on enrol, not on status: the response
     bodies never contain the password, the seed, or the envelope.
  3. THE API CANNOT OPEN. What the server stored opens under the WORKER's
     opener (composed here by the test, never by the app), and the app's
     dependencies hold a sealer and no opener at all.
  4. `electronic_filing` GATES IT — hidden by default for every role.

No real credential: the login is FAKE-ECF-USER, and each TOTP seed is
generated at test time. Tokens are signed for real, as everywhere else.
"""

from __future__ import annotations

import base64
import dataclasses
import os
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
from insolvia_core.adapters.memory.filing_credentials import (
    LocalCredentialOpener,
    LocalCredentialSealer,
    MemoryFilingCredentialStore,
)
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.filing_credentials import (
    CredentialUnavailableError,
    open_credential,
)
from insolvia_core.firms import (
    ADD_EDIT,
    ELECTRONIC_FILING,
    HIDDEN,
    VIEW_ONLY,
    Firm,
    FirmUser,
    default_permissions,
)

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
KID = "test-key-1"

FIRM_A = "00000000-0000-4000-8000-00000000f18a"
ATTORNEY = "00000000-0000-4000-8000-00000000a771"
COLLEAGUE = "00000000-0000-4000-8000-00000000c011"
ADMIN = "00000000-0000-4000-8000-00000000a11c"
VIEWER = "00000000-0000-4000-8000-00000000da4a"
BLOCKED = "00000000-0000-4000-8000-00000000b0b0"

LOGIN = "FAKE-ECF-USER"
PASSWORD = "FAKE-ECF-PASSWORD-not-a-real-one"

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def fresh_seed() -> str:
    return base64.b32encode(os.urandom(20)).decode("ascii")


def auth(subject: str) -> dict[str, str]:
    now = int(time.time())
    token = jwt.encode(
        {
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "sub": subject,
            "username": subject,
            "iat": now,
            "auth_time": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )
    return {"Authorization": f"Bearer {token}"}


def member(subject: str, level: str | None, *, is_admin: bool = False) -> FirmUser:
    permissions = default_permissions("attorney")
    if level is not None:
        permissions[ELECTRONIC_FILING] = level
    return FirmUser(
        firm_id=FIRM_A,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=is_admin,
        access_all_cases=False,
        permissions=permissions,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@dataclasses.dataclass
class World:
    client: object
    store: MemoryFilingCredentialStore
    log: MemoryAccessLog
    deps: ApiDependencies


@pytest.fixture
def world() -> World:
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
    firms.add_user(member(ATTORNEY, ADD_EDIT))
    firms.add_user(member(COLLEAGUE, ADD_EDIT))
    firms.add_user(member(ADMIN, None, is_admin=True))
    firms.add_user(member(VIEWER, VIEW_ONLY))
    # The default: an attorney nobody has granted the feature to.
    firms.add_user(member(BLOCKED, None))
    store = MemoryFilingCredentialStore()
    log = MemoryAccessLog()
    deps = ApiDependencies(
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
        access_log=log,
        firm_store=firms,
        user_directory=MemoryUserDirectory(),
        filing_credential_store=store,
        filing_credential_sealer=LocalCredentialSealer(),
    )
    return World(create_app(deps).test_client(), store, log, deps)


def enrol(world: World, subject: str = ATTORNEY, *, seed: str | None = None, **extra):
    body = {
        "login": LOGIN,
        "password": PASSWORD,
        "totp_seed": seed or fresh_seed(),
        **extra,
    }
    return world.client.post(  # type: ignore[attr-defined]
        "/v1/me/filing-credentials", json=body, headers=auth(subject)
    )


def status(world: World, subject: str = ATTORNEY):
    return world.client.get(  # type: ignore[attr-defined]
        "/v1/me/filing-credentials", headers=auth(subject)
    )


def revoke(world: World, credential_id: str, subject: str = ATTORNEY):
    return world.client.delete(  # type: ignore[attr-defined]
        f"/v1/me/filing-credentials/{credential_id}", headers=auth(subject)
    )


# ── Enrol and status ────────────────────────────────────────────


def test_enrol_answers_the_status_view_and_never_the_secret(world: World) -> None:
    seed = fresh_seed()
    response = enrol(world, seed=seed, courts=["txsb"])
    assert response.status_code == 201
    assert response.headers["Cache-Control"] == "no-store"
    body = response.get_json()
    assert set(body) == {"id", "login", "courts", "status", "created_at", "updated_at"}
    assert body["login"] == LOGIN
    assert body["courts"] == ["txsb"]
    assert body["status"] == "active"
    raw = response.get_data(as_text=True)
    assert PASSWORD not in raw
    assert seed not in raw


def test_status_lists_the_callers_own_and_carries_no_secret(world: World) -> None:
    seed = fresh_seed()
    enrolled = enrol(world, seed=seed).get_json()
    response = status(world)
    assert response.status_code == 200
    assert response.headers["Cache-Control"] == "no-store"
    assert response.get_json() == {"credentials": [enrolled]}
    raw = response.get_data(as_text=True)
    assert PASSWORD not in raw
    assert seed not in raw
    assert "ciphertext" not in raw
    assert "wrapped" not in raw.lower()


def test_a_bad_enrolment_is_a_400_naming_the_field(world: World) -> None:
    response = enrol(world, seed="not-base32!")
    assert response.status_code == 400
    assert "totp_seed" in response.get_json()["fields"]


def test_the_same_login_twice_is_a_409(world: World) -> None:
    assert enrol(world).status_code == 201
    assert enrol(world).status_code == 409


# ── Only the owner ──────────────────────────────────────────────


def test_a_colleague_sees_none_of_it_and_cannot_revoke_it(world: World) -> None:
    enrolled = enrol(world).get_json()
    assert status(world, COLLEAGUE).get_json() == {"credentials": []}
    assert revoke(world, enrolled["id"], COLLEAGUE).status_code == 404
    assert status(world).get_json() == {"credentials": [enrolled]}


def test_an_admin_holding_every_feature_cannot_reach_it_either(world: World) -> None:
    enrolled = enrol(world).get_json()
    assert status(world, ADMIN).get_json() == {"credentials": []}
    assert revoke(world, enrolled["id"], ADMIN).status_code == 404


def test_a_body_naming_another_attorney_is_ignored(world: World) -> None:
    enrol(world, attorney_id=COLLEAGUE)
    assert len(status(world).get_json()["credentials"]) == 1
    assert status(world, COLLEAGUE).get_json() == {"credentials": []}


# ── Revoke, and the worker's open ───────────────────────────────


def _worker_open(world: World, credential_id: str, attorney: str = ATTORNEY):
    """What services/filing will do (ADR 0024 PR 7) — composed HERE, by the
    test, because nothing in the API holds an opener."""
    return open_credential(
        firm_id=FIRM_A,
        attorney_id=attorney,
        credential_id=credential_id,
        filing_id="filing-0001",
        purpose="sign_in",
        opener=LocalCredentialOpener(),
        store=world.store,
        access_log=world.log,
    )


def test_what_the_api_sealed_the_worker_opens(world: World) -> None:
    seed = fresh_seed()
    enrolled = enrol(world, seed=seed).get_json()
    secret = _worker_open(world, enrolled["id"])
    assert secret.password == PASSWORD
    assert secret.totp_seed == seed


def test_revoke_destroys_it_and_the_next_open_fails(world: World) -> None:
    enrolled = enrol(world).get_json()
    response = revoke(world, enrolled["id"])
    assert response.status_code == 204
    assert world.store.items == {}
    assert status(world).get_json() == {"credentials": []}
    with pytest.raises(CredentialUnavailableError):
        _worker_open(world, enrolled["id"])
    assert revoke(world, enrolled["id"]).status_code == 404


def test_every_act_is_in_the_access_log(world: World) -> None:
    enrolled = enrol(world).get_json()
    _worker_open(world, enrolled["id"])
    revoke(world, enrolled["id"])
    rows = [
        (e.action, e.credential_id, e.principal, e.outcome)
        for e in world.log.events
        if e.credential_id is not None
    ]
    assert rows == [
        ("credential.enrol", enrolled["id"], ATTORNEY, "allowed"),
        ("credential.open", enrolled["id"], ATTORNEY, "allowed"),
        ("credential.revoke", enrolled["id"], ATTORNEY, "allowed"),
    ]


def test_a_malformed_credential_id_is_a_404(world: World) -> None:
    assert revoke(world, "not-a-uuid").status_code == 404


def test_the_api_holds_a_sealer_and_no_opener(world: World) -> None:
    fields = {f.name for f in dataclasses.fields(ApiDependencies)}
    assert "filing_credential_sealer" in fields
    assert not any("opener" in name for name in fields)
    assert not hasattr(world.deps.filing_credential_sealer, "open")


# ── The feature gate ────────────────────────────────────────────


def test_the_feature_is_hidden_by_default_for_every_role() -> None:
    for role in ("attorney", "paralegal", "staff"):
        assert default_permissions(role)[ELECTRONIC_FILING] == HIDDEN


def test_without_the_feature_every_route_is_403(world: World) -> None:
    assert status(world, BLOCKED).status_code == 403
    assert enrol(world, BLOCKED).status_code == 403
    assert (
        revoke(world, "00000000-0000-4000-8000-000000000000", BLOCKED).status_code
        == 403
    )


def test_view_only_reads_status_and_cannot_enrol(world: World) -> None:
    assert status(world, VIEWER).status_code == 200
    assert enrol(world, VIEWER).status_code == 403


def test_signed_out_is_401(world: World) -> None:
    assert world.client.get("/v1/me/filing-credentials").status_code == 401  # type: ignore[attr-defined]
