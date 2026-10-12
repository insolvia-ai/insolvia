"""The driver interface: one driver per court (ADR 0024, "Per-court drivers").

A driver knows one court's screens. The WORKER (core/worker.py) owns
everything else — the state machine, the credential, the re-checks, the
mark before the final submit — and calls a session's four steps in order,
writing a state between each:

    sign_in        claimed    -> signed_in
    open_case      signed_in  -> uploading
    upload         (still uploading)
    final_submit   at_final_submit -> submitted   (the one irreversible act)

A step that cannot go on raises `HandBackError` — before the final submit that is
always safe, because nothing reached the docket. `final_submit` itself NEVER
raises `HandBackError`: anything but a recognised confirmation is
`SubmitUncertainError`, which the worker records as `outcome_unknown`. A driver
does not retry, does not click forward through a screen it does not
recognise, and does not alter its input to get past a refusal.

Every screen a driver acts on is FINGERPRINTED (`screens.ScreenSpec`); a
screen it does not recognise is a stop, never a guess.

This PR ships two: the FAKE driver (drivers/fake.py), which speaks the fake
CM/ECF's screens and exists only where the fake does, and the HAND-BACK
outcome for every real court — `resolve_driver` returns None for a court
with no verified driver, and the worker hands the job back before it opens a
credential. Every court is unverified today (PR 10 verifies them one by one).
"""

from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Final, Protocol

from ..ports import HttpClient


class HandBackError(Exception):
    """Stop before the final submit. `reason` is a key of
    hand_back.HAND_BACK_REASONS; `court_said` is the court's own message
    when it gave one (short text, never a page)."""

    def __init__(self, reason: str, *, court_said: str | None = None) -> None:
        super().__init__(reason)
        self.reason = reason
        self.court_said = court_said


class SubmitUncertainError(Exception):
    """The final submit happened (or may have) and its result is not a
    recognised confirmation. `reason` is a key of
    hand_back.OUTCOME_UNKNOWN_REASONS."""

    def __init__(self, reason: str, *, court_said: str | None = None) -> None:
        super().__init__(reason)
        self.reason = reason
        self.court_said = court_said


@dataclass(frozen=True)
class CaseOpening:
    """What the opening screens need. No debtor identity crosses here in this
    PR: the fake opens a case by chapter, office and joint flag; PR 9's
    Case Upload file (and its logged tax-id read) is where a real court's
    debtor data comes from — `CourtDriver.case_upload`, uploaded as the
    first `FilingFile`."""

    court: str
    division: str
    chapter: int
    joint: bool


@dataclass(frozen=True)
class FilingFile:
    """One document to upload, in docket order: the bytes, and the digest the
    approval was bound to (already checked against the bytes).

    The Case Upload file is one too — position 0, key `case_upload`, built
    in memory at upload time from records the approval's digest binds; its
    bytes carry the full SSN, so they stay out of the repr like every
    document's, and the file is never stored."""

    position: int
    key: str
    file_name: str
    handling: str
    content: bytes = field(repr=False)
    sha256: str


@dataclass(frozen=True)
class CourtConfirmation:
    """What the court's confirmation screen said."""

    case_number: str
    filed_at: str
    docket_entries: tuple[str, ...]
    receipt_number: str | None
    fee_due: str | None
    page: bytes = field(repr=False)


class CourtSession(Protocol):
    def sign_in(self, login: str, password: str, totp: Callable[[], str]) -> None:
        """Sign in. `totp` computes the code at the moment the court asks —
        the session calls it, it is never computed ahead."""
        ...

    def open_case(self, opening: CaseOpening) -> None: ...

    def upload(self, files: Sequence[FilingFile]) -> None: ...

    def final_submit(self) -> CourtConfirmation: ...


class CourtDriver(Protocol):
    @property
    def driver_id(self) -> str: ...

    @property
    def base_urls(self) -> tuple[str, ...]:
        """Every origin this driver will contact. The worker checks each
        against the host fence BEFORE it opens the credential."""
        ...

    @property
    def case_upload(self) -> bool:
        """Whether this driver opens the case through the court's Case
        Upload (ADR 0024 PR 9) — the worker then builds Debtor.txt, with the
        full SSN from a logged `taxid.read`, and uploads it FIRST. A real
        driver says True only for a court whose Case Upload PR 10 verified on
        its training database (registry `case_upload.status`); a driver that
        types the opening screens instead says False, and no tax id is
        opened."""
        ...

    def start(self, http: HttpClient) -> CourtSession: ...


DriverFactory = Callable[[], CourtDriver]

# The verified drivers, by court code. EMPTY on purpose: a court joins only in
# ADR 0024 PR 10, after its driver opened the fixture case end to end on that
# court's training database — and its host joins core/fence.py in the same
# reviewed diff. Until then every real court hands back.
VERIFIED_DRIVERS: Final[Mapping[str, DriverFactory]] = {}


def resolve_driver(
    court: str,
    *,
    environment: str,
    fake: DriverFactory | None = None,
) -> CourtDriver | None:
    """The driver that files in `court` here, or None: hand it back.

    `fake` is the local composition's fake driver (entrypoints compose it
    only when INSOLVIA_ENV=local and FAKE_CMECF_URL is set). Locally it plays
    every court, because the fake is the only court a laptop has; anywhere
    else it is refused even if somebody composed one."""
    if fake is not None:
        return fake() if environment == "local" else None
    factory = VERIFIED_DRIVERS.get(court)
    return factory() if factory is not None else None
