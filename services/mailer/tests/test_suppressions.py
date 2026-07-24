"""User-initiated suppression — POST /v1/services/<id>/suppressions (#80).

The mailer half of the unsubscribe path. The other two halves live in
services/api (which mints and verifies the HMAC token proving the request came
from the address's owner) and apps/insolvia_marketing (the page a person
actually lands on).
"""

from __future__ import annotations

import pytest

from insolvia_mailer.adapters.memory.auth import RegisteredServiceAuthorizer
from insolvia_mailer.adapters.memory.config import load_service_registry
from insolvia_mailer.adapters.memory.store import MemoryStore
from insolvia_mailer.api.app_factory import create_app
from insolvia_mailer.api.dependencies import ApiDependencies
from insolvia_mailer.core.errors import ValidationError
from insolvia_mailer.core.models import SuppressionRequest, recipient_hash

ENDPOINT = "/v1/services/insolvia_api/suppressions"


@pytest.fixture
def store():
    return MemoryStore()


@pytest.fixture
def client(store):
    app = create_app(
        ApiDependencies(
            services=load_service_registry(),
            store=store,
            authorizer=RegisteredServiceAuthorizer(),
            attachment_receiver=store,
        )
    )
    return app.test_client()


def body(address="recipient@example.com", reason="unsubscribe"):
    return {"schema_version": 1, "email_address": address, "reason": reason}


# ── the request model ────────────────────────────────────────────────


def test_parses_a_valid_request():
    parsed = SuppressionRequest.from_dict(body())
    assert parsed == SuppressionRequest(
        email_address="recipient@example.com", reason="unsubscribe"
    )


def test_normalizes_nothing_the_hash_does_not():
    # recipient_hash lower-cases and strips; the model keeps the address as
    # given so a caller's log line and the stored hash cannot disagree about
    # which address was meant.
    parsed = SuppressionRequest.from_dict(body(address="Recipient@Example.com"))
    assert parsed.email_address == "Recipient@Example.com"
    assert recipient_hash(parsed.email_address) == recipient_hash(
        "recipient@example.com"
    )


@pytest.mark.parametrize(
    "reason",
    ["bounce", "complaint", "unsubscribe"],
)
def test_accepts_every_known_reason(reason):
    assert SuppressionRequest.from_dict(body(reason=reason)).reason == reason


def test_rejects_an_unknown_reason():
    with pytest.raises(ValidationError, match="reason must be one of"):
        SuppressionRequest.from_dict(body(reason="because"))


def test_rejects_a_display_name_address():
    with pytest.raises(ValidationError, match="email_address"):
        SuppressionRequest.from_dict(body(address="Someone <a@example.com>"))


def test_rejects_unexpected_fields():
    payload = body()
    payload["suppress_everyone"] = True
    with pytest.raises(ValidationError, match="unsupported fields"):
        SuppressionRequest.from_dict(payload)


# ── the endpoint ─────────────────────────────────────────────────────


def test_suppresses_the_address(client, store):
    response = client.post(ENDPOINT, json=body())

    assert response.status_code == 202
    assert response.get_json()["status"] == "suppressed"
    assert store.is_suppressed("recipient@example.com")


def test_stores_only_a_hash(client, store):
    client.post(ENDPOINT, json=body())

    assert list(store.suppressions) == [recipient_hash("recipient@example.com")]
    assert "recipient@example.com" not in store.suppressions


def test_is_idempotent(client, store):
    first = client.post(ENDPOINT, json=body())
    second = client.post(ENDPOINT, json=body())

    # A doubled click, or a mail client retrying its one-click POST, must not
    # surface an error for the thing that already worked.
    assert first.status_code == second.status_code == 202
    assert len(store.suppressions) == 1


def test_rejects_an_unregistered_service(client):
    response = client.post("/v1/services/not_a_service/suppressions", json=body())

    assert response.status_code == 403


def test_rejects_an_invalid_body(client, store):
    response = client.post(ENDPOINT, json=body(reason="because"))

    assert response.status_code == 400
    assert store.suppressions == {}
