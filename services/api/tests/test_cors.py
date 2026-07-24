import pytest

from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_api.core.cors import origin_allowed

ACAO = "Access-Control-Allow-Origin"


def client_for(environment):
    app = create_app(
        ApiDependencies(
            config=load_config({"INSOLVIA_ENV": environment}),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
        )
    )
    return app.test_client()


@pytest.mark.parametrize(
    ("environment", "origin"),
    [
        ("production", "https://app.insolvia.ai"),
        ("staging", "https://staging-app.insolvia.ai"),
        ("staging", "http://localhost:5173"),
        ("staging", "http://127.0.0.1:8081"),
        ("local", "http://localhost:8081"),
    ],
)
def test_allowed_origin_is_echoed_exactly(environment, origin):
    response = client_for(environment).get("/health", headers={"Origin": origin})

    assert response.headers[ACAO] == origin
    assert "Origin" in response.headers["Vary"]


@pytest.mark.parametrize(
    ("environment", "origin"),
    [
        # Cross-environment origins never bleed.
        ("production", "https://staging-app.insolvia.ai"),
        ("staging", "https://app.insolvia.ai"),
        # localhost is a dev convenience, not a production origin.
        ("production", "http://localhost:5173"),
        # www is deliberately absent everywhere: the marketing waitlist call
        # is server-to-server (no Origin), so CORS is not in play for it.
        ("production", "https://www.insolvia.ai"),
        ("staging", "https://www.insolvia.ai"),
        ("local", "https://evil.example"),
        # Lookalikes.
        ("production", "https://app.insolvia.ai.evil.example"),
        ("staging", "http://localhost.evil.example"),
    ],
)
def test_disallowed_origin_gets_no_cors_headers(environment, origin):
    response = client_for(environment).get("/health", headers={"Origin": origin})

    assert ACAO not in response.headers


@pytest.mark.parametrize("environment", ["local", "staging", "production"])
def test_no_origin_header_means_no_cors_headers(environment):
    # The desktop app and server-to-server callers (the marketing SSR
    # Lambda) send no Origin. That must produce no CORS headers at all —
    # never a wildcard.
    response = client_for(environment).get("/health")

    assert ACAO not in response.headers


def test_never_a_wildcard():
    for environment in ("local", "staging", "production"):
        response = client_for(environment).get(
            "/health", headers={"Origin": "https://app.insolvia.ai"}
        )
        assert response.headers.get(ACAO) != "*"


def test_preflight_gets_cors_headers():
    response = client_for("production").options(
        "/v1/waitlist",
        headers={
            "Origin": "https://app.insolvia.ai",
            "Access-Control-Request-Method": "POST",
        },
    )

    assert response.headers[ACAO] == "https://app.insolvia.ai"
    assert "POST" in response.headers["Access-Control-Allow-Methods"]


def test_origin_allowed_rejects_garbage_origins():
    config = load_config({})
    assert not origin_allowed(config, "localhost")
    assert not origin_allowed(config, "ftp://localhost")
    assert not origin_allowed(config, "http://localhost/path")
    assert not origin_allowed(config, "null")
    assert not origin_allowed(config, "")
