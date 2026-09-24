"""The forms hub endpoints (issue 13.2 / #343): `GET /v1/cases/<id>/forms`
and `GET /v1/cases/<id>/forms/<form>/preview`.

What matters most, as for every case child resource: what these REFUSE — the
case lookup is the only authorisation there is. Tokens are signed for real,
mirroring tests/test_packet_routes.py. Every identifier below is obviously
fake; this repo is public.
"""

from __future__ import annotations

import io
import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from insolvia_api.adapters.memory.mailer_client import InMemoryMailerClient
from insolvia_api.adapters.memory.waitlist_store import MemoryWaitlistStore
from insolvia_api.api.app_factory import create_app
from insolvia_api.api.dependencies import ApiDependencies
from insolvia_api.core.config import load_config
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.memory.jwks_provider import StaticJwksProvider
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.firms import Firm, FirmUser, default_permissions
from pypdf import PdfReader

ISSUER = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"
CLIENT_ID = "exampleappclientid000000"
FIRM_A = "00000000-0000-4000-8000-00000000f18a"
FIRM_B = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"
KID = "test-key-1"

TYPED = {"source": "staff_typed"}

_PRIVATE_KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
_PUBLIC_KEY = _PRIVATE_KEY.public_key()


def token_for(subject: str) -> str:
    now = int(time.time())
    return jwt.encode(
        {
            "sub": subject,
            "iss": ISSUER,
            "client_id": CLIENT_ID,
            "token_use": "access",
            "iat": now,
            "exp": now + 3600,
        },
        _PRIVATE_KEY,  # type: ignore[arg-type]
        algorithm="RS256",
        headers={"kid": KID},
    )


def auth(subject: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token_for(subject)}"}


def member(subject: str, firm_id: str) -> FirmUser:
    return FirmUser(
        firm_id=firm_id,
        subject=subject,
        email=f"{subject[-4:]}@example.test",
        first_name="Person",
        last_name=subject[-4:],
        role="attorney",
        is_admin=True,
        access_all_cases=True,
        permissions=default_permissions("attorney"),
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


@pytest.fixture
def stores():
    return {
        "case_store": MemoryCaseStore(),
        "debtor_store": MemoryDebtorStore(),
        "entity_store": MemoryCaseEntityStore(),
        "blobs": MemoryDocumentBlobStore(),
        "access_log": MemoryAccessLog(),
    }


@pytest.fixture
def client(stores):
    firms = MemoryFirmStore()
    for firm_id, name in ((FIRM_A, "Example & Partners"), (FIRM_B, "Other Firm LLP")):
        firms.create_firm(
            Firm(
                id=firm_id,
                name=name,
                status="active",
                created_at="2026-01-01T00:00:00.000Z",
                updated_at="2026-01-01T00:00:00.000Z",
            )
        )
    firms.add_user(member(ALICE, FIRM_A))
    firms.add_user(member(BOB, FIRM_B))
    app = create_app(
        ApiDependencies(
            config=load_config(
                {
                    "INSOLVIA_ENV": "local",
                    "AUTH_ISSUER_URL": ISSUER,
                    "AUTH_CLIENT_ID": CLIENT_ID,
                }
            ),
            waitlist_store=MemoryWaitlistStore(),
            mailer=InMemoryMailerClient(),
            jwks_provider=StaticJwksProvider({KID: _PUBLIC_KEY}),
            case_store=stores["case_store"],
            firm_store=firms,
            access_log=stores["access_log"],
            debtor_store=stores["debtor_store"],
            tax_id_store=MemoryTaxIdStore(),
            tax_id_cipher=LocalTaxIdCipher(),
            case_entity_store=stores["entity_store"],
            document_blobs=stores["blobs"],
        )
    )
    return app.test_client()


def open_case(client, subject=ALICE):
    # A real district name, not an abbreviation: rendering a form validates
    # it against the printed dropdown's exact options (form_fill.py), and
    # only a full name like the reference case's is one of them.
    response = client.post(
        "/v1/cases",
        json={"chapter": 7, "district": "Middle District of Florida"},
        headers=auth(subject),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


def add_debtor_1(client, case_id):
    response = client.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        json={"name": {"given": "Ada"}, "provenance": {"name.given": TYPED}},
        headers=auth(ALICE),
    )
    assert response.status_code == 201
    return response.get_json()["id"]


@pytest.mark.parametrize(
    "path",
    [
        "/v1/cases/any-id/forms",
        "/v1/cases/any-id/forms/b101/preview",
    ],
)
def test_every_route_refuses_an_unauthenticated_caller(client, path):
    assert client.get(path).status_code == 401


def test_an_unknown_case_is_not_found(client):
    listing = client.get("/v1/cases/no-such-case/forms", headers=auth(ALICE))
    preview = client.get(
        "/v1/cases/no-such-case/forms/b101/preview", headers=auth(ALICE)
    )
    assert listing.status_code == 404
    assert preview.status_code == 404


def test_another_firms_forms_are_the_same_404_as_no_case(client):
    case_id = open_case(client, ALICE)
    listing = client.get(f"/v1/cases/{case_id}/forms", headers=auth(BOB))
    preview = client.get(f"/v1/cases/{case_id}/forms/b101/preview", headers=auth(BOB))
    assert listing.status_code == 404
    assert preview.status_code == 404


def test_the_static_segments_are_not_shadowed_by_the_collection_routes(client):
    # /v1/cases/<id>/<collection> would match "forms" as a collection name if
    # Werkzeug ranked them the other way; it does not.
    case_id = open_case(client)
    response = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE))
    assert response.status_code == 200
    assert "forms" in response.get_json()


