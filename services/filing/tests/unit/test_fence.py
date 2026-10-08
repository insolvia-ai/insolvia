"""The environment host fence — ADR 0024's "a worker asked to sign in anywhere
else refuses before it decrypts", held at the HTTP layer: a refused host
raises before a socket, a DNS lookup or a connection object exists."""

from __future__ import annotations

import socket

import pytest
from insolvia_filing.adapters.http.fenced_client import FencedHttpClient
from insolvia_filing.core.fence import (
    ALLOWED_HOSTS,
    HostNotAllowedError,
    fence_for,
)
from insolvia_filing.core.ports import HttpRequest


def test_staging_and_production_allow_no_court_host_at_all():
    assert ALLOWED_HOSTS["staging"] == frozenset()
    assert ALLOWED_HOSTS["production"] == frozenset()


def test_local_allows_loopback_by_literal_address_only():
    assert ALLOWED_HOSTS["local"] == frozenset({"127.0.0.1", "::1"})


def test_an_unknown_environment_gets_an_empty_fence():
    assert fence_for("dev-anything").allowed == frozenset()


@pytest.mark.parametrize(
    "url",
    [
        "http://127.0.0.1:8790/login",
        "http://[::1]:8790/login",
    ],
)
def test_the_fake_on_loopback_passes_locally(url):
    assert fence_for("local").permits(url)


@pytest.mark.parametrize(
    ("environment", "url"),
    [
        ("local", "https://ecf.flmb.uscourts.gov/cgi-bin/login.pl"),
        ("local", "https://pacer.login.uscourts.gov/csologin/login.jsf"),
        ("local", "http://localhost:8790/login"),
        ("local", "http://user:pass@127.0.0.1:8790/login"),
        ("local", "ftp://127.0.0.1/"),
        ("local", "file:///etc/passwd"),
        ("local", "//127.0.0.1/login"),
        ("staging", "http://127.0.0.1:8790/login"),
        ("staging", "https://ecf-train.txsb.uscourts.gov/"),
        ("production", "https://ecf.flmb.uscourts.gov/"),
        ("production", "http://127.0.0.1:8790/login"),
    ],
)
def test_everything_else_is_refused(environment, url):
    with pytest.raises(HostNotAllowedError):
        fence_for(environment).check(url)


@pytest.fixture
def no_network(monkeypatch):
    touched: list[str] = []

    def refuse(name):
        def fail(*args, **kwargs):
            touched.append(name)
            raise AssertionError(f"{name} was reached")

        return fail

    for name in ("socket", "create_connection", "getaddrinfo"):
        monkeypatch.setattr(socket, name, refuse(name))
    return touched


@pytest.mark.parametrize("environment", ["local", "staging", "production"])
def test_the_client_refuses_a_court_host_before_any_socket(no_network, environment):
    connected: list[object] = []

    def connect(target, timeout):
        connected.append(target)
        raise AssertionError("a connection was created")

    client = FencedHttpClient(fence_for(environment), timeout=1.0, connect=connect)

    with pytest.raises(HostNotAllowedError):
        client.send(
            HttpRequest("GET", "https://ecf.txwb.uscourts.gov/cgi-bin/login.pl")
        )

    assert connected == []
    assert no_network == []


def test_a_refusal_names_the_host_and_nothing_of_the_request():
    with pytest.raises(HostNotAllowedError) as refused:
        fence_for("production").check(
            "https://ecf.flmb.uscourts.gov/login?user=FAKE&password=FAKE-SECRET"
        )

    assert "FAKE-SECRET" not in str(refused.value)
    assert refused.value.host == "ecf.flmb.uscourts.gov"


def test_no_module_but_the_fenced_client_can_open_a_connection():
    """The fence is a choke point only if nothing goes around it: no other
    module in the worker imports a socket, an HTTP client or a URL opener."""
    import ast
    from pathlib import Path

    package = Path(__file__).resolve().parents[2] / "src" / "insolvia_filing"
    network = {
        "socket",
        "ssl",
        "http.client",
        "urllib.request",
        "urllib3",
        "requests",
        "httpx",
        "aiohttp",
    }
    offenders = []
    for path in sorted(package.rglob("*.py")):
        if path.name == "fenced_client.py":
            continue
        tree = ast.parse(path.read_text())
        for node in ast.walk(tree):
            names: list[str] = []
            if isinstance(node, ast.Import):
                names = [alias.name for alias in node.names]
            elif isinstance(node, ast.ImportFrom) and node.module:
                names = [node.module]
            for name in names:
                if any(name == n or name.startswith(f"{n}.") for n in network):
                    offenders.append(f"{path.relative_to(package)}: {name}")
    assert offenders == []
