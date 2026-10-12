"""The written authorization (ADR 0024, guardrail 2): the text ships and is
digested exactly, a signature needs a fresh sign-in and the text as it
ships, and the record is stored current-plus-history.

How the vault USES the record — enrolment refused without it, opens refused
without it, withdrawal destroying credentials — is pinned in
test_filing_credentials, beside the vault's other rules.

Sign-in instants are fixed and far in the future; nothing reads the clock.
"""

from __future__ import annotations

import hashlib
from importlib import resources
from typing import Any

import pytest
from botocore.exceptions import ClientError
from insolvia_core.access_log import ACTIONS, access_item
from insolvia_core.adapters.aws.dynamo import from_attributes
from insolvia_core.adapters.aws.filing_credentials import (
    DynamoDbFilingAuthorizationStore,
)
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.filing_credentials import (
    MemoryFilingAuthorizationStore,
)
from insolvia_core.auth import ReauthenticationRequiredError
from insolvia_core.errors import ConflictError, ValidationError
from insolvia_core.filing_authorization import (
    SIGNATURE_MAX_AGE_SECONDS,
    TEXT_VERSION,
    FilingAuthorization,
    authorization_from_item,
    authorization_item,
    authorization_json,
    authorization_text,
    is_current,
    sign_authorization,
    text_digest,
    text_json,
)

FIRM = "firm-0001"
ATTORNEY = "00000000-0000-4000-8000-00000000a771"
OTHER_ATTORNEY = "00000000-0000-4000-8000-00000000b771"
SIGNED_IN_AT = 4_102_444_800  # 2100-01-01T00:00:00Z


def body(**overrides: str) -> dict[str, str]:
    return {
        "text_version": TEXT_VERSION,
        "text_digest": text_json()["digest"],
        **overrides,
    }


def sign(
    store: MemoryFilingAuthorizationStore,
    log: MemoryAccessLog,
    *,
    payload: object = None,
    authenticated_at: int | None = SIGNED_IN_AT,
    now: float = SIGNED_IN_AT + 60,
    attorney: str = ATTORNEY,
) -> FilingAuthorization:
    return sign_authorization(
        body() if payload is None else payload,
        firm_id=FIRM,
        attorney_id=attorney,
        authenticated_at=authenticated_at,
        now=now,
        store=store,
        access_log=log,
    )


# ── The text ────────────────────────────────────────────────────


def test_the_current_text_ships_in_the_package_and_the_digest_is_of_its_bytes() -> None:
    shipped = (
        resources.files("insolvia_core")
        .joinpath("agreements/filing-authorization")
        .joinpath(f"{TEXT_VERSION}.txt")
        .read_bytes()
    )
    served = text_json()
    assert served["version"] == TEXT_VERSION
    assert served["text"].encode("utf-8") == shipped
    assert served["digest"] == hashlib.sha256(shipped).hexdigest()


def test_the_text_is_marked_as_an_unreviewed_draft() -> None:
    """Until the maintainer and counsel replace it, the text itself says
    it must not be signed for real — and so does the version name every
    record carries."""
    text = authorization_text()
    assert text.startswith("DRAFT")
    assert "not legal advice" in text
    assert TEXT_VERSION.endswith("-draft")


@pytest.mark.parametrize(
    "phrase",
    [
        "approved that specific filing",
        "signing in again at the time of approval",
        "withdraw this authorization at any time",
        "immediately destroys every court login",
        "forbid a filer from sharing their login",
        "PACER terms of use",
    ],
)
def test_the_text_says_what_the_adr_requires_it_to_say(phrase: str) -> None:
    """ADR 0024: on guardrail 1's terms; withdrawal revokes; and the
    rules-based objection 'must say this plainly'."""
    assert phrase in authorization_text()


@pytest.mark.parametrize("version", ["", "../../errors", "2026-10-07.txt", "UPPER"])
def test_a_version_that_is_not_a_name_is_refused(version: str) -> None:
    with pytest.raises(ValueError, match="not an authorization text version"):
        authorization_text(version)


# ── Signing ─────────────────────────────────────────────────────


