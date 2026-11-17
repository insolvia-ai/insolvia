"""Shared fixtures: the app over memory adapters, and real staff tokens.

Tokens are SIGNED HERE with a test RSA keypair and verified through the same
code path production runs (StaticJwksProvider serving the public key) — same
approach as the API's auth tests. `staff_token` mints a valid Workspace ID
token; the cross-issuer test builds its own firm-pool-shaped one.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import time
from typing import Any

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_admin.adapters.memory.audit_log import MemoryAuditLog
from insolvia_admin.api.app_factory import create_app
from insolvia_admin.api.dependencies import AdminDependencies
from insolvia_admin.core.config import AppConfig
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory

CLIENT_ID = "000000000000-fake.apps.googleusercontent.com"
WORKSPACE = "example-workspace.test"
STAFF_SUB = "100000000000000000001"
STAFF_EMAIL = "operator@example-workspace.test"
KID = "test-key"

_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC = _KEY.public_key()


@pytest.fixture
def firm_store() -> MemoryFirmStore:
    return MemoryFirmStore()


@pytest.fixture
def user_directory() -> MemoryUserDirectory:
    return MemoryUserDirectory()


@pytest.fixture
def audit_log() -> MemoryAuditLog:
    return MemoryAuditLog()


@pytest.fixture
def app(firm_store, user_directory, audit_log):
    config = AppConfig(
        environment="local",
        google_client_id=CLIENT_ID,
        workspace_domain=WORKSPACE,
    )
    return create_app(
        AdminDependencies(
            config=config,
            jwks_provider=StaticJwksProvider({KID: _PUBLIC}),
            firm_store=firm_store,
            user_directory=user_directory,
            audit_log=audit_log,
        )
    )


@pytest.fixture
def client(app):
    return app.test_client()


def sign(claims: dict[str, Any]) -> str:
    return jwt.encode(claims, _KEY, algorithm="RS256", headers={"kid": KID})


def staff_token(**overrides: Any) -> str:
    now = int(time.time())
    claims: dict[str, Any] = {
        "iss": "https://accounts.google.com",
        "aud": CLIENT_ID,
        "sub": STAFF_SUB,
        "hd": WORKSPACE,
        "email": STAFF_EMAIL,
        "email_verified": True,
        "iat": now,
        "exp": now + 3600,
    }
    claims.update(overrides)
    return sign(claims)


@pytest.fixture
def staff_headers() -> dict[str, str]:
    return {"Authorization": f"Bearer {staff_token()}"}
