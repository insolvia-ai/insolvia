"""Cognito JWT verification and GET /v1/me (issue #79 / 7.4).

Every token here is signed for real: the module generates an RSA keypair once,
serves its public half through the in-memory JwksProvider, and mints tokens
with PyJWT. So these tests exercise the actual RS256 path — no mock verifier,
no patched `decode` — while touching no network and no Cognito.

Like tests/test_unsubscribe.py, most of what is asserted is what the verifier
*refuses*. Auth code that only has a happy-path test is auth code nobody has
checked.

Every id below is obviously fake. This repo is public; no real pool id, app
client id, or subject ever appears here.
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
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider

# Obviously-fake values. The issuer is shaped like a real Cognito issuer so
# the string handling is exercised, but names no pool that exists.
ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
OTHER_ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_OTHER000"
CLIENT_ID = "exampleappclientid000000"
OTHER_CLIENT_ID = "someotherappclientid0000"
SUBJECT = "00000000-0000-4000-8000-000000000001"
# With username_attributes = ["email"] the pool generates this; it is NOT an
# email address, and the test asserts the endpoint passes it through as-is.
USERNAME = "00000000-0000-4000-8000-000000000001"
KID = "test-key-1"
UNKNOWN_KID = "test-key-absent"

# One keypair for the module: generation is the slow part, and every test
# wants the same "the provider knows this key" starting point.
_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()

# A second keypair, never published through the provider — signing with it
# under a published `kid` is how the tampered-signature case is built.
_IMPOSTER_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)


def make_token(
    *,
    issuer: str = ISSUER,
    client_id: str = CLIENT_ID,
    token_use: str = "access",
    subject: str | None = SUBJECT,
    username: str | None = USERNAME,
    scope: str | None = "aws.cognito.signin.user.admin",
    expires_in: int = 3600,
    kid: str = KID,
    key: object = None,
    extra: dict[str, object] | None = None,
) -> str:
    """Mint a token shaped exactly like a Cognito access token."""
    now = int(time.time())
    claims: dict[str, object] = {
        "iss": issuer,
        "client_id": client_id,
        "token_use": token_use,
        "iat": now,
        "auth_time": now,
        "exp": now + expires_in,
        "jti": "00000000-0000-4000-8000-0000000000ff",
        "origin_jti": "00000000-0000-4000-8000-0000000000fe",
    }
    if subject is not None:
        claims["sub"] = subject
    if username is not None:
        claims["username"] = username
    if scope is not None:
        claims["scope"] = scope
    if extra:
        claims.update(extra)
    return jwt.encode(
        claims,
        key if key is not None else _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": kid},
    )


def config(**overrides: str):
    environ = {
        "INSOLVIA_ENV": "local",
        "AUTH_ISSUER_URL": ISSUER,
        "AUTH_CLIENT_ID": CLIENT_ID,
    }
    environ.update(overrides)
    return load_config(environ)


def make_client(*, app_config=None, provider=None, firm_store=None):
    """A test client with auth composed, mirroring test_unsubscribe.py's
    local helper for cases that need config the shared fixture does not have.
    """
    app = create_app(
        ApiDependencies(
            config=app_config if app_config is not None else config(),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=(
                provider
                if provider is not None
                else StaticJwksProvider({KID: _PUBLIC_KEY})
            ),
            # Composed even when empty. /v1/me resolves the accessor to report
            # a firm, and `resolve_accessor` RAISES rather than degrading when
            # the store is absent — a deployment that cannot resolve anyone
            # must not look like every user being unprovisioned. An empty store
            # is the honest "signed in, not yet in a firm" case.
            firm_store=MemoryFirmStore() if firm_store is None else firm_store,
        )
    )
    return app.test_client()


@pytest.fixture
def auth_client():
    return make_client()


def get_me(client, token: str | None):
    headers = {"Authorization": f"Bearer {token}"} if token is not None else {}
    return client.get("/v1/me", headers=headers)


# ── the happy path ───────────────────────────────────────────────────


def test_valid_access_token_returns_the_claims_derived_identity(auth_client):
    response = get_me(auth_client, make_token())

    assert response.status_code == 200
    assert response.content_type == "application/json"
    body = response.get_json()
    assert body["subject"] == SUBJECT
    assert body["username"] == USERNAME
    assert body["clientId"] == CLIENT_ID
    assert body["scopes"] == ["aws.cognito.signin.user.admin"]
    assert isinstance(body["expiresAt"], int)


def test_me_body_has_exactly_the_contract_keys(auth_client):
    # The api-client package pins this shape; a silently added key is a
    # contract change and should fail here first.
    body = get_me(auth_client, make_token()).get_json()

    assert set(body) == {
        "subject",
        "username",
        "clientId",
        "scopes",
        "expiresAt",
    }
    # `firm` is absent rather than null for a caller who is in no firm — the
    # same absent-not-null contract nextCursor follows.


def test_me_omits_the_firm_for_someone_not_in_one():
    """SIGNED IN, NOT PROVISIONED — a state, not an error.

    Every other authenticated route answers 403 for this caller. Here the
    absence of `firm` is the answer, which is what lets a client render
    "ask your firm's admin to add you" instead of an error screen. It is also
    the whole reason accessor resolution is separate from require_auth.
    """
    body = get_me(make_client(), make_token()).get_json()
    assert "firm" not in body


def test_me_reports_the_firm_and_the_effective_permissions():
    from insolvia_core.firms import (
        ADD_EDIT,
        FIRM_ADMINISTRATION,
        Firm,
        FirmUser,
        default_permissions,
    )

    firms = MemoryFirmStore()
    firm_id = "00000000-0000-4000-8000-00000000f18a"
    firms.create_firm(
        Firm(
            id=firm_id,
            name="Example & Partners",
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    firms.add_user(
        FirmUser(
            firm_id=firm_id,
            subject=SUBJECT,
            email="admin@example.test",
            first_name="Example",
            last_name="Admin",
            role="attorney",
            is_admin=True,
            access_all_cases=False,
            permissions=default_permissions("attorney"),
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )

    body = get_me(make_client(firm_store=firms), make_token()).get_json()
    assert body["firm"]["id"] == firm_id
    assert body["firm"]["name"] == "Example & Partners"
    assert body["firm"]["isAdmin"] is True
    # EFFECTIVE, not stored. The row says firm_administration: hidden and this
    # admin can nonetheless manage users — a client rendering the stored map
    # would hide the button and then be surprised the endpoint works. That is
    # the opposite choice from firm_user_json, which is an admin looking at
    # somebody's record and needs the two facts apart.
    assert default_permissions("attorney")[FIRM_ADMINISTRATION] != ADD_EDIT
    assert body["firm"]["permissions"][FIRM_ADMINISTRATION] == ADD_EDIT


def test_me_never_returns_an_email():
    # username_attributes = ["email"] means the access token carries no email
    # claim at all. Guard against a future change that starts leaking one.
    body = get_me(make_client(), make_token()).get_json()

    assert not any("email" in key for key in body)


# ── PATCH /v1/me — the self-service rename (#216 / 11.16) ────────────


def patch_me(client, token: str, payload):
    return client.patch(
        "/v1/me", headers={"Authorization": f"Bearer {token}"}, json=payload
    )


def provisioned(*, permissions=None, is_admin=False, user_status="active"):
    """A store holding one firm and SUBJECT as a member of it."""
    from insolvia_core.firms import Firm, FirmUser, default_permissions

    firms = MemoryFirmStore()
    firm_id = "00000000-0000-4000-8000-00000000f18a"
    firms.create_firm(
        Firm(
            id=firm_id,
            name="Example & Partners",
            status="active",
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    firms.add_user(
        FirmUser(
            firm_id=firm_id,
            subject=SUBJECT,
            email="member@example.test",
            first_name="Original",
            last_name="Name",
            role="staff",
            is_admin=is_admin,
            access_all_cases=False,
            permissions=(
                default_permissions("staff") if permissions is None else permissions
            ),
            status=user_status,
            created_at="2026-01-01T00:00:00.000Z",
            updated_at="2026-01-01T00:00:00.000Z",
        )
    )
    return firms, firm_id


def test_a_member_renames_themselves_and_the_row_agrees():
    firms, firm_id = provisioned()

    response = patch_me(
        make_client(firm_store=firms),
        make_token(),
        {"firstName": "Corrected", "lastName": "Name"},
    )

    assert response.status_code == 200
    firm_block = response.get_json()["firm"]
    assert firm_block["firstName"] == "Corrected"
    assert firm_block["lastName"] == "Name"
    # The composed string rides along, derived. Every screen that only RENDERS
    # a name reads this one field and was untouched by the split.
    assert firm_block["displayName"] == "Corrected Name"
    # The row itself, not just the echo — the directory reads this.
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Corrected", "Name")


def test_either_half_of_a_name_may_be_corrected_alone():
    """The state the first-run prompt exists for. A row whose halves were
    derived from a pre-split display name can have a right first name and an
    empty surname, and making that person retype both would be rude."""
    firms, firm_id = provisioned()

    response = patch_me(
        make_client(firm_store=firms), make_token(), {"lastName": "Renamed"}
    )

    assert response.status_code == 200
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Original", "Renamed")


def test_a_client_still_sending_one_display_name_is_accepted():
    """THE TRANSITION ARM, and the deploy window it exists for: a browser
    holding the previous bundle keeps sending this shape until it reloads."""
    firms, firm_id = provisioned()

    response = patch_me(
        make_client(firm_store=firms), make_token(), {"displayName": "Corrected Name"}
    )

    assert response.status_code == 200
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Corrected", "Name")


def test_a_single_token_display_name_is_refused_rather_than_half_stored():
    """An old client must not be able to write a row that puts its own user in
    front of the first-run prompt on their next load — that would read as the
    new release having lost their name."""
    firms, firm_id = provisioned()

    response = patch_me(
        make_client(firm_store=firms), make_token(), {"displayName": "Cher"}
    )

    assert response.status_code == 400
    assert "displayName" in response.get_json()["fields"]
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Original", "Name")


def test_the_patch_answers_with_the_get_body():
    # One serializer for both — the client re-renders from the response
    # without a follow-up GET, so the shapes must never drift.
    firms, _ = provisioned()
    client = make_client(firm_store=firms)
    token = make_token()

    patched = patch_me(
        client, token, {"firstName": "Corrected", "lastName": "Name"}
    ).get_json()
    fetched = get_me(client, token).get_json()

    assert patched == fetched


def test_no_permission_is_needed_to_rename_yourself():
    """The point of the route. The permission axes govern what an admin may do
    to OTHERS; a paralegal with every feature hidden still owns their name."""
    from insolvia_core.firms import FEATURES, HIDDEN

    firms, firm_id = provisioned(permissions=dict.fromkeys(FEATURES, HIDDEN))

    response = patch_me(
        make_client(firm_store=firms),
        make_token(),
        {"firstName": "Still", "lastName": "Mine"},
    )

    assert response.status_code == 200
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Still", "Mine")


def test_a_rename_cannot_smuggle_a_promotion():
    firms, firm_id = provisioned(is_admin=False)

    response = patch_me(
        make_client(firm_store=firms),
        make_token(),
        {
            "firstName": "Corrected",
            "lastName": "Name",
            "isAdmin": True,
            "role": "attorney",
        },
    )

    assert response.status_code == 200
    row = firms.get_user(firm_id, SUBJECT)
    assert row.is_admin is False
    assert row.role == "staff"


def test_a_privilege_only_payload_is_a_validation_error():
    firms, firm_id = provisioned()

    response = patch_me(make_client(firm_store=firms), make_token(), {"isAdmin": True})

    assert response.status_code == 400
    assert firms.get_user(firm_id, SUBJECT).is_admin is False


def test_a_bad_name_reports_the_field():
    firms, _ = provisioned()

    response = patch_me(
        make_client(firm_store=firms),
        make_token(),
        {"firstName": " ", "lastName": "Name"},
    )

    assert response.status_code == 400
    # The half that was wrong, not a generic "name" — this is what puts the
    # server's message under the right input on the account screen.
    assert "firstName" in response.get_json()["fields"]


def test_a_caller_in_no_firm_cannot_rename():
    # Unlike GET, which reports the absence, there is no row to write here.
    response = patch_me(
        make_client(), make_token(), {"firstName": "No", "lastName": "Body"}
    )
    assert response.status_code == 403


def test_a_disabled_member_cannot_rename():
    firms, firm_id = provisioned(user_status="disabled")

    response = patch_me(
        make_client(firm_store=firms),
        make_token(),
        {"firstName": "Not", "lastName": "Anymore"},
    )

    assert response.status_code == 403
    row = firms.get_user(firm_id, SUBJECT)
    assert (row.first_name, row.last_name) == ("Original", "Name")


def test_multiple_scopes_split_on_whitespace(auth_client):
    token = make_token(scope="openid profile email")

    assert get_me(auth_client, token).get_json()["scopes"] == [
        "openid",
        "profile",
        "email",
    ]


def test_a_token_without_a_scope_claim_is_still_valid(auth_client):
    body = get_me(auth_client, make_token(scope=None)).get_json()

    assert body["scopes"] == []


# ── rejections ───────────────────────────────────────────────────────


def assert_unauthorized(response) -> None:
    assert response.status_code == 401
    assert response.get_json() == {
        "error": "Unauthorized",
        "message": "authentication required",
    }


def test_expired_token_is_rejected(auth_client):
    assert_unauthorized(get_me(auth_client, make_token(expires_in=-60)))


def test_wrong_issuer_is_rejected(auth_client):
    # Correctly signed by a key we trust, but minted by a different pool.
    assert_unauthorized(get_me(auth_client, make_token(issuer=OTHER_ISSUER)))


def test_wrong_client_id_is_rejected(auth_client):
    # Same pool, different app client — a token that was never meant for us.
    assert_unauthorized(get_me(auth_client, make_token(client_id=OTHER_CLIENT_ID)))


def test_tampered_signature_is_rejected(auth_client):
    # Claims and `kid` say the trusted key; the signature is from another.
    assert_unauthorized(get_me(auth_client, make_token(key=_IMPOSTER_KEY)))


def test_mutated_payload_is_rejected(auth_client):
    header, _payload, signature = make_token().split(".")
    other = make_token(subject="00000000-0000-4000-8000-00000000dead")
    swapped = f"{header}.{other.split('.')[1]}.{signature}"

    assert_unauthorized(get_me(auth_client, swapped))


def test_id_token_is_rejected(auth_client):
    # Signed by the same pool key and otherwise valid — token_use is the only
    # thing standing between an ID token and an authenticated request.
    assert_unauthorized(get_me(auth_client, make_token(token_use="id")))


def test_missing_authorization_header_is_rejected(auth_client):
    assert_unauthorized(auth_client.get("/v1/me"))


@pytest.mark.parametrize(
    "header",
    [
        "Bearer",
        "Bearer ",
        "Basic abcdef",
        "Token abcdef",
        make_token(),  # no scheme at all
        "Bearer a b",
        "",
        "   ",
    ],
)
def test_malformed_authorization_header_is_rejected(auth_client, header):
    response = auth_client.get("/v1/me", headers={"Authorization": header})

    assert_unauthorized(response)


def test_unknown_kid_is_rejected(auth_client):
    assert_unauthorized(get_me(auth_client, make_token(kid=UNKNOWN_KID)))


def test_token_without_a_kid_header_is_rejected(auth_client):
    token = jwt.encode(
        {"iss": ISSUER, "sub": SUBJECT, "exp": int(time.time()) + 60},
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
    )

    assert_unauthorized(get_me(auth_client, token))


def test_unsigned_alg_none_token_is_rejected(auth_client):
    token = jwt.encode(
        {
            "iss": ISSUER,
            "sub": SUBJECT,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "exp": int(time.time()) + 60,
        },
        key="",
        algorithm="none",
        headers={"kid": KID},
    )

    assert_unauthorized(get_me(auth_client, token))


def test_garbage_token_is_rejected(auth_client):
    assert_unauthorized(get_me(auth_client, "not.a.jwt"))


def test_token_without_a_subject_is_rejected(auth_client):
    assert_unauthorized(get_me(auth_client, make_token(subject=None)))


def test_not_yet_valid_token_is_rejected(auth_client):
    future = int(time.time()) + 3600

    assert_unauthorized(get_me(auth_client, make_token(extra={"nbf": future})))


# ── fail closed ──────────────────────────────────────────────────────


def test_missing_auth_config_fails_closed():
    # A deployment with no AUTH_ISSUER_URL/AUTH_CLIENT_ID must not answer 200
    # to a protected route — not even with an otherwise perfect token.
    client = make_client(app_config=load_config({"INSOLVIA_ENV": "local"}))

    assert_unauthorized(get_me(client, make_token()))


def test_missing_issuer_alone_fails_closed():
    client = make_client(
        app_config=load_config({"INSOLVIA_ENV": "local", "AUTH_CLIENT_ID": CLIENT_ID})
    )

    assert_unauthorized(get_me(client, make_token()))


def test_missing_jwks_provider_fails_closed():
    app = create_app(
        ApiDependencies(
            config=config(),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
        )
    )

    assert_unauthorized(get_me(app.test_client(), make_token()))


def test_health_still_works_without_auth_config():
    client = make_client(app_config=load_config({"INSOLVIA_ENV": "local"}))

    assert client.get("/health").status_code == 200


# ── the public routes must not regress ───────────────────────────────


def test_health_needs_no_credentials(auth_client):
    assert auth_client.get("/health").status_code == 200


def test_waitlist_needs_no_credentials(auth_client):
    response = auth_client.post(
        "/v1/waitlist",
        json={
            "name": "Ada Lovelace",
            "firm": "Lovelace & Byron LLP",
            "email": "ada@lovelace-law.example",
            "host": "www.insolvia.ai",
        },
    )

    assert response.status_code == 201


def test_unsubscribe_needs_no_credentials(auth_client):
    # No token, so the route is reached; it then rejects the *unsubscribe*
    # token on its own terms with a 400, never a 401.
    response = auth_client.post("/v1/unsubscribe", json={"token": "v1.bad.token"})

    assert response.status_code != 401


def test_a_bogus_bearer_header_does_not_break_a_public_route(auth_client):
    # The decorator is opt-in: an Authorization header on a public route is
    # simply ignored, never a reason to reject.
    response = auth_client.get("/health", headers={"Authorization": "Bearer nope"})

    assert response.status_code == 200


# ── the log line must never carry the token or a claim ───────────────


def test_rejection_logs_a_category_and_nothing_sensitive(auth_client, caplog):
    token = make_token(expires_in=-60)

    with caplog.at_level("INFO", logger="insolvia_api.api.auth"):
        get_me(auth_client, token)

    records = [r for r in caplog.records if r.name == "insolvia_api.api.auth"]
    assert records, "a rejection must be logged"
    record = records[0]
    assert record.reason == "expired"
    rendered = record.getMessage() + repr(record.__dict__)
    assert token not in rendered
    assert SUBJECT not in rendered
