"""The Lambda handler, invoked more than once in one process.

Lambda keeps a container warm and calls the handler again for the next
request, and Mangum runs the ASGI lifespan — startup AND shutdown — around
every one of those invocations. The MCP SDK's StreamableHTTPSessionManager
refuses a second run() on one instance, so a server whose lifespan runs a
manager created once at import serves exactly one request per container and
500s every request after it. The staging smoke test was the first thing to
hit a warm container, and it was what found this.

What is pinned here is therefore the WARM path: the real handler
entrypoints/mcp_lambda.py exports, built from the environment the deploy
workflow derives, invoked twice in one process with an API Gateway payload
2.0 event (the shape infra/modules/mcp_service wires). The metadata document
must answer 200 both times, and an unauthenticated tools/list must still be
the 401 challenge the smoke test asserts — proving the fix moved the manager's
lifecycle, not the auth posture.

Two things this test deliberately reaches that the rest of the suite does
not. It imports an entrypoint, which composes from os.environ at import —
the one place this package reads the environment outside load_config's
mapping seam — so the fixture sets that environment and imports the module
fresh, the mailer's test_development_api.py precedent. And it constructs the
real DynamoDB adapters, which build a boto3 client at construction: the
fixture pins a region and obviously-fake credentials so client creation
touches nothing, and neither request below ever reaches a store, the JWKS
endpoint, or the network.
"""

from __future__ import annotations

import asyncio
import importlib
import json
import sys

import pytest

from tests.conftest import CLIENT_ID, ISSUER

HOST = "staging-mcp.insolvia.ai"
METADATA_PATH = "/.well-known/oauth-protected-resource/mcp"
PROTOCOL_VERSION = "2026-07-28"
META = {"io.modelcontextprotocol/protocolVersion": PROTOCOL_VERSION}


@pytest.fixture
def handler(monkeypatch):
    monkeypatch.setenv("INSOLVIA_ENV", "staging")
    monkeypatch.setenv("AUTH_ISSUER_URL", ISSUER)
    monkeypatch.setenv("AUTH_CLIENT_IDS", CLIENT_ID)
    monkeypatch.setenv("CASE_TABLE_NAME", "insolvia-staging-case-table-test")
    monkeypatch.setenv(
        "CASE_ACCESS_LOG_TABLE_NAME", "insolvia-staging-case-access-log-test"
    )
    monkeypatch.setenv("FIRM_TABLE_NAME", "insolvia-staging-firm-table-test")
    # boto3 resolves a region and a credential provider when a client is
    # built. Fake values satisfy both without consulting a profile, an
    # instance-metadata endpoint, or anything else off this machine.
    monkeypatch.setenv("AWS_DEFAULT_REGION", "us-east-1")
    monkeypatch.setenv("AWS_ACCESS_KEY_ID", "testing")
    monkeypatch.setenv("AWS_SECRET_ACCESS_KEY", "testing")
    monkeypatch.setenv("AWS_EC2_METADATA_DISABLED", "true")

    # Mangum binds to the process's event loop at construction; give it one
    # and take it away again so the test leaves no loop behind.
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    sys.modules.pop("insolvia_mcp.entrypoints.mcp_lambda", None)
    module = importlib.import_module("insolvia_mcp.entrypoints.mcp_lambda")
    try:
        yield module.handler
    finally:
        sys.modules.pop("insolvia_mcp.entrypoints.mcp_lambda", None)
        loop.close()
        asyncio.set_event_loop(None)


def _event(method: str, path: str, *, headers=None, body=None):
    """An API Gateway HTTP API (payload 2.0) event, as the custom domain
    forwards it — Host is the environment's own hostname, which transport
    security requires."""
    request_headers = {"host": HOST, **(headers or {})}
    if body is not None:
        request_headers["content-length"] = str(len(body.encode()))
    return {
        "version": "2.0",
        "routeKey": "$default",
        "rawPath": path,
        "rawQueryString": "",
        "headers": request_headers,
        "requestContext": {
            "domainName": HOST,
            "http": {
                "method": method,
                "path": path,
                "protocol": "HTTP/1.1",
                "sourceIp": "127.0.0.1",
                "userAgent": "insolvia-tests",
            },
            "requestId": "test",
            "stage": "$default",
        },
        "body": body,
        "isBase64Encoded": False,
    }


def _tools_list_event():
    return _event(
        "POST",
        "/mcp",
        headers={
            "content-type": "application/json",
            "accept": "application/json, text/event-stream",
            "mcp-protocol-version": PROTOCOL_VERSION,
            "mcp-method": "tools/list",
        },
        body=json.dumps(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/list",
                "params": {"_meta": META},
            }
        ),
    )


def test_a_warm_container_serves_the_second_invocation(handler) -> None:
    first = handler(_event("GET", METADATA_PATH), None)
    assert first["statusCode"] == 200, first

    second = handler(_event("GET", METADATA_PATH), None)
    assert second["statusCode"] == 200, second
    metadata = json.loads(second["body"])
    assert metadata["resource"] == f"https://{HOST}/mcp"
    assert metadata["authorization_servers"] == [ISSUER]


def test_an_unauthenticated_tools_list_is_still_challenged(handler) -> None:
    # Warm the container first: the challenge must hold on the path the
    # smoke test actually exercises, not only on a cold start.
    handler(_event("GET", METADATA_PATH), None)

    response = handler(_tools_list_event(), None)

    assert response["statusCode"] == 401, response
    challenge = response["headers"]["www-authenticate"]
    assert challenge.startswith("Bearer ")
    assert f'resource_metadata="https://{HOST}{METADATA_PATH}"' in challenge