# ── GET /v1/cases/<id>/forms ─────────────────────────────────────


def test_a_bare_case_lists_every_chapter_7_form_all_blocked(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE)).get_json()
    assert len(body["forms"]) > 0
    # No Debtor 1 yet — every row carries at least one problem.
    assert all(row["problems"] for row in body["forms"])
    series_ids = [row["series"] for row in body["forms"]]
    assert series_ids == sorted(set(series_ids), key=series_ids.index)
    assert "form/b101" in series_ids


def test_count_metrics_are_zero_on_an_empty_case(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE)).get_json()
    by_series = {row["series"]: row for row in body["forms"]}
    assert by_series["form/b106ab"]["metric"] == {"kind": "count", "value": "0"}
    assert by_series["form/b106g"]["metric"] == {"kind": "count", "value": "0"}


def test_dollar_metrics_are_zero_on_an_empty_case(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE)).get_json()
    by_series = {row["series"]: row for row in body["forms"]}
    assert by_series["form/b106i"]["metric"] == {"kind": "total", "value": "0"}
    assert by_series["form/b106j"]["metric"] == {"kind": "total", "value": "0"}


def test_forms_with_no_defined_metric_omit_the_key(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE)).get_json()
    by_series = {row["series"]: row for row in body["forms"]}
    assert "metric" not in by_series["form/b101"]


def test_a_problem_names_where_the_fix_belongs(client):
    case_id = open_case(client)
    body = client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE)).get_json()
    b101 = next(row for row in body["forms"] if row["series"] == "form/b101")
    assert all(problem["source"] for problem in b101["problems"])
    assert all(problem["message"] for problem in b101["problems"])


def test_the_read_is_access_logged(client, stores):
    case_id = open_case(client)
    client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE))
    assert any(
        e.action == "case.read" and e.case_id == case_id
        for e in stores["access_log"].events
    )


# ── GET /v1/cases/<id>/forms/<form>/preview ──────────────────────


def test_a_form_this_case_does_not_file_is_not_found(client):
    case_id = open_case(client)
    # B106J-2 files only when Debtor 2 keeps a separate household, and this
    # case has no households recorded at all — packet_form_series skips it.
    response = client.get(
        f"/v1/cases/{case_id}/forms/b106j2/preview", headers=auth(ALICE)
    )
    assert response.status_code == 404


def test_an_unknown_form_key_is_not_found(client):
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/forms/not-a-form/preview", headers=auth(ALICE)
    )
    assert response.status_code == 404


def test_a_blocked_form_answers_200_with_problems_and_no_url(client):
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b101/preview", headers=auth(ALICE)
    )
    assert response.status_code == 200
    body = response.get_json()
    assert body["problems"]
    assert "url" not in body


def test_a_renderable_form_mints_a_short_lived_get(client, stores):
    case_id = open_case(client)
    add_debtor_1(client, case_id)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE)
    )
    assert response.status_code == 200
    body = response.get_json()
    assert body["problems"] == []
    assert body["method"] == "GET"
    assert "expiresAt" in body
    minted = stores["blobs"].minted[-1]
    assert minted.method == "GET"
    assert minted.expires_in == 5 * 60
    assert minted.storage_ref.startswith(f"form-previews/{case_id}/")
    assert minted.storage_ref.endswith(".pdf")


