"""The filing worker's test suite. START HERE.

    tests/
      conftest.py   this map, the paths, and the `filing` world fixture
      unit/         pytest over core/, the adapters' transports, and the
                    whole worker against the fake CM/ECF on 127.0.0.1

There is one tier, `unit/`, and it touches no AWS and no network beyond
loopback: the fake court runs in-process on 127.0.0.1 — the only host the
local fence allows — and every store is a memory adapter. The flow against
this machine's REAL dev queue, vault and tables is
scripts/dev-filing-proof.sh, which is a developer's proof, not a CI tier
(ADR 0021: nothing in a PR check reaches AWS).

THE CASE IS THE API'S. The approval-ready reference case, its real packet
assembly and the approval world come from services/api's own unit tier
(tests/unit/test_filing_approval.py's `make_world`) — imported, not copied,
so the case this worker files is the one the API's digest tests pin. That is
why services/api is on sys.path below and why this suite runs with
--import-mode=importlib and has no `tests` package of its own.

Every identifier is fake; the fake court's password and TOTP seed are minted
from `secrets` per test; "now" is the API world's fixed far-future instant.
"""

from __future__ import annotations

import importlib
import json
import sys
import types
from collections.abc import Callable, Iterator
from dataclasses import dataclass, field
from pathlib import Path

import pytest

#: services/filing
UNIT_DIR = Path(__file__).resolve().parents[1]
REPO_ROOT = UNIT_DIR.parents[1]
PACKAGE = UNIT_DIR / "src" / "insolvia_filing"
FAKE_PACKAGE = UNIT_DIR / "fake" / "fake_cmecf"
API_DIR = REPO_ROOT / "services" / "api"

if str(API_DIR) not in sys.path:
    sys.path.insert(0, str(API_DIR))

from fake_cmecf import FakeCmEcf  # noqa: E402
from insolvia_api.core.filing_approval import FilingApproval  # noqa: E402
from insolvia_core.adapters.memory.document_store import (  # noqa: E402
    MemoryDocumentStore,
)
from insolvia_core.adapters.memory.filing_credentials import (  # noqa: E402
    LocalCredentialOpener,
    LocalCredentialSealer,
)
from insolvia_core.adapters.memory.filing_store import MemoryFilingStore  # noqa: E402
from insolvia_core.filing_credentials import (  # noqa: E402
    enrol_credential,
    parse_enrolment,
)
from insolvia_core.filings import Filing, Step  # noqa: E402
from insolvia_filing.adapters.http.fenced_client import FencedHttpClient  # noqa: E402
from insolvia_filing.adapters.memory.kill_switch import StaticKillSwitch  # noqa: E402
from insolvia_filing.core.drivers.base import CourtDriver  # noqa: E402
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver  # noqa: E402
from insolvia_filing.core.fence import fence_for  # noqa: E402
from insolvia_filing.core.worker import (  # noqa: E402
    FilingDeps,
    FilingResult,
    run_filing,
)


def _import_api_tests() -> types.ModuleType:
    """services/api's `tests.unit.test_filing_approval`, imported under its
    OWN package name. pytest has registered this suite's directory as
    `tests` in sys.modules, so the API's package is imported with ours set
    aside, then ours is put back; the API's modules keep their bindings."""
    ours = {
        name: sys.modules.pop(name)
        for name in list(sys.modules)
        if name == "tests" or name.startswith("tests.")
    }
    try:
        module = importlib.import_module("tests.unit.test_filing_approval")
    finally:
        for name in [n for n in sys.modules if n == "tests" or n.startswith("tests.")]:
            sys.modules[f"api_{name}"] = sys.modules.pop(name)
        sys.modules.update(ours)
    return module


_api = _import_api_tests()
ATTORNEY: str = _api.ATTORNEY
NOW: float = _api.NOW
World = _api.World
make_world = _api.make_world
CASE_ID: str = sys.modules["api_tests.unit.test_packet_assembly"].CASE_ID

# Short enough that the slow and hang faults (2 s) exceed it.
REQUEST_TIMEOUT = 1.0


@dataclass
class Clock:
    now: float = NOW + 5

    def __call__(self) -> float:
        return self.now