def test_a_fresh_sign_in_over_the_current_text_records_the_signature() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    signed = sign(store, log)
    assert signed.attorney_id == ATTORNEY
    assert signed.text_version == TEXT_VERSION
    assert signed.text_digest == text_digest(authorization_text())
    assert signed.authenticated_at == SIGNED_IN_AT
    assert store.get_current(FIRM, ATTORNEY) == signed
    assert store.history[signed.authorization_id] == signed
    assert is_current(signed, firm_id=FIRM, attorney_id=ATTORNEY)


def test_signing_is_logged_under_the_signature() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    signed = sign(store, log)
    (event,) = log.events
    assert event.action == "authorization.sign"
    assert event.authorization_id == signed.authorization_id
    assert event.principal == ATTORNEY
    assert access_item(event)["PK"] == f"AUTHORIZATION#{signed.authorization_id}"
    assert {"authorization.sign", "authorization.withdraw"} <= set(ACTIONS)


@pytest.mark.parametrize(
    "authenticated_at",
    [None, SIGNED_IN_AT - SIGNATURE_MAX_AGE_SECONDS],
    ids=["no-auth-time", "older-than-the-window"],
)
def test_a_stale_sign_in_cannot_sign(authenticated_at: int | None) -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    with pytest.raises(ReauthenticationRequiredError):
        sign(store, log, authenticated_at=authenticated_at, now=SIGNED_IN_AT + 1)
    assert store.current == {}
    assert log.events == []


@pytest.mark.parametrize(
    "overrides",
    [
        {"text_version": "2026-01-01-older"},
        {"text_digest": "0" * 64},
    ],
    ids=["another-version", "another-digest"],
)
def test_a_text_other_than_the_one_that_ships_cannot_be_signed(
    overrides: dict[str, str],
) -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    with pytest.raises(ConflictError):
        sign(store, log, payload=body(**overrides))
    assert store.current == {}


@pytest.mark.parametrize("payload", [[], {"text_version": TEXT_VERSION}, "x"])
def test_a_malformed_body_is_a_validation_error(payload: object) -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    with pytest.raises(ValidationError):
        sign(store, log, payload=payload)


def test_signing_twice_is_a_conflict() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    sign(store, log)
    with pytest.raises(ConflictError):
        sign(store, log)


def test_one_attorneys_signature_is_not_anothers() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    signed = sign(store, log)
    assert store.get_current(FIRM, OTHER_ATTORNEY) is None
    assert not is_current(signed, firm_id=FIRM, attorney_id=OTHER_ATTORNEY)
    assert not is_current(signed, firm_id="firm-0002", attorney_id=ATTORNEY)


def test_a_record_whose_digest_no_longer_matches_the_text_is_not_current() -> None:
    """An in-place edit of a shipped text would change its digest; every
    signature over the old bytes stops being current."""
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    signed = sign(store, log)
    tampered = FilingAuthorization(**{**signed.__dict__, "text_digest": "f" * 64})
    assert not is_current(tampered, firm_id=FIRM, attorney_id=ATTORNEY)


# ── The view and the item ───────────────────────────────────────


def test_the_status_view_is_pinned() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    assert authorization_json(None, firm_id=FIRM, attorney_id=ATTORNEY) == {
        "current_version": TEXT_VERSION,
        "current": False,
        "signature": None,
    }
    signed = sign(store, log)
    view = authorization_json(signed, firm_id=FIRM, attorney_id=ATTORNEY)
    assert view == {
        "current_version": TEXT_VERSION,
        "current": True,
        "signature": {
            "id": signed.authorization_id,
            "text_version": TEXT_VERSION,
            "text_digest": signed.text_digest,
            "signed_at": signed.signed_at,
        },
    }


def test_the_item_round_trips() -> None:
    store, log = MemoryFilingAuthorizationStore(), MemoryAccessLog()
    signed = sign(store, log)
    assert authorization_from_item(authorization_item(signed, sort_key="x")) == signed


# ── The DynamoDB adapter, with the transport faked at the client ─