def test_the_rendered_bytes_are_a_pdf(client, stores):
    case_id = open_case(client)
    add_debtor_1(client, case_id)
    client.get(f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE))
    ((storage_ref, content),) = stores["blobs"].contents.items()
    assert storage_ref.startswith(f"form-previews/{case_id}/")
    assert content[:5] == b"%PDF-"


def test_two_previews_of_the_same_form_get_two_distinct_objects(client, stores):
    case_id = open_case(client)
    add_debtor_1(client, case_id)
    client.get(f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE))
    client.get(f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE))
    assert len(stores["blobs"].contents) == 2


def test_the_preview_is_access_logged(client, stores):
    case_id = open_case(client)
    add_debtor_1(client, case_id)
    client.get(f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE))
    assert any(
        e.action == "form_preview.render" and e.case_id == case_id
        for e in stores["access_log"].events
    )


# ── The tax id's full-value read (issue 13.12 / #382) ────────────
# 987-65-4321 is from the SSA's never-issued advertising block.


def add_debtor_1_with_a_tax_id(client, case_id):
    response = client.put(
        f"/v1/cases/{case_id}/debtors/debtor_1",
        json={
            "name": {"given": "Ada"},
            "tax_id": {"kind": "ssn", "value": "987-65-4321"},
            "provenance": {"name.given": TYPED, "tax_id": TYPED},
        },
        headers=auth(ALICE),
    )
    assert response.status_code == 201


def test_previewing_b121_performs_the_logged_read_against_the_caller(client, stores):
    case_id = open_case(client)
    add_debtor_1_with_a_tax_id(client, case_id)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b121/preview", headers=auth(ALICE)
    )
    assert response.status_code == 200
    assert response.get_json()["problems"] == []
    reads = [e for e in stores["access_log"].events if e.action == "taxid.read"]
    assert [(e.principal, e.case_id, e.filing_role, e.purpose) for e in reads] == [
        (ALICE, case_id, "debtor_1", "b121")
    ]
    # The number is on the rendered form — and nowhere in the response.
    ((_, content),) = stores["blobs"].contents.items()
    fields = PdfReader(io.BytesIO(content)).get_fields()
    assert fields is not None
    assert fields["Debtor1a.SSNum"].value == "987-65-4321"
    assert "987" not in str(response.get_json())


def test_previewing_any_other_form_never_opens_the_envelope(client, stores):
    # B106G renders on a bare case (B101 waits on a petition record); the
    # point is the log, not the form: no `taxid.read` for anything but B121.
    # B101's last-four-from-the-record path is pinned by the projection tests.
    case_id = open_case(client)
    add_debtor_1_with_a_tax_id(client, case_id)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b106g/preview", headers=auth(ALICE)
    )
    assert response.status_code == 200
    assert response.get_json()["problems"] == []
    assert not any(e.action == "taxid.read" for e in stores["access_log"].events)


def test_the_hub_listing_never_opens_the_envelope(client, stores):
    case_id = open_case(client)
    add_debtor_1_with_a_tax_id(client, case_id)
    client.get(f"/v1/cases/{case_id}/forms", headers=auth(ALICE))
    assert not any(e.action == "taxid.read" for e in stores["access_log"].events)


# ── Output options (issue 13.11) ─────────────────────────────────


def test_no_query_params_echo_the_default_options(client):
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b101/preview", headers=auth(ALICE)
    )
    assert response.get_json()["options"] == {
        "draftWatermark": False,
        "printDate": False,
        "signaturePages": "all",
        "signElectronically": False,
    }


def test_query_params_are_validated_and_echoed_back(client, stores):
    case_id = open_case(client)
    add_debtor_1(client, case_id)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b106g/preview?draftWatermark=true&printDate=true",
        headers=auth(ALICE),
    )
    assert response.status_code == 200
    body = response.get_json()
    assert body["options"] == {
        "draftWatermark": True,
        "printDate": True,
        "signaturePages": "all",
        "signElectronically": False,
    }
    ((_, content),) = stores["blobs"].contents.items()
    assert "DRAFT" in PdfReader(io.BytesIO(content)).pages[0].extract_text()


def test_an_invalid_signature_pages_query_value_is_a_400(client):
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b101/preview?signaturePages=sideways",
        headers=auth(ALICE),
    )
    assert response.status_code == 400


def test_a_forms_query_param_is_refused_on_the_preview_route(client):
    # The preview already names one form in the URL — a subset selection
    # belongs to packet assembly, not here.
    case_id = open_case(client)
    response = client.get(
        f"/v1/cases/{case_id}/forms/b101/preview?forms=b101",
        headers=auth(ALICE),
    )
    assert response.status_code == 400
