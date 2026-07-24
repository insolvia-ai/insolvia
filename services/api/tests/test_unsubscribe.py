"""Unsubscribe tokens and POST /v1/unsubscribe (issue #80 / 6.8).

The token is the only thing standing between an unsubscribe link and a
"suppress anyone's address" button, so most of what is asserted here is what
the verifier *refuses*.
"""

from __future__ import annotations

import base64

import pytest

from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import AppConfig, load_config
from insolvia_api.core.errors import ValidationError
from insolvia_api.core.unsubscribe import (
    UnsubscribeSecretMissing,
    mint_token,
    token_age_seconds,
    unsubscribe_url,
    verify_token,
)

SECRET = "test-unsubscribe-secret"
OTHER_SECRET = "a-different-secret"
ADDRESS = "ada@example.com"


# ── the token ────────────────────────────────────────────────────────


def test_round_trips_an_address():
    assert verify_token(mint_token(ADDRESS, secret=SECRET), secret=SECRET) == ADDRESS


def test_normalizes_case_and_whitespace():
    # The mailer hashes address.strip().lower(); a token minted for the mixed
    # -case form must resolve to the same suppression entry.
    token = mint_token("  Ada@Example.COM ", secret=SECRET)

    assert verify_token(token, secret=SECRET) == ADDRESS


def test_tokens_differ_per_address():
    first = mint_token(ADDRESS, secret=SECRET, issued_at=1_700_000_000)
    second = mint_token("bob@example.com", secret=SECRET, issued_at=1_700_000_000)

    assert first != second


def test_rejects_a_token_signed_with_another_secret():
    token = mint_token(ADDRESS, secret=OTHER_SECRET)

    with pytest.raises(ValidationError, match="invalid"):
        verify_token(token, secret=SECRET)


def test_rejects_a_swapped_address():
    """The attack the HMAC exists to stop: take your own valid token, edit the
    address inside it, and unsubscribe someone else."""
    version, payload, signature = mint_token(ADDRESS, secret=SECRET).split(".")
    decoded = base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)).decode()
    tampered_payload = decoded.replace(ADDRESS, "victim@example.com")
    forged = ".".join(
        [
            version,
            base64.urlsafe_b64encode(tampered_payload.encode()).decode().rstrip("="),
            signature,
        ]
    )

    with pytest.raises(ValidationError, match="invalid"):
        verify_token(forged, secret=SECRET)


@pytest.mark.parametrize(
    "token",
    [
        "",
        "not-a-token",
        "v1.only-two-parts",
        "v2.abc.def",
        "v1.!!!.###",
        "v1..",
        "v1.abc.def.ghi",
    ],
)
def test_rejects_malformed_tokens(token):
    with pytest.raises(ValidationError, match="invalid"):
        verify_token(token, secret=SECRET)


def test_rejects_an_absurdly_long_token():
    with pytest.raises(ValidationError, match="invalid"):
        verify_token("v1." + "a" * 5000 + ".b", secret=SECRET)


def test_refuses_to_mint_without_a_secret():
    # A missing secret must never degrade to "unsigned tokens are fine" —
    # that would make every made-up token valid. And it is not a
    # ValidationError, because the caller did nothing wrong.
    with pytest.raises(UnsubscribeSecretMissing):
        mint_token(ADDRESS, secret="")

    assert not issubclass(UnsubscribeSecretMissing, ValidationError)


def test_refuses_to_verify_without_a_secret():
    token = mint_token(ADDRESS, secret=SECRET)

    with pytest.raises(UnsubscribeSecretMissing):
        verify_token(token, secret="")


def test_rejects_an_address_that_would_shift_the_payload_fields():
    # The payload is colon-delimited "1:<issued_at>:<address>"; an address
    # containing a colon could otherwise be parsed as a different address.
    with pytest.raises(ValidationError, match="valid unsubscribe subject"):
        mint_token("a:b@example.com", secret=SECRET)