@dataclass
class FilingWorld:
    """The API's approval world, the fake court, and the worker's own
    stores, wired together."""

    api: World
    court: FakeCmEcf
    credential_id: str
    filings: MemoryFilingStore = field(default_factory=MemoryFilingStore)
    documents: MemoryDocumentStore = field(default_factory=MemoryDocumentStore)
    kill_switch: StaticKillSwitch = field(
        default_factory=lambda: StaticKillSwitch(True)
    )
    clock: Clock = field(default_factory=Clock)
    environment: str = "local"
    driver: Callable[[], CourtDriver] | None = None
    lease_seconds: int = 600

    @property
    def case_id(self) -> str:
        return CASE_ID

    def deps(self) -> FilingDeps:
        fence = fence_for(self.environment)
        base_url = self.court.base_url
        api = self.api
        return FilingDeps(
            environment=self.environment,
            filings=self.filings,
            approvals=api.approvals,
            case_store=api.deps.case_store,
            debtor_store=api.deps.debtor_store,
            entity_store=api.deps.entity_store,
            packet_store=api.deps.packet_store,
            blobs=api.deps.blobs,
            document_store=self.documents,
            credentials=api.credentials,
            authorizations=api.authorizations,
            opener=LocalCredentialOpener(),
            access_log=api.log,
            kill_switch=self.kill_switch,
            fence=fence,
            http=lambda: FencedHttpClient(fence, timeout=REQUEST_TIMEOUT),
            fake_driver=self.driver or (lambda: FakeCmEcfDriver(base_url)),
            clock=self.clock,
            lease_seconds=self.lease_seconds,
        )

    def approve(self) -> tuple[FilingApproval, str]:
        """An approval by the attorney, freshly signed in — and the exact
        message body the API put on the queue for it."""
        approval = self.api.approve(credential_id=self.credential_id)
        body = json.dumps(self.api.queue.messages[-1])
        return approval, body

    def run(self, body: str) -> FilingResult:
        return run_filing(body, self.deps())

    def filing(self, approval: FilingApproval) -> Filing:
        stored = self.filings.get(CASE_ID, approval.filing_id)
        assert stored is not None
        return stored

    def actions(self) -> list[tuple[str, str, str | None]]:
        return [(e.action, e.outcome, e.purpose) for e in self.api.log.events]


def make_filing_world(court: FakeCmEcf) -> FilingWorld:
    api = make_world(signed=True, enrolled=False)
    account = court.account_json()
    credential = enrol_credential(
        parse_enrolment(
            {
                "login": account["login"],
                "password": account["password"],
                "totp_seed": account["totp_seed"],
                "courts": ["flmb"],
            }
        ),
        firm_id=api.firm_id,
        attorney_id=ATTORNEY,
        sealer=LocalCredentialSealer(),
        store=api.credentials,
        access_log=api.log,
        authorizations=api.authorizations,
    )
    return FilingWorld(
        api=api,
        court=court,
        credential_id=credential.credential_id,
        # The same table: filing the record files the case in one write.
        filings=MemoryFilingStore(api.deps.case_store),
    )


@pytest.fixture
def court() -> Iterator[FakeCmEcf]:
    with FakeCmEcf(slow_seconds=2.0, hang_seconds=2.0) as running:
        yield running


@pytest.fixture
def filing(court: FakeCmEcf) -> FilingWorld:
    return make_filing_world(court)


def _make_filing(**changes: object) -> Filing:
    base = {
        "filing_id": "00000000-0000-4000-8000-0000000000f1",
        "case_id": "00000000-0000-4000-8000-0000000000c1",
        "approval_id": "00000000-0000-4000-8000-0000000000a1",
        "firm_id": "proof-firm-00000000",
        "attorney_id": "00000000-0000-4000-8000-00000000a771",
        "credential_id": "00000000-0000-4000-8000-0000000000e1",
        "court": "flmb",
        "division": "3",
        "state": "claimed",
        "attempt_id": "attempt-1",
        "claimed_at": "2099-01-15T12:00:00.000Z",
        "lease_expires_at": 4071989400,
        "updated_at": "2099-01-15T12:00:00.000Z",
        "history": (Step("claimed", "2099-01-15T12:00:00.000Z"),),
    }
    return Filing(**(base | changes))  # type: ignore[arg-type]


@pytest.fixture
def make_filing() -> Callable[..., Filing]:
    """A filing record with fake ids; keyword arguments replace members."""
    return _make_filing
