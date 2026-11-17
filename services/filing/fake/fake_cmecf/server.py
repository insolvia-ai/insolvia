"""The fake CM/ECF server: PACER-style sign-in with a REAL TOTP check, the
Open BK Case screens, document upload, a review screen, the final submit and
a confirmation with a case number — plus the fault modes the worker must
survive. Standard library only; listens on loopback only.

    python -m fake_cmecf --port 8790 --fault none     (scripts/dev-up.sh)
    FakeCmEcf(fault="bad_login").start()                (tests, in-process)

THE ACCOUNT. One fake attorney per server: login `FAKE-ECF-USER`, a password
and a TOTP seed minted from `secrets` at start-up. Nothing real, nothing
committed; `/__fake/account` hands them to the local tooling that enrols
them in the dev vault (loopback only, like the rest of the server).

COUNTING. `submissions` counts final-submit requests the server RECEIVED —
incremented before anything else the handler does, so a request whose
response is lost (the `timeout_after_submit` fault) still counts. "A
redelivered job and a crash after the final submit never produce a second
filing" is asserted as `submissions == 1` against this number.

FAULTS (one per run; `set_fault` or `POST /__fake/fault?mode=...`):

    none                  files normally
    bad_login             the sign-in screen comes back with an error
    totp_rejected         the MFA screen comes back with an error
    slow                  every response before the submit is delayed by
                          `slow_seconds` (longer than the worker's timeout)
    upload_500            the upload answers 500
    timeout_after_submit  the submit is RECORDED, then the response is held
                          past the worker's timeout
    duplicate_case        opening the case shows the duplicate-case screen
    unexpected_screen     after MFA, an unknown notice screen
    receipt_missing       the confirmation has no case number
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import html
import json
import secrets
import struct
import threading
import time
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Final
from urllib.parse import parse_qs, urlsplit

FAULTS: Final = (
    "none",
    "bad_login",
    "totp_rejected",
    "slow",
    "upload_500",
    "timeout_after_submit",
    "duplicate_case",
    "unexpected_screen",
    "receipt_missing",
)

LOGIN: Final = "FAKE-ECF-USER"
_SESSION_COOKIE: Final = "FAKEECF"


def _totp(seed: str, counter: int) -> str:
    """RFC 6238, written independently of the worker's core/totp.py so the two
    cross-check each other rather than share a bug."""
    key = base64.b32decode(seed + "=" * (-len(seed) % 8))
    mac = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = mac[19] & 0xF
    code = (int.from_bytes(mac[offset : offset + 4], "big") & 0x7FFFFFFF) % 1_000_000
    return f"{code:06d}"


def _page(title: str, body: str) -> bytes:
    return (
        "<!doctype html><html><head><meta charset='utf-8'>"
        f"<title>{html.escape(title)}</title></head><body>"
        f"<h1>{html.escape(title)}</h1>{body}</body></html>"
    ).encode()


def _message(text: str) -> str:
    return f"<p class='message'>{html.escape(text)}</p>"


# The statistics-record field counts NextGen 1.5.4-1.7.1 accept (the AO's
# Case Upload spec, ADR 0024 source S22, pp. 11-12), and the court's own
# words when the count is wrong.
CASE_UPLOAD_STAT_COUNTS: Final = (73, 78, 80)
CASE_UPLOAD_WRONG_COUNT: Final = (
    'The statistics record (type "stat") in the case information file'
    " (Debtor.txt) does not have the correct number of data items. Be sure you"
    " are using the correct Case Upload specification for this court."
)


def case_upload_refusal(content: bytes) -> str | None:
    """What the fake court says about a Debtor.txt, or None to accept it —
    the checks a court makes before it opens anything: ASCII, a first
    `stat` record with an accepted field count, and one or two `debt`
    records each carrying an nnn-nn-nnnn tax id. Not the spec validator
    (services/api core/case_upload.py is that); a court's own refusal."""
    try:
        lines = content.decode("ascii").split("\n")
    except UnicodeDecodeError:
        return "The case information file (Debtor.txt) could not be read."
    if not lines or not lines[0].startswith("stat|"):
        return CASE_UPLOAD_WRONG_COUNT
    if len(lines[0].split("|")) not in CASE_UPLOAD_STAT_COUNTS:
        return CASE_UPLOAD_WRONG_COUNT
    debtors = [line.split("|") for line in lines if line.startswith("debt|")]
    if len(debtors) not in (1, 2):
        return "The case information file (Debtor.txt) has no debtor record."
    for debtor in debtors:
        ssn = debtor[7] if len(debtor) > 7 else ""
        digits = ssn.replace("-", "")
        if len(ssn) != 11 or not digits.isdigit() or ssn[3] != "-" or ssn[6] != "-":
            return "A debtor record's SSN/ITIN is not in nnn-nn-nnnn format."
    return None


