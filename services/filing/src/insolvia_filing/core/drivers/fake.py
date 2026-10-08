"""The driver for the fake CM/ECF (services/filing/fake/fake_cmecf) — the one
court a laptop has, and the reference shape PR 10's real drivers follow.

It is a real driver in every way that matters: it fingerprints every screen
it acts on (`SCREENS`), stops on any screen it does not know, follows each
redirect itself through the fenced client, checks that the court's own list
of uploaded documents is exactly what it sent before it submits, and treats
anything but a recognised confirmation after the final submit as
`SubmitUncertainError`. It never retries.

It is composed only where the fake exists (`INSOLVIA_ENV=local` with
`FAKE_CMECF_URL`; entrypoints/compose.py), and even a composed one can reach
nothing but loopback — the fence (core/fence.py) is the second wall.
"""

from __future__ import annotations

import uuid
from collections.abc import Callable, Mapping, Sequence
from typing import Final
from urllib.parse import urlencode, urljoin

from ..ports import (
    HttpClient,
    HttpRequest,
    HttpResponse,
    HttpTimeoutError,
    HttpTransportError,
)
from .base import (
    CaseOpening,
    CourtConfirmation,
    FilingFile,
    HandBackError,
    SubmitUncertainError,
)
from .screens import Screen, ScreenSpec, court_text, parse_screen, recognise

DRIVER_ID: Final = "fake-cmecf/1"

# The fingerprints — every screen this driver will act on. A change to the
# fake's markup that moves a form or a field fails tests/unit/test_fake_driver.py
# first, exactly as a court's drift would stop a real driver.
SCREENS: Final[Mapping[str, ScreenSpec]] = {
    "login": ScreenSpec(
        "PACER Central Sign-On",
        (("/login", ("login", "password", "redaction_compliance")),),
    ),
    "mfa": ScreenSpec("Multifactor Authentication", (("/mfa", ("otp",)),)),
    "menu": ScreenSpec("CM/ECF Bankruptcy - Main Menu", (("/open-case", ()),)),
    "open_case": ScreenSpec(
        "Open Bankruptcy Case", (("/open-case", ("chapter", "joint", "office")),)
    ),
    "duplicate": ScreenSpec(
        "Possible Duplicate Case", (("/open-case/continue", ("proceed",)),)
    ),
    "upload": ScreenSpec(
        "Upload Documents",
        (
            ("/upload", ("event", "file", "file_name", "position")),
            ("/review", ()),
        ),
    ),
    "review": ScreenSpec("Review Filing", (("/submit", ("token",)),)),
    "confirmation": ScreenSpec("Notice of Bankruptcy Case Filing", (("/payment", ()),)),
}

_MAX_REDIRECTS: Final = 5


class FakeCmEcfDriver:
    def __init__(self, base_url: str) -> None:
        self._base = base_url.rstrip("/") + "/"

    @property
    def driver_id(self) -> str:
        return DRIVER_ID

    @property
    def base_urls(self) -> tuple[str, ...]:
        return (self._base,)

    def start(self, http: HttpClient) -> FakeCmEcfSession:
        return FakeCmEcfSession(self._base, http)


