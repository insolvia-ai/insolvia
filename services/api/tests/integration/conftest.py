"""The integration harness: a RUNNING API over HTTP, signed in for real.

**Opt-in, and silent otherwise.** Without `INSOLVIA_INTEGRATION=1` every test
in this tree is skipped at collection — a bare `pytest` here is the unit
tier (`testpaths` in pyproject.toml already excludes this directory; the gate
is belt and braces for `pytest tests`). With it, a missing input fails by
NAME before a single request is made.

## Two targets, one suite

    dev       this machine's API from `scripts/dev-up.sh` on :8080, its pool
              and its tables — `services/api/scripts/dev-test-integration.sh`
    staging   https://staging-api.insolvia.ai, after the alias shift, from
              `api-staging.yml`

The same specs run against both, and the fixture they sign in with is the one
that environment was seeded from — `seeds/dev.json` or `seeds/staging.json`,
picked by `INTEGRATION_TARGET`. ONE FILE DECIDES WHO EXISTS, exactly as the
browser suite's `e2e/support/env.ts` reads it: the seeder and this suite
cannot drift onto two different people.

## What it proves that nothing else can

The unit tier runs the Flask app in-process over memory adapters. It cannot
tell whether DynamoDB accepts the item shapes, whether the bucket policy
accepts the presigned PUT the API mints, whether the Lambda's own execution
role may make the calls the code makes, or whether a real access token from
the real pool verifies against the deployed configuration. Every request
below crosses all of those at once — and against staging it crosses API
Gateway, Mangum and the role too, which is the one combination a laptop can
never reproduce (ADR 0008's "AWS adapters remain the least-covered code by
design" is the gap this tier closes).

## Credentials

`E2E_TEST_USER_PASSWORD` — the ONE shared test-user password, the same secret
the browser suite reads — has no default and never will; this repo is
public. Nothing here interpolates it into a test id, a message or a URL. The
pool id and client id are public values (they ride in every sign-in redirect)
and still have no defaults, because a value the suite guessed would be a run
against an environment nobody chose.

**No AWS credentials are needed or used.** Sign-in is SRP over unsigned
Cognito calls (`cognito_srp.py`) and everything after that is HTTPS to the
API — which is what makes the API's calls run under ITS role, not yours.

## Scratch discipline

Cases cannot be deleted through the API, so a spec that opened one per run
would fill a table nobody prunes. The suite keeps ONE scratch case per
environment, found by district (`SCRATCH_DISTRICT`) and opened only when
absent. Everything else it creates — debtors are replaced in place, documents
are deleted — lives inside that case, and every deletion runs in teardown
however the test ended.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

import pytest

from tests import paths
from tests.integration.cognito_srp import SignInError, sign_in

HERE = Path(__file__).resolve().parent

GATE = "INSOLVIA_INTEGRATION"
TARGETS = ("dev", "staging")

#: The district the suite's scratch case carries — a value no real case would,
#: which is what lets a later run find it again rather than opening another.
SCRATCH_DISTRICT = "INTEGRATION-SCRATCH"

#: What `/health` reports per target. The local compose stack runs the API as
#: `local`; there is no `dev` environment name on the API side.
EXPECTED_ENVIRONMENT = {"dev": "local", "staging": "staging"}


def pytest_collection_modifyitems(
    config: pytest.Config, items: list[pytest.Item]
) -> None:
    """Skip this tree unless it was asked for, by name.

    FILTERED TO THIS DIRECTORY, and that is not cosmetic: pytest hands this
    hook every collected item regardless of which conftest defines it, so an
    unfiltered version would skip the unit tier too and report a green run
    that executed nothing.
    """
    if os.environ.get(GATE) == "1":
        return
    skip = pytest.mark.skip(
        reason=f"integration tier: set {GATE}=1 and aim it at a running API "
        "(services/api/scripts/dev-test-integration.sh)"
    )
    for item in items:
        if HERE in Path(str(item.fspath)).resolve().parents:
            item.add_marker(skip)


def _required(name: str) -> str:
    """A variable this suite cannot invent, or a failure naming ONLY the variable."""
    value = os.environ.get(name, "").strip()
    if not value:
        pytest.fail(
            f"{name} is not set. The integration tier takes its target and its "
            "credentials from the environment only — see tests/integration/"
            "conftest.py. On CI an environment-scoped secret resolves to an "
            "empty string in silence when a job is missing its `environment:` "
            "key, so check that first."
        )
    return value


# ── the target ──────────────────────────────────────────────────


@pytest.fixture(scope="session")
def target() -> str:
    value = _required("INTEGRATION_TARGET")
    if value not in TARGETS:
        pytest.fail(f"INTEGRATION_TARGET must be one of {TARGETS}, not {value!r}")
    return value


@pytest.fixture(scope="session")
def base_url() -> str:
    """The API origin under test, with no trailing slash."""
    return _required("INTEGRATION_API_URL").rstrip("/")


@pytest.fixture(scope="session")
def fixture(target: str) -> dict[str, Any]:
    """`seeds/<target>.json` — the file that says who exists in this target."""
    path = paths.REPO_ROOT / "seeds" / f"{target}.json"
    try:
        return dict(json.loads(path.read_text()))
    except OSError as error:
        pytest.fail(f"could not read the seed fixture at {path}: {error}")


def seeded_users(fixture: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """Every fixture person by `handle` — the same lookup e2e/support/env.ts does."""
    found: dict[str, dict[str, Any]] = {}
    for firm in fixture.get("firms") or []:
        for user in firm.get("users") or []:
            if user.get("handle") and user.get("email"):
                found[str(user["handle"])] = {**user, "firmName": firm.get("name")}
    return found


# ── the signed-in callers ───────────────────────────────────────


class Api:
    """The API as one signed-in caller sees it.

    `urllib` rather than a client library: the tier's dependency surface is
    the thing it is trying not to have, and the API is JSON over six verbs.
    """

    def __init__(self, base_url: str, token: str | None) -> None:
        self._base = base_url
        self._token = token

    def request(
        self,
        method: str,
        path: str,
        *,
        params: dict[str, str] | None = None,
        body: Any = None,
        expect: int | tuple[int, ...] = 200,
    ) -> Any:
        url = f"{self._base}{path}"
        if params:
            url = f"{url}?{urllib.parse.urlencode(params)}"
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(url, data=data, method=method)
        if self._token:
            request.add_header("Authorization", f"Bearer {self._token}")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                status, payload = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, payload = error.code, error.read()
        # A body that is not JSON is a body the application did not write —
        # API Gateway's own `Unauthorized`, or a CloudFront error page. Keep
        # the STATUS readable rather than replacing a useful 403 with a
        # JSONDecodeError three frames down.
        try:
            parsed = json.loads(payload) if payload else None
        except ValueError:
            parsed = payload.decode(errors="replace")[:500]
        expected = expect if isinstance(expect, tuple) else (expect,)
        if status not in expected:
            pytest.fail(
                f"{method} {path} returned {status}, expected {expected}: {parsed}"
            )
        return parsed

    def get(self, path: str, expect: int | tuple[int, ...] = 200, **params: str) -> Any:
        return self.request("GET", path, params=params or None, expect=expect)

    def post(
        self, path: str, body: Any = None, expect: int | tuple[int, ...] = 201
    ) -> Any:
        return self.request(
            "POST", path, body={} if body is None else body, expect=expect
        )

    def put(
        self, path: str, body: Any = None, expect: int | tuple[int, ...] = 200
    ) -> Any:
        return self.request(
            "PUT", path, body={} if body is None else body, expect=expect
        )

    def patch(self, path: str, body: Any, expect: int | tuple[int, ...] = 200) -> Any:
        return self.request("PATCH", path, body=body, expect=expect)

    def delete(self, path: str, expect: int | tuple[int, ...] = 204) -> Any:
        return self.request("DELETE", path, expect=expect)


@pytest.fixture(scope="session")
def tokens(fixture: dict[str, Any]) -> dict[str, str]:
    """One real access token per fixture person, minted once for the session.

    Session-scoped because SRP is several round trips and the token is valid
    for an hour; minting one per test would add minutes and prove nothing a
    single mint does not.
    """
    pool_id = _required("INTEGRATION_AUTH_POOL_ID")
    client_id = _required("INTEGRATION_AUTH_CLIENT_ID")
    password = _required("E2E_TEST_USER_PASSWORD")
    minted: dict[str, str] = {}
    for handle, person in seeded_users(fixture).items():
        try:
            result = sign_in(
                pool_id=pool_id,
                client_id=client_id,
                email=str(person["email"]),
                password=password,
            )
        except SignInError as refusal:
            pytest.fail(f"could not sign in as '{handle}': {refusal}")
        minted[handle] = result["AccessToken"]
    if not minted:
        pytest.fail("the seed fixture names nobody with a handle and an email")
    return minted


@pytest.fixture(scope="session")
def people(fixture: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return seeded_users(fixture)


@pytest.fixture(scope="session")
def as_user(base_url: str, tokens: dict[str, str]):
    """`as_user("admin")` — the API, signed in as that fixture person."""

    def _as(handle: str) -> Api:
        if handle not in tokens:
            pytest.fail(
                f"no fixture person with handle '{handle}'; known: "
                + ", ".join(sorted(tokens))
            )
        return Api(base_url, tokens[handle])

    return _as


@pytest.fixture(scope="session")
def anonymous(base_url: str) -> Api:
    """The API with no token at all — for the routes that must refuse."""
    return Api(base_url, None)


@pytest.fixture(scope="session")
def admin(as_user) -> Api:
    """The first firm's administrator: the caller most specs act as."""
    return as_user("admin")


# ── the scratch case ────────────────────────────────────────────


@pytest.fixture(scope="session")
def scratch_case(admin: Api) -> dict[str, Any]:
    """ONE case per environment for the suite to work in, opened on demand.

    Found by its district on later runs rather than re-opened: cases have no
    delete route, and a fresh row per run is a table nobody prunes.
    """
    cursor: str | None = None
    while True:
        params: dict[str, str] = {"limit": "50"}
        if cursor:
            params["cursor"] = cursor
        page = admin.get("/v1/cases", **params)
        for case in page.get("cases") or []:
            if case.get("district") == SCRATCH_DISTRICT:
                return dict(case)
        cursor = page.get("nextCursor")
        if not cursor:
            break
    return dict(admin.post("/v1/cases", {"chapter": 7, "district": SCRATCH_DISTRICT}))