LOGIN_FORM: Final = (
    "<form action='/login' method='post'>"
    "<input name='login'><input name='password' type='password'>"
    "<label><input name='redaction_compliance' type='checkbox'> I comply with"
    " the redaction rules</label><button>Sign in</button></form>"
)
MFA_FORM: Final = (
    "<form action='/mfa' method='post'><input name='otp'><button>Verify</button></form>"
)


@dataclass
class _Session:
    stage: str = "mfa"
    opened: bool = False
    uploads: list[tuple[int, str, str, int]] = field(default_factory=list)
    token: str = field(default_factory=lambda: secrets.token_hex(8))
    submitted: bool = False


@dataclass
class _State:
    fault: str = "none"
    submissions: int = 0
    uploads: int = 0
    # Case Upload files (Debtor.txt) the court ACCEPTED — the count only;
    # the fake keeps no byte of one (it carries the full SSN).
    case_uploads: int = 0
    sign_ins: int = 0
    case_numbers: list[str] = field(default_factory=list)
    sessions: dict[str, _Session] = field(default_factory=dict)


class FakeCmEcf:
    def __init__(
        self,
        *,
        fault: str = "none",
        port: int = 0,
        slow_seconds: float = 3.0,
        hang_seconds: float = 4.0,
    ) -> None:
        if fault not in FAULTS:
            raise ValueError(f"unknown fault {fault!r}")
        self.login = LOGIN
        self.password = secrets.token_urlsafe(18)
        self.totp_seed = base64.b32encode(secrets.token_bytes(20)).decode("ascii")
        self.slow_seconds = slow_seconds
        self.hang_seconds = hang_seconds
        self._lock = threading.RLock()
        self._state = _State(fault=fault)
        self._server = ThreadingHTTPServer(("127.0.0.1", port), _handler_for(self))
        self._server.daemon_threads = True
        self._thread: threading.Thread | None = None

    # ── control (tests, the dev proof) ──────────────────────────

    @property
    def base_url(self) -> str:
        host, port = self._server.server_address[:2]
        name = host.decode() if isinstance(host, bytes) else str(host)
        return f"http://{name}:{port}"

    @property
    def submissions(self) -> int:
        with self._lock:
            return self._state.submissions

    @property
    def uploads(self) -> int:
        with self._lock:
            return self._state.uploads

    @property
    def case_uploads(self) -> int:
        with self._lock:
            return self._state.case_uploads

    @property
    def fault(self) -> str:
        with self._lock:
            return self._state.fault

    def set_fault(self, fault: str) -> None:
        if fault not in FAULTS:
            raise ValueError(f"unknown fault {fault!r}")
        with self._lock:
            self._state.fault = fault

    def reset_counts(self) -> None:
        with self._lock:
            self._state.submissions = 0
            self._state.uploads = 0
            self._state.case_uploads = 0
            self._state.sign_ins = 0
            self._state.case_numbers.clear()
            self._state.sessions.clear()

    def state_json(self) -> dict[str, object]:
        with self._lock:
            return {
                "fault": self._state.fault,
                "submissions": self._state.submissions,
                "uploads": self._state.uploads,
                "caseUploads": self._state.case_uploads,
                "signIns": self._state.sign_ins,
                "caseNumbers": list(self._state.case_numbers),
            }

    def account_json(self) -> dict[str, str]:
        return {
            "login": self.login,
            "password": self.password,
            "totp_seed": self.totp_seed,
        }

    def start(self) -> str:
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()
        return self.base_url

    def serve_forever(self) -> None:
        self._server.serve_forever()

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()

    def __enter__(self) -> FakeCmEcf:
        self.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.stop()

    # ── the court (called by the handler, under the lock) ───────

    def _verify_otp(self, code: str) -> bool:
        step = int(time.time()) // 30
        return any(
            hmac.compare_digest(code, _totp(self.totp_seed, step + drift))
            for drift in (-1, 0, 1)
        )


