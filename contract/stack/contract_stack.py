"""The Python contract stack: today's backend, offline, for the contract suite.

ADR 0028 makes the running Python backend the ORACLE for the .NET rewrite:
`contract/` is a TypeScript suite that calls a base URL, and this file is
the thing it calls on a laptop. It composes the three HTTP services the suite
covers — the tenant API, the admin service and the MCP server — over their
EXISTING in-memory adapters, preloads them from `seeds/dev.json` through the
admin seed loader's own in-memory path, and serves them on three local ports.
No AWS, no Cognito, no Google, no network beyond 127.0.0.1.

## Why one process, outside `services/`

Deployed, the three services share one set of tables: a firm the admin
service provisions is a firm the API resolves, and a candidate the MCP server
proposes is one the API's review queue lists. Memory stores live in a
process, so sharing them means one process — and a module that imports
`insolvia_api`, `insolvia_admin` and `insolvia_mcp` together cannot sit in
any one service's `entrypoints/` (admin's architecture test puts
`insolvia_api` out of bounds everywhere, by design). So the composition lives
here, beside the suite it serves, and is deleted with the Python (18.23).
Nothing in `services/` imports it, and no Lambda entrypoint can reach it.

## Authentication is real

There is no bypass and no test mode. Every service is configured exactly as
it is deployed — an issuer URL and client ids — and composes the SAME
`CognitoJwksProvider` its own entrypoints do. The only difference is where
the keys are published: the auxiliary server below serves a locally
generated RS256 JWKS (`CONTRACT_JWKS`, written by
`contract/scripts/make-signing-key.ts`) at `<issuer>/.well-known/jwks.json`,
which is the URL the provider derives for a Cognito issuer, and at the
explicit `jwks_url` the admin service already passes for Google. Tokens the
suite mints are verified signature-first, issuer, client id, `token_use`,
`hd`, lifetime — the whole fail-closed path.

## Bytes move, too

The memory blob store mints `memory+s3://` URLs nothing can fetch — honest
for unit tests, useless for a suite that uploads through the shipped
client's presigned PUT. `LocalBlobStore` keeps every behaviour of the memory
store and only changes the URL: it points at the auxiliary server, which
accepts one PUT per minted capability with the content type and byte count
S3 would bind into the signature, and serves GETs. That is a stand-in for
S3, not a change to any route.

Run by `contract/scripts/dev-test.sh`; never by hand in CI yet.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, quote, unquote, urlencode, urlparse

HERE = Path(__file__).resolve().parent
CONTRACT = HERE.parent
REPO = CONTRACT.parent
for service in ("api", "admin", "mcp"):
    sys.path.insert(0, str(REPO / "services" / service / "src"))

import uvicorn  # noqa: E402
from insolvia_admin.adapters.memory.audit_log import MemoryAuditLog  # noqa: E402
from insolvia_admin.api.app_factory import create_app as create_admin_app  # noqa: E402
from insolvia_admin.api.dependencies import AdminDependencies  # noqa: E402
from insolvia_admin.core.config import load_config as load_admin_config  # noqa: E402
from insolvia_admin.entrypoints import seed  # noqa: E402
from insolvia_api.adapters.memory.event_store import (  # noqa: E402
    MemoryCalendarTokenStore,
    MemoryEventStore,
)
from insolvia_api.adapters.memory.filing_approval_store import (  # noqa: E402
    MemoryFilingApprovalStore,
)
from insolvia_api.adapters.memory.filing_queue import MemoryFilingQueue  # noqa: E402
from insolvia_api.adapters.memory.job_queue import MemoryJobQueue  # noqa: E402
from insolvia_api.adapters.memory.job_store import MemoryJobStore  # noqa: E402
from insolvia_api.adapters.memory.mailer_client import (  # noqa: E402
    InMemoryMailerClient,
)
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore  # noqa: E402
from insolvia_api.adapters.memory.waitlist_store import (  # noqa: E402
    MemoryWaitlistStore,
)
from insolvia_api.api.app_factory import create_app as create_api_app  # noqa: E402
from insolvia_api.api.dependencies import ApiDependencies  # noqa: E402
from insolvia_api.core.config import load_config as load_api_config  # noqa: E402
from insolvia_core.adapters.aws.jwks_provider import CognitoJwksProvider  # noqa: E402
from insolvia_core.adapters.memory.access_log import MemoryAccessLog  # noqa: E402
from insolvia_core.adapters.memory.candidate_store import (  # noqa: E402
    MemoryCandidateStore,
)
from insolvia_core.adapters.memory.case_entity_store import (  # noqa: E402
    MemoryCaseEntityStore,
)
from insolvia_core.adapters.memory.case_store import MemoryCaseStore  # noqa: E402
from insolvia_core.adapters.memory.client_binding_store import (  # noqa: E402
    MemoryClientBindingStore,
)
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore  # noqa: E402
from insolvia_core.adapters.memory.document_blobs import (  # noqa: E402
    MemoryDocumentBlobStore,
)
from insolvia_core.adapters.memory.document_request_store import (  # noqa: E402
    MemoryDocumentRequestStore,
)
from insolvia_core.adapters.memory.document_store import (  # noqa: E402
    MemoryDocumentStore,
)
from insolvia_core.adapters.memory.filing_credentials import (  # noqa: E402
    LocalCredentialSealer,
    MemoryFilingAuthorizationStore,
    MemoryFilingCredentialStore,
)
from insolvia_core.adapters.memory.filing_store import MemoryFilingStore  # noqa: E402
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore  # noqa: E402
from insolvia_core.adapters.memory.task_store import MemoryTaskStore  # noqa: E402
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher  # noqa: E402
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore  # noqa: E402
from insolvia_core.adapters.memory.user_directory import (  # noqa: E402
    MemoryUserDirectory,
)
from insolvia_core.auth import GOOGLE_ISSUERS  # noqa: E402
from insolvia_mcp.api.dependencies import McpDependencies  # noqa: E402
from insolvia_mcp.api.server import create_asgi_app  # noqa: E402
from insolvia_mcp.core.config import load_config as load_mcp_config  # noqa: E402
from werkzeug.serving import make_server  # noqa: E402

HOST = "127.0.0.1"


def _port(name: str, default: int) -> int:
    return int(os.environ.get(name, str(default)))


API_PORT = _port("CONTRACT_API_PORT", 18080)
ADMIN_PORT = _port("CONTRACT_ADMIN_PORT", 18090)
MCP_PORT = _port("CONTRACT_MCP_PORT", 18070)
AUX_PORT = _port("CONTRACT_AUX_PORT", 18060)

# The trust configuration — the only values the suite and this stack must
# agree on, and they live in ONE file both read (contract/stack/stack.json).
STACK = json.loads((HERE / "stack.json").read_text())
AUX = f"http://{HOST}:{AUX_PORT}"
ISSUER_URL = f"{AUX}/{STACK['issuerPath']}"
GOOGLE_JWKS_URL = f"{AUX}/google/certs"

# The seed loader's guards accept only dev/staging names; these are dev-shaped
# names for tables that exist nowhere but in this process's memory.
FIRM_TABLE = "insolvia-dev-000000000000-firms"
CASE_TABLE = "insolvia-dev-000000000000-cases"
DOCUMENT_BUCKET = "insolvia-dev-000000000000-case-documents-local"
FIXTURE_BUCKET = "insolvia-shared-dev-fixtures-local"


# ── the auxiliary server: the JWKS and a stand-in for S3 ────────────


class LocalBlobStore(MemoryDocumentBlobStore):
    """The memory blob store with URLs the auxiliary server answers."""

    def _url(self, storage_ref: str, **terms: object) -> str:
        if not storage_ref:
            raise ValueError("a blob URL needs a storage ref")
        query = urlencode({key: str(value) for key, value in terms.items()})
        return f"{AUX}/blobs/{quote(storage_ref, safe='')}?{query}"


def _aux_handler(jwks: bytes, blobs: LocalBlobStore) -> type[BaseHTTPRequestHandler]:
    jwks_paths = {f"/{STACK['issuerPath']}/.well-known/jwks.json", "/google/certs"}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: Any) -> None:
            return

        def _send(self, status: int, body: bytes = b"", ctype: str = "") -> None:
            self.send_response(status)
            if ctype:
                self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _blob(self) -> tuple[str, dict[str, str]] | None:
            url = urlparse(self.path)
            if not url.path.startswith("/blobs/"):
                return None
            terms = {k: v[0] for k, v in parse_qs(url.query).items()}
            return unquote(url.path[len("/blobs/") :]), terms

        def do_GET(self) -> None:
            if urlparse(self.path).path in jwks_paths:
                self._send(200, jwks, "application/json")
                return
            found = self._blob()
            if found is None or found[1].get("method") != "GET":
                self._send(404)
                return
            content = blobs.contents.get(found[0])
            if content is None:
                self._send(404)
                return
            ctype = blobs.content_types.get(found[0], "application/octet-stream")
            self._send(200, content, ctype)

        def do_PUT(self) -> None:
            found = self._blob()
            if found is None or found[1].get("method") != "PUT":
                self._send(403)
                return
            ref, terms = found
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length)
            # What S3 binds into the signature, refused the way S3 would.
            if str(len(body)) != terms.get("content_length") or self.headers.get(
                "Content-Type"
            ) != terms.get("content_type"):
                self._send(403)
                return
            blobs.accept_upload(
                ref,
                byte_size=len(body),
                etag=hashlib.md5(body, usedforsecurity=False).hexdigest(),
                content=body,
            )
            blobs.content_types[ref] = terms.get("content_type", "")
            self._send(200)

    return Handler


# ── the seed loader's two seams that need something real ──────────


class StackAccounts:
    """`seed.Accounts` over the stack's fixed directory (stack.json).

    `seeds/dev.json` names people without passwords — on a laptop the account
    must already exist. Here "exists" means "is in stack.json's accounts",
    whose subjects the suite mints tokens for.
    """

    def __init__(self, directory: MemoryUserDirectory) -> None:
        self.directory = directory

    def subject_of(self, email: str) -> str:
        subject = self.directory.subjects.get(email)
        if subject is None:
            raise seed.RefusedError(f"no contract-stack account for {email}")
        return subject

    def ensure(self, email: str, password: str) -> str:
        return self.subject_of(email)


class LocalFixtureObjects:
    """`seed.FixtureObjects` reading the committed fixture folders directly —
    the shared fixture bucket's content IS seeds/fixtures/<version>/objects."""

    def __init__(self, blobs: MemoryDocumentBlobStore) -> None:
        self.blobs = blobs

    def copy(self, source_key: str, *, target_key: str, content_type: str) -> None:
        content = (REPO / "seeds" / "fixtures" / source_key).read_bytes()
        self.blobs.put_bytes(target_key, content=content, content_type=content_type)

    def put(self, key: str, *, content: bytes, content_type: str) -> None:
        raise RuntimeError("the contract stack never publishes fixtures")

    def digest(self, key: str) -> str | None:
        path = REPO / "seeds" / "fixtures" / key
        return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


