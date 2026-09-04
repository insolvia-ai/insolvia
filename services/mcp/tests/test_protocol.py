"""The MCP protocol seam, pinned the way the api-client's contract test pins
the REST surface: real JSON-RPC POSTs (revision 2026-07-28 shape — _meta
protocol version, MCP-Protocol-Version / Mcp-Method / Mcp-Name headers)
against the real ASGI app, with real RS256 tokens.

What is pinned here is the WIRE: the 401 challenge with its
resource_metadata pointer, the static eight-tool listing with annotations
and output schemas, structuredContent on success, the {error: {code, ...}}
envelope on domain refusal, and the header-mismatch rejection. If the SDK or
our registration changes any of it, a harness would see the difference — so
a test should too.
"""

from __future__ import annotations

import pytest
from insolvia_core.cases import create_case, parse_case_creation
from insolvia_mcp.api.server import create_asgi_app
from starlette.testclient import TestClient

from .conftest import FIRM_ID, SUBJECT, make_token

PROTOCOL_VERSION = "2026-07-28"

META = {
    "io.modelcontextprotocol/protocolVersion": PROTOCOL_VERSION,
    "io.modelcontextprotocol/clientInfo": {"name": "insolvia-tests", "version": "0"},
    "io.modelcontextprotocol/clientCapabilities": {},
}

EXPECTED_TOOLS = (
    "whoami",
    "list_cases",
    "get_case",
    "list_case_records",
    "get_case_record",
    "propose_case_records",
    "check_proposals",
    "withdraw_proposal",
)

READ_ONLY_TOOLS = (
    "whoami",
    "list_cases",
    "get_case",
    "list_case_records",
    "get_case_record",
    "check_proposals",
)


@pytest.fixture
def client(deps):
    app = create_asgi_app(deps)
    # The dev-server host, which local transport security allows; the
    # TestClient default (testserver) is deliberately NOT allowed — that
    # refusal is itself asserted below.
    with TestClient(app, base_url="http://127.0.0.1:8788") as test_client:
        yield test_client


def _rpc(client, method, params=None, *, headers=None, rpc_id=1):
    body = {
        "jsonrpc": "2.0",
        "id": rpc_id,
        "method": method,
        "params": {**(params or {}), "_meta": META},
    }
    request_headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": PROTOCOL_VERSION,
        "Mcp-Method": method,
    }
    if headers:
        request_headers.update(headers)
    return client.post("/mcp", json=body, headers=request_headers)


def _call(client, tool, arguments, *, headers=None, name_header=None):
    return _rpc(
        client,
        "tools/call",
        {"name": tool, "arguments": arguments},
        headers={"Mcp-Name": name_header or tool, **(headers or {})},
    )


def _auth():
    return {"Authorization": f"Bearer {make_token()}"}


def _seed_case(deps):
    case, assignment = create_case(
        parse_case_creation({"chapter": 7, "district": "Middle District of Florida"}),
        firm_id=FIRM_ID,
        created_by=SUBJECT,
    )
    deps.case_store.create(case, assignment)
    return case


# ── transport auth ──────────────────────────────────────────────────


def test_an_unauthenticated_request_is_challenged(client) -> None:
    response = _rpc(client, "tools/list")
    assert response.status_code == 401
    challenge = response.headers["WWW-Authenticate"]
    assert challenge.startswith("Bearer ")
    assert "resource_metadata=" in challenge


def test_protected_resource_metadata_names_the_authorization_server(client) -> None:
    response = client.get("/.well-known/oauth-protected-resource/mcp")
    assert response.status_code == 200
    metadata = response.json()
    assert metadata["resource"] == "http://127.0.0.1:8788/mcp"
    assert metadata["authorization_servers"] == [
        "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
    ]


def test_a_wrong_host_header_is_rejected(deps) -> None:
    # The DNS-rebinding MUST: a deployed environment answers exactly its own
    # hostname.
    app = create_asgi_app(deps)
    with TestClient(app, base_url="http://testserver") as client:
        response = _rpc(client, "tools/list", headers=_auth())
    assert response.status_code == 421


def test_an_expired_token_is_a_401(client) -> None:
    response = _rpc(
        client,
        "tools/list",
        headers={"Authorization": f"Bearer {make_token(expires_in=-60)}"},
    )
    assert response.status_code == 401


# ── the tool listing ────────────────────────────────────────────────