def test_does_not_expire():
    """Deliberate: a person who kept an old email and clicks unsubscribe must
    still be unsubscribed. Expiry would be a compliance problem dressed up as
    a security control."""
    ancient = mint_token(ADDRESS, secret=SECRET, issued_at=1)

    assert verify_token(ancient, secret=SECRET) == ADDRESS


def test_reports_token_age():
    token = mint_token(ADDRESS, secret=SECRET, issued_at=1_700_000_000)

    assert token_age_seconds(token, secret=SECRET, now=1_700_000_060) == 60


def test_builds_a_link_on_the_marketing_origin():
    url = unsubscribe_url(
        ADDRESS, secret=SECRET, marketing_origin="https://staging-www.insolvia.ai/"
    )

    assert url.startswith("https://staging-www.insolvia.ai/unsubscribe?token=v1.")
    token = url.split("token=", 1)[1]
    assert verify_token(token, secret=SECRET) == ADDRESS


# ── the endpoint ─────────────────────────────────────────────────────


def config(secret: str | None = SECRET) -> AppConfig:
    base = load_config({})
    return AppConfig(
        environment=base.environment,
        waitlist_table_name=base.waitlist_table_name,
        mailer_api_url=base.mailer_api_url,
        unsubscribe_secret=secret,
        marketing_origin=base.marketing_origin,
        cors_allowed_origins=base.cors_allowed_origins,
        cors_allow_localhost=base.cors_allow_localhost,
    )


@pytest.fixture
def mailer():
    return InMemoryMailerClient()


def make_client(mailer, secret=SECRET):
    app = create_app(
        ApiDependencies(
            config=config(secret),
            waitlist_store=MemoryWaitlistStore(),
            mailer=mailer,
        )
    )
    return app.test_client()


def test_suppresses_on_a_valid_token(mailer):
    client = make_client(mailer)

    response = client.post(
        "/v1/unsubscribe", json={"token": mint_token(ADDRESS, secret=SECRET)}
    )

    assert response.status_code == 202
    assert response.get_json() == {"status": "unsubscribed"}
    assert mailer.suppressed == [(ADDRESS, "unsubscribe")]


def test_is_idempotent(mailer):
    client = make_client(mailer)
    token = mint_token(ADDRESS, secret=SECRET)

    first = client.post("/v1/unsubscribe", json={"token": token})
    second = client.post("/v1/unsubscribe", json={"token": token})

    # A doubled click, or a mail client retrying its one-click POST, sees the
    # same success both times.
    assert first.status_code == second.status_code == 202
    assert mailer.suppressed == [(ADDRESS, "unsubscribe")] * 2


def test_rejects_a_forged_token_without_suppressing(mailer):
    client = make_client(mailer)

    response = client.post(
        "/v1/unsubscribe", json={"token": mint_token(ADDRESS, secret=OTHER_SECRET)}
    )

    assert response.status_code == 400
    assert mailer.suppressed == []


@pytest.mark.parametrize(
    "payload", [{}, {"token": None}, {"token": 42}, {"token": ""}, []]
)
def test_rejects_a_malformed_body(mailer, payload):
    client = make_client(mailer)

    assert client.post("/v1/unsubscribe", json=payload).status_code == 400
    assert mailer.suppressed == []


def test_response_reveals_nothing_about_the_address(mailer):
    client = make_client(mailer)

    response = client.post(
        "/v1/unsubscribe", json={"token": mint_token(ADDRESS, secret=SECRET)}
    )

    # Reachable by anyone holding a link, so the body must not confirm the
    # address, whether it was already suppressed, or whether it has an account.
    assert ADDRESS not in response.get_data(as_text=True)


def test_fails_loudly_when_no_secret_is_configured(mailer):
    client = make_client(mailer, secret=None)

    response = client.post("/v1/unsubscribe", json={"token": "v1.a.b"})

    # Without a key there is no way to tell a real token from a made-up one.
    # Answering 200 would be a lie, and answering 400 would blame the caller.
    assert response.status_code == 500
    assert mailer.suppressed == []
