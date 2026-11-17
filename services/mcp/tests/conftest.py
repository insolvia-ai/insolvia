"""Shared fixtures: a real RSA keypair, Cognito-shaped tokens, and a composed
in-memory surface. Every token is signed for real (the API's test_auth.py
pattern) — no mock verifier, no patched decode — while touching no network.

Every id below is obviously fake. This repo is public; no real pool id, app
client id, or subject ever appears here.
"""

from __future__ import annotations

import time
from dataclasses import replace

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_core.access import Accessor
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.cases import Case
from insolvia_core.firm_clients import (
    FirmClient,
    create_firm_client,
    debtor_from_client,
    parse_firm_client,
)
from insolvia_core.firms import Firm, FirmUser
from insolvia_core.ports import DebtorStore, FirmStore
from insolvia_mcp.api.dependencies import McpDependencies
from insolvia_mcp.core.config import load_config
from insolvia_mcp.core.tools import CaseTools

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
# Two allowlisted MCP clients — the surface's shape is one pre-registered
# client per harness — plus the app's, which must never verify here.
CLIENT_ID = "examplemcpclaudeclient00"
SECOND_CLIENT_ID = "examplemcpinspector00000"
OTHER_CLIENT_ID = "exampleappclientid000000"
SUBJECT = "00000000-0000-4000-8000-000000000001"
COLLEAGUE = "00000000-0000-4000-8000-000000000002"
FIRM_ID = "00000000-0000-4000-8000-00000000f1a1"
OTHER_FIRM_ID = "00000000-0000-4000-8000-00000000f1a2"
KID = "test-key-1"
# A tax-id POINTER, as #382's sealed item would be addressed — fake, and
# asserted never to appear on this surface's wire.
TAX_ID_REF = "taxid-ref-example-0000000000000001"

# One keypair for the whole suite: generation is the slow part.
PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
PUBLIC_KEY = PRIVATE_KEY.public_key()


def make_token(
    *,
    issuer: str = ISSUER,
    client_id: str = CLIENT_ID,
    token_use: str = "access",
    subject: str | None = SUBJECT,
    expires_in: int = 3600,
    kid: str = KID,
    key: object = None,
) -> str:
    """Mint a token shaped exactly like a Cognito access token."""
    now = int(time.time())
    claims: dict[str, object] = {
        "iss": issuer,
        "client_id": client_id,
        "token_use": token_use,
        "iat": now,
        "exp": now + expires_in,
        "scope": "insolvia/mcp",
    }
    if subject is not None:
        claims["sub"] = subject
    return jwt.encode(
        claims,
        key if key is not None else PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": kid},
    )


def make_firm(firm_id: str = FIRM_ID, *, status: str = "active") -> Firm:
    return Firm(
        id=firm_id,
        name="Example Firm",
        status=status,
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def make_user(
    subject: str = SUBJECT,
    *,
    firm_id: str = FIRM_ID,
    is_admin: bool = False,
    access_all_cases: bool = False,
    permissions: dict[str, str] | None = None,
    status: str = "active",
) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email="dev@example.com",
        first_name="Dev",
        last_name="User",
        role="attorney",
        is_admin=is_admin,
        access_all_cases=access_all_cases,
        permissions=permissions or {},
        status=status,
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def make_accessor(
    *,
    is_admin: bool = True,
    access_all_cases: bool = False,
    permissions: dict[str, str] | None = None,
    subject: str = SUBJECT,
    firm_id: str = FIRM_ID,
) -> Accessor:
    return Accessor(
        firm=make_firm(firm_id),
        user=make_user(
            subject,
            firm_id=firm_id,
            is_admin=is_admin,
            access_all_cases=access_all_cases,
            permissions=permissions,
        ),
    )


def make_firm_client(
    firm_store: FirmStore,
    *,
    surname: str = "Example",
    firm_id: str = FIRM_ID,
    with_tax_id: bool = True,
) -> FirmClient:
    """A client in `firm_id`'s directory, carrying a tax-id pointer the way
    #382 will set it — so a test can prove the pointer never leaves."""
    client = create_firm_client(
        parse_firm_client({"name": {"given": "Pat", "surname": surname}}),
        firm_id=firm_id,
        created_by=SUBJECT,
    )
    if with_tax_id:
        client = replace(client, tax_id_ref=TAX_ID_REF, tax_id_last_four="6789")
    firm_store.create_client(client)
    return client


def link_client(
    debtor_store: DebtorStore, client: FirmClient, case: Case, role: str
) -> None:
    """Copy `client` onto `case` as `role` — the debtor POST /v1/cases
    writes, `client_id` and all."""
    assert debtor_store.create(debtor_from_client(client, case=case, filing_role=role))


@pytest.fixture
def stores() -> dict[str, object]:
    return {
        "case_store": MemoryCaseStore(),
        "case_entity_store": MemoryCaseEntityStore(),
        "debtor_store": MemoryDebtorStore(),
        "document_store": MemoryDocumentStore(),
        "candidate_store": MemoryCandidateStore(),
        "access_log": MemoryAccessLog(),
    }


@pytest.fixture
def tools(stores, firm_store) -> CaseTools:
    return CaseTools(firm_store=firm_store, **stores)


@pytest.fixture
def firm_store() -> MemoryFirmStore:
    store = MemoryFirmStore()
    store.create_firm(make_firm())
    store.add_user(make_user(is_admin=True))
    return store


@pytest.fixture
def deps(stores, firm_store) -> McpDependencies:
    return McpDependencies(
        config=load_config(
            {
                "AUTH_ISSUER_URL": ISSUER,
                "AUTH_CLIENT_IDS": f"{CLIENT_ID},{SECOND_CLIENT_ID}",
            }
        ),
        firm_store=firm_store,
        jwks_provider=StaticJwksProvider({KID: PUBLIC_KEY}),
        **stores,
    )
