"""The JWKS adapters (issue #79 / 7.4).

The HTTP one never actually opens a socket here: `_fetch` is monkeypatched,
mirroring tests/test_mailer_client.py's approach to the same problem. What is
under test is the caching and fail-closed policy around the fetch, which is
where the security-relevant decisions live.
"""

from __future__ import annotations

import json

import pytest
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider, parse_jwks
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.auth import AuthenticationError, AuthFailureReason

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"


# ── memory adapter ───────────────────────────────────────────────────


def test_static_provider_returns_a_known_key():
    provider = StaticJwksProvider({"kid-1": "key-1"})

    assert provider.signing_key("kid-1") == "key-1"


def test_static_provider_raises_unknown_key():
    with pytest.raises(AuthenticationError) as caught:
        StaticJwksProvider({"kid-1": "key-1"}).signing_key("kid-2")

    assert caught.value.reason is AuthFailureReason.UNKNOWN_KEY


# ── the HTTP adapter's URL and caching policy ────────────────────────


def test_builds_the_well_known_jwks_url():
    assert CognitoJwksProvider(ISSUER + "/").jwks_url == (
        ISSUER + "/.well-known/jwks.json"
    )


def fake_provider(monkeypatch, documents, **kwargs):
    """A provider whose `_fetch` yields `documents` in order, counting calls."""
    provider = CognitoJwksProvider(ISSUER, **kwargs)
    calls: list[int] = []

    def _fetch(self=provider):
        calls.append(1)
        result = documents[min(len(calls) - 1, len(documents) - 1)]
        if isinstance(result, Exception):
            raise result
        return result

    monkeypatch.setattr(provider, "_fetch", _fetch)
    return provider, calls


def test_fetches_once_and_serves_from_cache(monkeypatch):
    provider, calls = fake_provider(monkeypatch, [{"kid-1": "key-1"}])

    assert provider.signing_key("kid-1") == "key-1"
    assert provider.signing_key("kid-1") == "key-1"
    assert len(calls) == 1


def test_refetches_once_for_an_unknown_kid_then_serves_the_rotated_key(monkeypatch):
    provider, calls = fake_provider(
        monkeypatch,
        [{"kid-1": "key-1"}, {"kid-1": "key-1", "kid-2": "key-2"}],
        min_refresh_seconds=0.0,
    )

    assert provider.signing_key("kid-1") == "key-1"
    assert provider.signing_key("kid-2") == "key-2"
    assert len(calls) == 2


def test_an_unknown_kid_raises_rather_than_returning_nothing(monkeypatch):
    provider, _ = fake_provider(
        monkeypatch, [{"kid-1": "key-1"}], min_refresh_seconds=0.0
    )
    provider.signing_key("kid-1")

    with pytest.raises(AuthenticationError) as caught:
        provider.signing_key("kid-absent")

    assert caught.value.reason is AuthFailureReason.UNKNOWN_KEY


def test_unknown_kids_do_not_refetch_faster_than_the_floor(monkeypatch):
    # A forged token carries an unknown kid; without this floor an anonymous
    # caller could make the service hammer Cognito.
    provider, calls = fake_provider(
        monkeypatch, [{"kid-1": "key-1"}], min_refresh_seconds=3600.0
    )
    provider.signing_key("kid-1")

    for _ in range(5):
        with pytest.raises(AuthenticationError):
            provider.signing_key("forged-kid")

    assert len(calls) == 1


def test_a_failed_refresh_keeps_serving_the_cached_keys(monkeypatch):
    provider, _ = fake_provider(
        monkeypatch,
        [{"kid-1": "key-1"}, RuntimeError("cognito is having a day")],
        min_refresh_seconds=0.0,
    )
    provider.signing_key("kid-1")

    with pytest.raises(AuthenticationError):
        provider.signing_key("kid-2")
    # The outage must not have logged everyone out.
    assert provider.signing_key("kid-1") == "key-1"


def test_a_cold_cache_that_cannot_be_filled_fails_closed(monkeypatch):
    provider, _ = fake_provider(monkeypatch, [RuntimeError("no network")])

    with pytest.raises(AuthenticationError) as caught:
        provider.signing_key("kid-1")

    assert caught.value.reason is AuthFailureReason.UNKNOWN_KEY


def test_a_stale_cache_is_refreshed_on_the_happy_path(monkeypatch):
    provider, calls = fake_provider(
        monkeypatch,
        [{"kid-1": "old"}, {"kid-1": "new"}],
        ttl_seconds=0.0,
    )

    assert provider.signing_key("kid-1") == "old"
    assert provider.signing_key("kid-1") == "new"
    assert len(calls) == 2


# ── parsing a real-shaped JWKS document ──────────────────────────────

# An RSA public key in JWK form. Modulus and exponent are a throwaway keypair
# generated for this test file — obviously not any Cognito pool's.
VALID_JWK = {
    "kty": "RSA",
    "kid": "test-kid",
    "use": "sig",
    "alg": "RS256",
    "n": (
        "sXchd_Cy8oGqzGT3JTuLQBQyMoJnSbCe0nUX6WFj5mAvJnbaBenxD5UMcxNKZKPQ"
        "1UzGnAcOKlPjTgYnW-BeVdgFhcuLBOs_h3l6QYIYAqAe4mdMBiTF9pMxGB8Cc1RE"
        "3nz_Lj0hjLjIJ8vXwqHOmXGCEQGRQAtsFmlLBGKlSbW-8LoQocDXMr0N-y8-cA4x"
        "RySRSAHNVIVCJUCXCcMPuF2xhJZBk8mFtSl2yEhPQhZaLnAECRQNMxNBhTsq-XiJ"
        "aeb2WlBHUZaZzOgIrRSt-Ug2h13uMdBjNVsUxfMLwoT9DTX26e3AY-6R9uSxrKPO"
        "N5UPnAgjNP0-r-a4bJHRlQ"
    ),
    "e": "AQAB",
}


def test_parse_jwks_keys_by_kid():
    keys = parse_jwks({"keys": [VALID_JWK]})

    assert list(keys) == ["test-kid"]


def test_parse_jwks_skips_unusable_entries_but_keeps_the_good_ones():
    document = {
        "keys": [
            "not-an-object",
            {"kty": "RSA", "n": "abc", "e": "AQAB"},  # no kid
            {"kty": "NONSENSE", "kid": "bad-kid"},
            VALID_JWK,
        ]
    }

    assert list(parse_jwks(document)) == ["test-kid"]


@pytest.mark.parametrize(
    "document",
    ["not-an-object", {"no_keys": []}, {"keys": "nope"}, {"keys": []}],
)
def test_parse_jwks_rejects_a_document_with_no_usable_keys(document):
    with pytest.raises(ValueError, match="JWKS document"):
        parse_jwks(document)


def test_parse_jwks_accepts_a_serialized_document():
    # The shape `_fetch` actually hands it, round-tripped through JSON.
    keys = parse_jwks(json.loads(json.dumps({"keys": [VALID_JWK]})))

    assert list(keys) == ["test-kid"]