class FakeDynamo:
    """put/get/delete with the two condition forms the store uses."""

    def __init__(self) -> None:
        self.items: dict[tuple[str, str], dict[str, Any]] = {}
        self.calls: list[tuple[str, str]] = []

    @staticmethod
    def _refuse(operation: str) -> ClientError:
        return ClientError(
            {"Error": {"Code": "ConditionalCheckFailedException"}}, operation
        )

    def _check(self, key: tuple[str, str], kwargs: dict[str, Any], op: str) -> None:
        condition = kwargs.get("ConditionExpression")
        held = self.items.get(key)
        if condition == "attribute_not_exists(SK)" and held is not None:
            raise self._refuse(op)
        if condition == "authorizationId = :held":
            wanted = kwargs["ExpressionAttributeValues"][":held"]["S"]
            if held is None or held["authorizationId"]["S"] != wanted:
                raise self._refuse(op)

    def put_item(self, **kwargs: Any) -> None:
        item = kwargs["Item"]
        key = (item["PK"]["S"], item["SK"]["S"])
        self._check(key, kwargs, "PutItem")
        self.calls.append(("put", key[1]))
        self.items[key] = item

    def get_item(self, **kwargs: Any) -> dict[str, Any]:
        assert kwargs["ConsistentRead"] is True
        key = (kwargs["Key"]["PK"]["S"], kwargs["Key"]["SK"]["S"])
        return {"Item": self.items[key]} if key in self.items else {}

    def delete_item(self, **kwargs: Any) -> dict[str, Any]:
        key = (kwargs["Key"]["PK"]["S"], kwargs["Key"]["SK"]["S"])
        self._check(key, kwargs, "DeleteItem")
        self.calls.append(("delete", key[1]))
        self.items.pop(key, None)
        return {}


def test_the_dynamodb_store_writes_current_first_and_withdraws_current_first() -> None:
    dynamo = FakeDynamo()
    store = DynamoDbFilingAuthorizationStore("t", client=dynamo)
    log = MemoryAccessLog()
    signed = sign_authorization(
        body(),
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        authenticated_at=SIGNED_IN_AT,
        now=SIGNED_IN_AT,
        store=store,
        access_log=log,
    )
    pk = f"ATTORNEY#{FIRM}#{ATTORNEY}"
    history = f"AUTHORIZATION#{signed.authorization_id}"
    assert dynamo.calls == [("put", "AUTHORIZATION"), ("put", history)]
    assert store.get_current(FIRM, ATTORNEY) == signed

    assert store.withdraw(signed, withdrawn_at="2100-01-01T00:05:00.000000Z")
    assert dynamo.calls[2:] == [("delete", "AUTHORIZATION"), ("put", history)]
    assert store.get_current(FIRM, ATTORNEY) is None
    kept = authorization_from_item(from_attributes(dynamo.items[(pk, history)]))
    assert kept.status == "withdrawn"
    # A second withdrawal of the same signature finds nothing to delete.
    assert not store.withdraw(signed, withdrawn_at="2100-01-01T00:06:00.000000Z")


def test_the_dynamodb_store_refuses_a_lost_race() -> None:
    dynamo = FakeDynamo()
    store = DynamoDbFilingAuthorizationStore("t", client=dynamo)
    first = FilingAuthorization(
        authorization_id="a1",
        firm_id=FIRM,
        attorney_id=ATTORNEY,
        text_version=TEXT_VERSION,
        text_digest=text_json()["digest"],
        signed_at="2100-01-01T00:00:00.000000Z",
        authenticated_at=SIGNED_IN_AT,
    )
    store.put_current(first, replacing=None)
    second = FilingAuthorization(**{**first.__dict__, "authorization_id": "a2"})
    # Two signers who both read "nothing current": the second loses.
    with pytest.raises(ConflictError):
        store.put_current(second, replacing=None)
    # Replacing the one actually held succeeds, and supersedes its history.
    store.put_current(second, replacing=first)
    superseded = dynamo.items[(f"ATTORNEY#{FIRM}#{ATTORNEY}", "AUTHORIZATION#a1")]
    assert superseded["status"] == {"S": "superseded"}