def test_the_listing_is_the_static_eight(client) -> None:
    response = _rpc(client, "tools/list", headers=_auth())
    assert response.status_code == 200
    tools = response.json()["result"]["tools"]
    assert tuple(tool["name"] for tool in tools) == EXPECTED_TOOLS
    by_name = {tool["name"]: tool for tool in tools}
    for name in EXPECTED_TOOLS:
        assert by_name[name]["outputSchema"], name
    for name in READ_ONLY_TOOLS:
        assert by_name[name]["annotations"]["readOnlyHint"] is True, name
    for name in set(EXPECTED_TOOLS) - set(READ_ONLY_TOOLS):
        assert by_name[name]["annotations"]["readOnlyHint"] is False, name


def test_the_listing_is_the_same_for_a_permission_stripped_caller(
    client, firm_store
) -> None:
    # List-visible, call-denied: permissions are revocable mid-hour, so a
    # filtered list would be stale the moment it mattered.
    from .conftest import make_user

    user = firm_store.get_user(FIRM_ID, SUBJECT)
    assert user is not None
    firm_store.update_user(
        make_user(is_admin=False, access_all_cases=False, permissions={})
    )
    response = _rpc(client, "tools/list", headers=_auth())
    tools = response.json()["result"]["tools"]
    assert tuple(tool["name"] for tool in tools) == EXPECTED_TOOLS


# ── tool calls ──────────────────────────────────────────────────────


def test_whoami_answers_structured_content(client) -> None:
    response = _call(client, "whoami", {}, headers=_auth())
    result = response.json()["result"]
    assert result.get("isError") is not True
    structured = result["structuredContent"]
    assert structured["firm"] == {"id": FIRM_ID, "name": "Example Firm"}
    # The backwards-compatibility text block carries the same JSON.
    assert result["content"][0]["type"] == "text"
    assert '"displayName"' in result["content"][0]["text"]


def test_the_candidate_flow_end_to_end(client, deps) -> None:
    case = _seed_case(deps)
    proposed = _call(
        client,
        "propose_case_records",
        {
            "caseId": case.id,
            "proposals": [
                {"entityType": "creditors", "payload": {"name": "Example Bank"}}
            ],
        },
        headers=_auth(),
    )
    structured = proposed.json()["result"]["structuredContent"]
    (candidate,) = structured["candidates"]
    assert candidate["status"] == "pending"

    checked = _call(client, "check_proposals", {"caseId": case.id}, headers=_auth())
    assert (
        checked.json()["result"]["structuredContent"]["candidates"][0]["candidateId"]
        == candidate["candidateId"]
    )

    withdrawn = _call(
        client,
        "withdraw_proposal",
        {"caseId": case.id, "candidateId": candidate["candidateId"]},
        headers=_auth(),
    )
    assert withdrawn.json()["result"]["structuredContent"]["status"] == "withdrawn"


def test_a_domain_refusal_carries_the_error_envelope(client) -> None:
    response = _call(client, "get_case", {"caseId": "no-such-case"}, headers=_auth())
    result = response.json()["result"]
    assert result["isError"] is True
    error = result["structuredContent"]["error"]
    assert error["code"] == "not_found"
    assert error["message"]


def test_a_permission_refusal_is_permission_denied(client, firm_store) -> None:
    from .conftest import make_user

    firm_store.update_user(
        make_user(is_admin=False, access_all_cases=False, permissions={})
    )
    response = _call(client, "list_cases", {}, headers=_auth())
    error = response.json()["result"]["structuredContent"]["error"]
    assert error["code"] == "permission_denied"


def test_a_validation_refusal_names_fields(client, deps) -> None:
    case = _seed_case(deps)
    response = _call(
        client,
        "propose_case_records",
        {
            "caseId": case.id,
            "proposals": [{"entityType": "creditors", "payload": {"name": 42}}],
        },
        headers=_auth(),
    )
    error = response.json()["result"]["structuredContent"]["error"]
    assert error["code"] == "validation_failed"
    assert "proposals[0].payload.name" in error["fields"]


def test_an_unknown_tool_is_a_protocol_error(client) -> None:
    response = _call(client, "no_such_tool", {}, headers=_auth())
    body = response.json()
    # The call was malformed at the protocol layer, not refused by the
    # domain: JSON-RPC error, not a tool result.
    assert "error" in body or body["result"].get("isError") is True


def test_a_header_body_mismatch_is_rejected(client) -> None:
    response = _call(client, "whoami", {}, headers=_auth(), name_header="somebody_else")
    assert response.status_code == 400
    assert response.json()["error"]["code"] == -32020