# ── composition ────────────────────────────────────────────────────


def main() -> None:
    logging.basicConfig(level=logging.WARNING)
    jwks_path = os.environ.get("CONTRACT_JWKS")
    if not jwks_path:
        raise SystemExit("CONTRACT_JWKS (the public JWKS file) is required")
    jwks = Path(jwks_path).read_bytes()

    # One set of stores, shared by all three services — one "database".
    firm_store = MemoryFirmStore()
    user_directory = MemoryUserDirectory()
    for email, subject in STACK["accounts"].items():
        user_directory.subjects[email] = subject
    debtor_store = MemoryDebtorStore(firm_store=firm_store)
    case_store = MemoryCaseStore(debtor_store=debtor_store)
    access_log = MemoryAccessLog()
    case_entity_store = MemoryCaseEntityStore()
    document_store = MemoryDocumentStore()
    blobs = LocalBlobStore()
    tax_id_store = MemoryTaxIdStore()
    tax_id_cipher = LocalTaxIdCipher()
    candidate_store = MemoryCandidateStore()
    client_binding_store = MemoryClientBindingStore()

    code = seed.main(
        [
            "load",
            "--fixture",
            str(REPO / "seeds" / "dev.json"),
            "--firm-table",
            FIRM_TABLE,
            "--user-pool-id",
            "contract-stack",
            "--case-table",
            CASE_TABLE,
            "--document-bucket",
            DOCUMENT_BUCKET,
            "--fixture-bucket",
            FIXTURE_BUCKET,
        ],
        deps=seed.Dependencies(
            firm_store=lambda _: firm_store,
            accounts=lambda _: StackAccounts(user_directory),
            case_store=lambda _: case_store,
            debtor_store=lambda _: debtor_store,
            entity_store=lambda _: case_entity_store,
            document_store=lambda _: document_store,
            document_blobs=lambda _: blobs,
            fixture_objects=lambda _f, _t: LocalFixtureObjects(blobs),
            tax_id_store=lambda _: tax_id_store,
            tax_id_cipher=lambda _: tax_id_cipher,
            client_binding_store=lambda _f, _c: client_binding_store,
        ),
    )
    if code != 0:
        raise SystemExit(f"seeding seeds/dev.json failed with exit code {code}")

    clients = STACK["clientIds"]
    api_config = load_api_config(
        {
            "INSOLVIA_ENV": "local",
            "AUTH_ISSUER_URL": ISSUER_URL,
            "AUTH_CLIENT_ID": clients["app"],
            "AUTH_PORTAL_CLIENT_ID": clients["portal"],
        }
    )
    api = create_api_app(
        ApiDependencies(
            config=api_config,
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=CognitoJwksProvider(ISSUER_URL),
            case_store=case_store,
            access_log=access_log,
            firm_store=firm_store,
            user_directory=user_directory,
            document_store=document_store,
            document_blobs=blobs,
            debtor_store=debtor_store,
            tax_id_store=tax_id_store,
            tax_id_cipher=tax_id_cipher,
            case_entity_store=case_entity_store,
            job_store=MemoryJobStore(),
            job_queue=MemoryJobQueue(),
            packet_store=MemoryPacketStore(case_store),
            candidate_store=candidate_store,
            event_store=MemoryEventStore(),
            calendar_token_store=MemoryCalendarTokenStore(),
            task_store=MemoryTaskStore(),
            document_request_store=MemoryDocumentRequestStore(),
            client_binding_store=client_binding_store,
            filing_credential_store=MemoryFilingCredentialStore(),
            filing_credential_sealer=LocalCredentialSealer(),
            filing_authorization_store=MemoryFilingAuthorizationStore(),
            filing_approval_store=MemoryFilingApprovalStore(),
            filing_queue=MemoryFilingQueue(),
            filing_store=MemoryFilingStore(case_store),
        )
    )

    admin = create_admin_app(
        AdminDependencies(
            config=load_admin_config(
                {"INSOLVIA_ENV": "local", "GOOGLE_CLIENT_ID": clients["google"]}
            ),
            # The admin dev server's own composition, with the keys served
            # locally instead of from googleapis.com.
            jwks_provider=CognitoJwksProvider(
                GOOGLE_ISSUERS[0], jwks_url=GOOGLE_JWKS_URL
            ),
            firm_store=firm_store,
            user_directory=user_directory,
            audit_log=MemoryAuditLog(),
        )
    )

    mcp = create_asgi_app(
        McpDependencies(
            config=load_mcp_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER_URL,
                    "AUTH_CLIENT_IDS": clients["mcp"],
                    "MCP_RESOURCE_URL": f"http://{HOST}:{MCP_PORT}/mcp",
                }
            ),
            case_store=case_store,
            case_entity_store=case_entity_store,
            debtor_store=debtor_store,
            document_store=document_store,
            candidate_store=candidate_store,
            access_log=access_log,
            firm_store=firm_store,
            jwks_provider=CognitoJwksProvider(ISSUER_URL),
        )
    )

    aux = ThreadingHTTPServer((HOST, AUX_PORT), _aux_handler(jwks, blobs))
    threading.Thread(target=aux.serve_forever, daemon=True).start()
    for app, port in ((api, API_PORT), (admin, ADMIN_PORT)):
        server = make_server(HOST, port, app, threaded=True)
        threading.Thread(target=server.serve_forever, daemon=True).start()
    print(
        f"contract stack: api :{API_PORT} admin :{ADMIN_PORT} "
        f"mcp :{MCP_PORT} aux :{AUX_PORT}",
        flush=True,
    )
    uvicorn.run(mcp, host=HOST, port=MCP_PORT, log_level="warning")


if __name__ == "__main__":
    main()
