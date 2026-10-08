"""The filing worker's own ports. The stores it shares with the API come from
`insolvia_core.ports` (cases, debtors, entities, documents, blobs, the vault,
the access log) and from `insolvia_api.core.ports` (packets, approvals)."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Protocol

from .filings import Filing


class FilingStore(Protocol):
    """The filing records (core/filings.py). Every write is conditional."""

    def get(self, case_id: str, filing_id: str) -> Filing | None:
        """Strongly consistent."""
        ...

    def claim(self, filing: Filing) -> bool:
        """Create the record, conditional on there being none. True when this
        call created it — of two racing consumers, exactly one."""
        ...

    def transition(self, filing: Filing, *, expected_state: str) -> bool:
        """Write `filing` (its new state, its history, its outcome members),
        conditional on the stored record still being in `expected_state` AND
        still carrying `filing.attempt_id`. True when this call made the
        write; False when anything else moved first."""
        ...


class KillSwitch(Protocol):
    """ADR 0024's environment kill switch: one flag the maintainer flips to
    refuse every submission without a deploy. Read fresh on every call — a
    cached "on" would outlive the flip."""

    def submissions_enabled(self) -> bool: ...


@dataclass(frozen=True)
class HttpResponse:
    status: int
    headers: Mapping[str, str]
    body: bytes
    url: str

    def header(self, name: str) -> str | None:
        wanted = name.lower()
        for key, value in self.headers.items():
            if key.lower() == wanted:
                return value
        return None

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace")


@dataclass(frozen=True)
class HttpRequest:
    method: str
    url: str
    body: bytes | None = None
    headers: Mapping[str, str] = field(default_factory=dict)


class HttpTimeoutError(Exception):
    """The court did not answer within the request timeout."""


class HttpTransportError(Exception):
    """The connection failed (refused, reset, TLS). Carries no request data."""


class HttpClient(Protocol):
    """One court session's HTTP. Implementations MUST pass every URL through
    the environment's host fence before connecting, MUST NOT follow
    redirects themselves (the driver follows each hop through `send`, so the
    fence sees it), and keep the session's cookies."""

    def send(self, request: HttpRequest) -> HttpResponse: ...