def _handler_for(court: FakeCmEcf) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "FakeCMECF/1"

        def log_message(self, format: str, *args: object) -> None:
            # Quiet: a request line can carry nothing secret here (every
            # secret travels in a POST body), but a test run is no place
            # for an access log either.
            return

        # ── helpers ─────────────────────────────────────────────

        def _send(
            self,
            status: int,
            body: bytes,
            *,
            content_type: str = "text/html; charset=utf-8",
            headers: dict[str, str] | None = None,
        ) -> None:
            try:
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                for key, value in (headers or {}).items():
                    self.send_header(key, value)
                self.end_headers()
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def _redirect(
            self, location: str, headers: dict[str, str] | None = None
        ) -> None:
            self._send(303, b"", headers={"Location": location, **(headers or {})})

        def _form(self) -> dict[str, str]:
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length).decode("utf-8")
            return {key: values[0] for key, values in parse_qs(raw).items()}

        def _session(self) -> _Session | None:
            header = self.headers.get("Cookie") or ""
            for part in header.split(";"):
                name, _, value = part.strip().partition("=")
                if name == _SESSION_COOKIE:
                    return court._state.sessions.get(value)
            return None

        def _slow(self) -> None:
            if court.fault == "slow":
                time.sleep(court.slow_seconds)

        # ── routes ──────────────────────────────────────────────

        def do_GET(self) -> None:
            path = urlsplit(self.path).path
            if path == "/__fake/state":
                self._send(
                    200,
                    json.dumps(court.state_json()).encode(),
                    content_type="application/json",
                )
                return
            if path == "/__fake/account":
                self._send(
                    200,
                    json.dumps(court.account_json()).encode(),
                    content_type="application/json",
                )
                return
            self._slow()
            if path == "/login":
                self._send(200, _page("PACER Central Sign-On", LOGIN_FORM))
                return
            with court._lock:
                session = self._session()
                if path == "/mfa" and session is not None:
                    self._send(200, _page("Multifactor Authentication", MFA_FORM))
                    return
                if session is None or session.stage != "in":
                    self._redirect("/login")
                    return
                if path == "/menu":
                    self._send(
                        200,
                        _page(
                            "CM/ECF Bankruptcy - Main Menu",
                            "<form action='/open-case' method='get'>"
                            "<button>Open BK Case</button></form>",
                        ),
                    )
                elif path == "/notice":
                    self._send(
                        200,
                        _page(
                            "Important Notice",
                            _message("Updated terms of use. Acknowledge to continue.")
                            + "<form action='/notice' method='post'>"
                            "<input name='acknowledge' type='checkbox'></form>",
                        ),
                    )
                elif path == "/open-case":
                    self._send(
                        200,
                        _page(
                            "Open Bankruptcy Case",
                            "<form action='/open-case' method='post'>"
                            "<select name='chapter'><option>7</option></select>"
                            "<input name='office'><select name='joint'>"
                            "<option>no</option><option>yes</option></select>"
                            "<button>Next</button></form>",
                        ),
                    )
                elif path == "/upload" and session.opened:
                    self._send(200, self._upload_page(session))
                elif path == "/review" and session.opened:
                    entries = "".join(
                        f"<li>{position}|{html.escape(name)}|{digest}</li>"
                        for position, name, digest, _ in session.uploads
                    )
                    self._send(
                        200,
                        _page(
                            "Review Filing",
                            f"<ul id='entries'>{entries}</ul>"
                            "<form action='/submit' method='post'>"
                            "<input type='hidden' name='token'"
                            f" value='{session.token}'>"
                            "<button>Submit</button></form>",
                        ),
                    )
                else:
                    self._send(404, _page("Not Found", ""))

        def _upload_page(self, session: _Session, message: str | None = None) -> bytes:
            listed = "".join(
                f"<li>{position}. {html.escape(name)} ({size} bytes)</li>"
                for position, name, _, size in session.uploads
            )
            return _page(
                "Upload Documents",
                (_message(message) if message else "")
                + f"<ol id='uploaded'>{listed}</ol>"
                "<form action='/upload' method='post' enctype='multipart/form-data'>"
                "<input name='position'><input name='event'><input name='file_name'>"
                "<input name='file' type='file'><button>Upload</button></form>"
                "<form action='/review' method='get'><button>Continue</button></form>",
            )

        def do_POST(self) -> None:
            parts = urlsplit(self.path)
            if parts.path == "/__fake/fault":
                mode = parse_qs(parts.query).get("mode", ["none"])[0]
                try:
                    court.set_fault(mode)
                except ValueError:
                    self._send(400, b"unknown fault", content_type="text/plain")
                    return
                self._send(
                    200,
                    json.dumps(court.state_json()).encode(),
                    content_type="application/json",
                )
                return
            if parts.path == "/submit":
                self._submit()
                return
            self._slow()
            if parts.path == "/login":
                self._login()
            elif parts.path == "/mfa":
                self._mfa()
            elif parts.path == "/open-case":
                self._open_case()
            elif parts.path == "/upload":
                self._upload()
            else:
                self._send(404, _page("Not Found", ""))

        def _login(self) -> None:
            form = self._form()
            with court._lock:
                good = (
                    court.fault != "bad_login"
                    and hmac.compare_digest(form.get("login", ""), court.login)
                    and hmac.compare_digest(form.get("password", ""), court.password)
                    and form.get("redaction_compliance") == "on"
                )
                if not good:
                    self._send(
                        200,
                        _page(
                            "PACER Central Sign-On",
                            _message("Login failed: invalid username or password.")
                            + LOGIN_FORM,
                        ),
                    )
                    return
                token = secrets.token_hex(16)
                court._state.sessions[token] = _Session()
            self._redirect(
                "/mfa", {"Set-Cookie": f"{_SESSION_COOKIE}={token}; HttpOnly"}
            )

        def _mfa(self) -> None:
            form = self._form()
            with court._lock:
                session = self._session()
                if session is None:
                    self._redirect("/login")
                    return
                if court.fault == "totp_rejected" or not court._verify_otp(
                    form.get("otp", "")
                ):
                    self._send(
                        200,
                        _page(
                            "Multifactor Authentication",
                            _message("The code is not valid.") + MFA_FORM,
                        ),
                    )
                    return
                session.stage = "in"
                court._state.sign_ins += 1
                target = "/notice" if court.fault == "unexpected_screen" else "/menu"
            self._redirect(target)

        def _open_case(self) -> None:
            form = self._form()
            with court._lock:
                session = self._session()
                if session is None or session.stage != "in":
                    self._redirect("/login")
                    return
                if court.fault == "duplicate_case":
                    self._send(
                        200,
                        _page(
                            "Possible Duplicate Case",
                            _message(
                                "A case was filed for a debtor with the same"
                                " identifiers within the last 30 days."
                            )
                            + "<form action='/open-case/continue' method='post'>"
                            "<input name='proceed' type='checkbox'></form>",
                        ),
                    )
                    return
                if form.get("chapter") != "7" or not form.get("office"):
                    self._send(
                        200,
                        _page(
                            "Open Bankruptcy Case",
                            _message("Chapter and office are required."),
                        ),
                    )
                    return
                session.opened = True
            self._redirect("/upload")

        def _upload(self) -> None:
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length)
            if court.fault == "upload_500":
                self._send(500, b"Internal Server Error", content_type="text/plain")
                return
            fields, content = _parse_multipart(
                self.headers.get("Content-Type", ""), raw
            )
            refusal = (
                case_upload_refusal(content)
                if fields.get("event") == "case_upload"
                else None
            )
            with court._lock:
                session = self._session()
                if session is None or not session.opened:
                    self._redirect("/login")
                    return
                if refusal is not None:
                    page = self._upload_page(session, message=refusal)
                    self._send(200, page)
                    return
                if fields.get("event") == "case_upload":
                    court._state.case_uploads += 1
                session.uploads.append(
                    (
                        int(fields.get("position", "0")),
                        fields.get("file_name", ""),
                        hashlib.sha256(content).hexdigest(),
                        len(content),
                    )
                )
                court._state.uploads += 1
                page = self._upload_page(session)
            self._send(200, page)

        def _submit(self) -> None:
            # COUNTED FIRST — before the form is read, before the fault, so
            # a submit whose response never arrives still counts.
            with court._lock:
                court._state.submissions += 1
                fault = court._state.fault
            form = self._form()
            with court._lock:
                session = self._session()
                if session is None or form.get("token") != session.token:
                    self._send(200, _page("Session Expired", ""))
                    return
                session.submitted = True
                number = f"6:26-bk-{10000 + len(court._state.case_numbers):05d}"
                court._state.case_numbers.append(number)
                docs = "".join(
                    f"<li>{position} {html.escape(name)}</li>"
                    for position, name, _, _ in session.uploads
                )
            if fault == "timeout_after_submit":
                time.sleep(court.hang_seconds)
            filed_at = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            case_number = (
                ""
                if fault == "receipt_missing"
                else f"<p id='case-number'>{number}</p>"
            )
            self._send(
                200,
                _page(
                    "Notice of Bankruptcy Case Filing",
                    case_number + f"<p id='filed-at'>{filed_at}</p>"
                    f"<ul id='entries'>{docs}</ul>"
                    f"<p id='receipt-number'>FAKE-{secrets.token_hex(4).upper()}</p>"
                    "<p id='fee-due'>$338.00</p>"
                    "<form action='/payment' method='get'><button>Pay filing fee"
                    "</button></form>",
                ),
            )

    return Handler


def _parse_multipart(content_type: str, raw: bytes) -> tuple[dict[str, str], bytes]:
    """Just enough multipart/form-data for the upload form."""
    _, _, boundary = content_type.partition("boundary=")
    if not boundary:
        return {}, b""
    fields: dict[str, str] = {}
    content = b""
    for part in raw.split(b"--" + boundary.encode()):
        head, sep, body = part.partition(b"\r\n\r\n")
        if not sep:
            continue
        if body.endswith(b"\r\n"):
            body = body[:-2]
        disposition = head.decode("utf-8", errors="replace")
        name = disposition.split('name="', 1)[-1].split('"', 1)[0]
        if "filename=" in disposition:
            content = body
        else:
            fields[name] = body.decode("utf-8")
    return fields, content