class FakeCmEcfSession:
    def __init__(self, base: str, http: HttpClient) -> None:
        self._base = base
        self._http = http
        self._token: str | None = None

    # ── transport ───────────────────────────────────────────────

    def _send(self, request: HttpRequest) -> HttpResponse:
        """One request and its redirects, each hop through the fenced client.
        A transport failure before the final submit is a hand-back."""
        try:
            response = self._http.send(request)
            for _ in range(_MAX_REDIRECTS):
                location = response.header("Location")
                if response.status not in (301, 302, 303, 307) or location is None:
                    break
                response = self._http.send(
                    HttpRequest("GET", urljoin(response.url, location))
                )
        except HttpTimeoutError as error:
            raise HandBackError("court_timeout") from error
        except HttpTransportError as error:
            raise HandBackError("court_error") from error
        if response.status >= 500:
            raise HandBackError("court_error", court_said=f"HTTP {response.status}")
        return response

    def _get(self, path: str) -> HttpResponse:
        return self._send(HttpRequest("GET", urljoin(self._base, path.lstrip("/"))))

    def _post(self, path: str, fields: Mapping[str, str]) -> HttpResponse:
        return self._send(
            HttpRequest(
                "POST",
                urljoin(self._base, path.lstrip("/")),
                body=urlencode(fields).encode("utf-8"),
                headers={"Content-Type": "application/x-www-form-urlencoded"},
            )
        )

    @staticmethod
    def _expect(response: HttpResponse, *allowed: str) -> tuple[str, Screen]:
        screen = parse_screen(response.text)
        name = recognise(screen, {key: SCREENS[key] for key in allowed})
        if name is None:
            raise HandBackError(
                "unrecognised_screen", court_said=court_text(screen.title)
            )
        return name, screen

    @staticmethod
    def _said(screen: Screen) -> str | None:
        return court_text(" ".join(screen.messages)) if screen.messages else None

    # ── the four steps ──────────────────────────────────────────

    def sign_in(self, login: str, password: str, totp: Callable[[], str]) -> None:
        self._expect(self._get("/login"), "login")
        name, screen = self._expect(
            self._post(
                "/login",
                {
                    "login": login,
                    "password": password,
                    # The redaction-compliance acknowledgement PACER asks of
                    # every filer at sign-in (ADR 0024 research §1) — the
                    # attorney gave it in the authorization they signed.
                    "redaction_compliance": "on",
                },
            ),
            "mfa",
            "login",
        )
        if name == "login":
            raise HandBackError("sign_in_failed", court_said=self._said(screen))
        name, screen = self._expect(self._post("/mfa", {"otp": totp()}), "menu", "mfa")
        if name == "mfa":
            raise HandBackError("mfa_rejected", court_said=self._said(screen))

    def open_case(self, opening: CaseOpening) -> None:
        self._expect(self._get("/open-case"), "open_case")
        name, screen = self._expect(
            self._post(
                "/open-case",
                {
                    "chapter": str(opening.chapter),
                    "office": opening.division,
                    "joint": "yes" if opening.joint else "no",
                },
            ),
            "upload",
            "duplicate",
        )
        if name == "duplicate":
            raise HandBackError("duplicate_case", court_said=self._said(screen))

    def upload(self, files: Sequence[FilingFile]) -> None:
        for file in files:
            boundary = uuid.uuid4().hex
            body = _multipart(
                boundary,
                {
                    "position": str(file.position),
                    "event": file.handling,
                    "file_name": file.file_name,
                },
                file_field=("file", file.file_name, file.content),
            )
            self._expect(
                self._send(
                    HttpRequest(
                        "POST",
                        urljoin(self._base, "upload"),
                        body=body,
                        headers={
                            "Content-Type": f"multipart/form-data; boundary={boundary}"
                        },
                    )
                ),
                "upload",
            )
        _, review = self._expect(self._get("/review"), "review")
        expected = tuple(f"{f.position}|{f.file_name}|{f.sha256}" for f in files)
        if review.entries != expected:
            raise HandBackError("upload_mismatch")
        form = review.forms[0]
        token = form.hidden.get("token")
        if not token:
            raise HandBackError("unrecognised_screen", court_said="no submit token")
        self._token = token

    def final_submit(self) -> CourtConfirmation:
        """THE CLICK. Never a HandBackError from here: whatever goes wrong, the
        court may have the filing."""
        if self._token is None:
            # A programming error, caught before the request: nothing sent.
            raise RuntimeError("final_submit before a reviewed upload")
        try:
            response = self._http.send(
                HttpRequest(
                    "POST",
                    urljoin(self._base, "submit"),
                    body=urlencode({"token": self._token}).encode("utf-8"),
                    headers={"Content-Type": "application/x-www-form-urlencoded"},
                )
            )
        except HttpTimeoutError as error:
            raise SubmitUncertainError("submit_timeout") from error
        except Exception as error:
            raise SubmitUncertainError("submit_error") from error
        if response.status != 200:
            raise SubmitUncertainError(
                "submit_error", court_said=f"HTTP {response.status}"
            )
        screen = parse_screen(response.text)
        if recognise(screen, {"confirmation": SCREENS["confirmation"]}) is None:
            raise SubmitUncertainError(
                "unrecognised_confirmation", court_said=court_text(screen.title)
            )
        case_number = screen.by_id.get("case-number", "").strip()
        filed_at = screen.by_id.get("filed-at", "").strip()
        if not case_number or not filed_at:
            raise SubmitUncertainError("receipt_missing")
        return CourtConfirmation(
            case_number=court_text(case_number, limit=40),
            filed_at=court_text(filed_at, limit=40),
            docket_entries=tuple(court_text(e) for e in screen.entries),
            receipt_number=screen.by_id.get("receipt-number") or None,
            fee_due=screen.by_id.get("fee-due") or None,
            page=response.body,
        )


def _multipart(
    boundary: str,
    fields: Mapping[str, str],
    *,
    file_field: tuple[str, str, bytes],
) -> bytes:
    lines: list[bytes] = []
    for name, value in fields.items():
        lines += [
            f"--{boundary}".encode(),
            f'Content-Disposition: form-data; name="{name}"'.encode(),
            b"",
            value.encode("utf-8"),
        ]
    field_name, file_name, content = file_field
    lines += [
        f"--{boundary}".encode(),
        (
            f'Content-Disposition: form-data; name="{field_name}"; '
            f'filename="{file_name}"'
        ).encode(),
        b"Content-Type: application/octet-stream",
        b"",
        content,
        f"--{boundary}--".encode(),
        b"",
    ]
    return b"\r\n".join(lines)
