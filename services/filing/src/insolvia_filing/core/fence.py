"""The environment host fence (ADR 0024, "Three environments").

"The filing worker's court hosts are fenced per environment, in code and in
configuration ... A worker asked to sign in anywhere else refuses before it
decrypts."

THE ALLOWLIST IS THIS MODULE'S CONSTANT, AND NOTHING ELSE. No environment
variable, parameter or message can add a host: a court joins only through a
reviewed diff here, made in ADR 0024 PR 10 after that court's driver has
opened the fixture case on its training database. The fence is enforced
twice, and both are ordinary code paths with tests:

1. `core/worker.py` checks every host a driver will use BEFORE the
   credential is opened — a disallowed host costs nothing and decrypts
   nothing;
2. `adapters/http/fenced_client.py` checks every request's URL BEFORE it
   creates a connection — including each redirect hop, which it never
   follows on its own. No socket is opened for a refused host
   (tests/unit/test_fence.py proves it with `socket.socket` patched to fail).

Today:

- `local` — the fake CM/ECF on loopback, and only by literal address
  (127.0.0.1, ::1). Not `localhost`: a name is resolved through files and
  resolvers this module does not control.
- `staging` — nothing. The fake is never deployed (services/filing/README.md
  says why), and no court's training database is verified yet, so a staging
  worker hands back every job before it opens a credential.
- `production` — nothing, and with the kill switch off besides. Prod's worker
  hands back every job until PR 10 verifies a district.

Only `https` reaches a deployed allowlist; `http` is accepted for loopback
alone, where the fake listens.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Final
from urllib.parse import urlsplit

LOOPBACK: Final = frozenset({"127.0.0.1", "::1"})

ALLOWED_HOSTS: Final[Mapping[str, frozenset[str]]] = {
    "local": LOOPBACK,
    "staging": frozenset(),
    "production": frozenset(),
}


class HostNotAllowedError(Exception):
    """A request, or a driver, named a host outside this environment's
    allowlist. Raised before any connection exists. The message carries the
    host only — never a path, query or credential."""

    def __init__(self, host: str, environment: str) -> None:
        super().__init__(f"{host!r} is not an allowed court host in {environment}")
        self.host = host
        self.environment = environment


@dataclass(frozen=True)
class Target:
    """A checked request target: what the HTTP client may connect to."""

    scheme: str
    host: str
    port: int
    path: str


@dataclass(frozen=True)
class HostFence:
    environment: str
    allowed: frozenset[str]

    def check(self, url: str) -> Target:
        """The target `url` names, or HostNotAllowedError. Refuses anything
        that is not plain http(s) to an allowlisted host: userinfo, a
        missing host, another scheme, http to a non-loopback host."""
        parts = urlsplit(url)
        host = (parts.hostname or "").lower()
        if (
            not host
            or parts.username is not None
            or parts.password is not None
            or parts.scheme not in ("http", "https")
            or host not in self.allowed
            or (parts.scheme == "http" and host not in LOOPBACK)
        ):
            raise HostNotAllowedError(host or "<none>", self.environment)
        port = parts.port or (443 if parts.scheme == "https" else 80)
        path = parts.path or "/"
        if parts.query:
            path = f"{path}?{parts.query}"
        return Target(scheme=parts.scheme, host=host, port=port, path=path)

    def permits(self, url: str) -> bool:
        try:
            self.check(url)
        except HostNotAllowedError:
            return False
        return True


def fence_for(environment: str) -> HostFence:
    """The fence for an environment. An unknown environment gets an EMPTY
    allowlist — never a default that reaches somewhere."""
    return HostFence(
        environment=environment,
        allowed=ALLOWED_HOSTS.get(environment, frozenset()),
    )
