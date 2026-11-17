"""The attorney's answer to a hand-back (ADR 0024 PR 8, core/filing_outcome.py)
and its route, `POST /v1/cases/<id>/filings/<filing_id>/resolution`.

The filing records here are written the way the filing worker leaves them —
claimed under the approval, then `handed_back` or `outcome_unknown` — into
the same in-memory table the case lives in, so a `filed` resolution files the
case in the one write the DynamoDB adapter makes. Every identifier is fake;
every date is far-future.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.api.routes import filing_approval as routes
from insolvia_core.cases import case_item
from insolvia_core.documents import STATUS_STORED, Document
from insolvia_core.filings import Confirmation, Filing, HandBackNote, Step

from tests.unit.test_filing_approval import (
    ATTORNEY,
    CASE_ID,
    COLLEAGUE,
    NOW,
    make_world,
)
from tests.unit.test_filing_approval_routes import App
from tests.unit.test_filing_credential_routes import auth

LEASE = int(NOW) - 60  # the attempt's lease ran out a minute ago
NUMBER = "6:99-bk-10000"
NOTE = HandBackNote("court_not_verified", "claimed", "t", "a")


@pytest.fixture
def app(monkeypatch) -> App:
    # The resolution happens the day after the far-future approval.
    monkeypatch.setattr(routes, "resolution_clock", lambda: NOW + 86400)
    return App(make_world())


def handed_back(app: App, *, state: str = "handed_back", **changes) -> Filing:
    """Approve, consume, and leave the filing as the worker would."""
    approval = app.world.approve()
    app.world.consume(approval)
    filing = Filing(
        filing_id=approval.filing_id,
        case_id=CASE_ID,
        approval_id=approval.approval_id,
        firm_id=approval.firm_id,
        attorney_id=approval.attorney_id,
        credential_id=approval.credential_id,
        court=approval.court,
        division=approval.division,
        state=state,  # type: ignore[arg-type]
        attempt_id="attempt-1",
        claimed_at="2099-01-13T12:00:00.000Z",
        lease_expires_at=LEASE,
        updated_at="2099-01-13T12:00:00.000Z",
        history=(Step("claimed", "2099-01-13T12:00:00.000Z"), Step(state, "x")),
        hand_back=NOTE,
        **changes,
    )
    assert app.world.filings.claim(filing)
    return filing


def resolve(app: App, filing: Filing, subject: str = ATTORNEY, **body):
    payload = {"docket_checked": True, **body}
    return app.client.post(
        f"/v1/cases/{CASE_ID}/filings/{filing.filing_id}/resolution",
        json=payload,
        headers=auth(subject),
    )


def filed_body(**changes):
    return {
        "outcome": "filed",
        "case_number": NUMBER + "-ABC",
        "filed_at": "2099-01-14",
        **changes,
    }


def denials(app: App) -> list[str | None]:
    return [
        e.purpose
        for e in app.world.log.events
        if e.action == "filing.resolve" and e.outcome == "denied"
    ]


# ── The screen sees the filing ─────────────────────────────────


def test_the_approval_screen_carries_the_filing_record(app):
    filing = handed_back(app)

    body = app.read().get_json()

    assert body["filing"]["filingId"] == filing.filing_id
    assert body["filing"]["state"] == "handed_back"
    assert body["filing"]["resolvable"] is True
    assert body["filing"]["handBack"]["reason"] == "court_not_verified"


# ── "Filed by me" ──────────────────────────────────────────────


def test_filed_by_me_files_the_case_with_its_number_and_history(app):
    filing = handed_back(app)

    response = resolve(app, filing, **filed_body())

    assert response.status_code == 200, response.get_json()
    case = app.world.case()
    assert case.status == "filed"
    assert case.case_number == NUMBER + "-ABC"
    assert case.filed_at == "2099-01-14"
    assert case_item(case)["caseNumberKey"] == f"flmb:{NUMBER}"
    history = app.world.deps.case_store.status_history(CASE_ID)
    assert history[-1].to_status == "filed"
    assert history[-1].changed_by == ATTORNEY
    assert history[-1].filing_id == filing.filing_id
    record = response.get_json()["filing"]
    assert record["resolvable"] is False
    assert record["resolution"]["outcome"] == "filed"
    assert record["resolution"]["docketCheckedAt"]
    assert ("filing.resolve", "allowed", "filed") in [
        (e.action, e.outcome, e.purpose) for e in app.world.log.events
    ]


def test_once_filed_the_pins_freeze_and_nothing_is_approved_again(app):
    filing = handed_back(app)
    pins = app.world.case().form_revisions
    resolve(app, filing, **filed_body())

    basis = app.read().get_json()["basis"]
    assert "filed" in basis["blockers"]
    assert app.approve().status_code == 409
    assert app.world.case().form_revisions == pins


def test_a_confirmation_document_must_be_a_stored_document_of_the_case(app):
    filing = handed_back(app)
    response = resolve(
        app,
        filing,
        **filed_body(confirmation_document_id="00000000-0000-4000-8000-0000000000d9"),
    )
    assert response.status_code == 400
    assert "confirmation_document_id" in response.get_json()["fields"]
    assert app.world.case().status != "filed"


def test_an_uploaded_notice_is_recorded_with_the_resolution(app):
    filing = handed_back(app)
    notice = Document(
        id="00000000-0000-4000-8000-0000000000d2",
        case_id=CASE_ID,
        kind="court_notice",
        file_name="notice.pdf",
        content_type="application/pdf",
        byte_size=10,
        storage_ref=f"cases/{CASE_ID}/00000000-0000-4000-8000-0000000000d2",
        uploaded_by=ATTORNEY,
        uploaded_at="2099-01-14T09:00:00.000000Z",
        status=STATUS_STORED,
    )
    app.world.documents.create(notice)

    response = resolve(app, filing, **filed_body(confirmation_document_id=notice.id))

    assert response.status_code == 200, response.get_json()
    assert (
        response.get_json()["filing"]["resolution"]["confirmationDocumentId"]
        == notice.id
    )


@pytest.mark.parametrize(
    ("changes", "field"),
    [
        ({"case_number": "pending"}, "case_number"),
        ({"case_number": "99-10000"}, "case_number"),  # no office
        ({"filed_at": "16/01/2099"}, "filed_at"),
        ({"filed_at": "2099-02-20"}, "filed_at"),  # the future
        ({"filed_at": "2099-01-01"}, "filed_at"),  # before the approval
    ],
)
def test_a_filed_resolution_needs_a_real_number_and_date(app, changes, field):
    filing = handed_back(app)
    response = resolve(app, filing, **filed_body(**changes))
    assert response.status_code == 400
    assert field in response.get_json()["fields"]
    assert app.world.case().status != "filed"


def test_the_docket_check_must_be_confirmed(app):
    filing = handed_back(app)
    response = app.client.post(
        f"/v1/cases/{CASE_ID}/filings/{filing.filing_id}/resolution",
        json=filed_body(),
        headers=auth(ATTORNEY),
    )
    assert response.status_code == 400
    assert "docket_checked" in response.get_json()["fields"]


def test_the_courts_confirmation_decides_the_number(app):
    filing = handed_back(
        app,
        state="outcome_unknown",
        confirmation=Confirmation(NUMBER, "2099-01-14T10:00:00-05:00", ()),
    )
    wrong = resolve(app, filing, **filed_body(case_number="6:99-bk-10001"))
    assert wrong.status_code == 400
    assert NUMBER in wrong.get_json()["fields"]["case_number"]

    right = resolve(app, filing, **filed_body(case_number=NUMBER))
    assert right.status_code == 200


# ── "Not filed" ────────────────────────────────────────────────


def test_not_filed_frees_the_case_for_a_new_approval(app):
    filing = handed_back(app)
    assert app.approve().status_code == 409  # unresolved: still in the way
    assert "filing_unresolved" in [
        e.purpose
        for e in app.world.log.events
        if e.action == "filing.approve" and e.outcome == "denied"
    ]

    response = resolve(app, filing, outcome="not_filed")

    assert response.status_code == 200, response.get_json()
    assert app.world.case().status != "filed"
    again = app.approve()
    assert again.status_code == 201, again.get_json()
    assert again.get_json()["approval"]["filingId"] != filing.filing_id


def test_an_unknown_outcome_with_the_courts_confirmation_is_never_not_filed(app):
    filing = handed_back(
        app,
        state="outcome_unknown",
        confirmation=Confirmation(NUMBER, "2099-01-14", ()),
    )
    response = resolve(app, filing, outcome="not_filed")
    assert response.status_code == 409
    assert response.get_json()["reason"] == "court_confirmed"
    assert denials(app) == ["court_confirmed"]
    assert app.approve().status_code == 409


def test_an_unknown_outcome_waits_out_the_attempts_lease(app):
    filing = handed_back(app, state="outcome_unknown")
    app.world.filings._items[(CASE_ID, filing.filing_id)]["leaseExpiresAt"] = (
        4102444800  # 2100-01-01: the attempt may still be waiting on the court
    )
    response = resolve(app, filing, outcome="not_filed")
    assert response.status_code == 409
    assert response.get_json()["reason"] == "attempt_may_be_live"


def test_an_unknown_outcome_past_its_lease_and_checked_may_be_not_filed(app):
    filing = handed_back(app, state="outcome_unknown")
    assert resolve(app, filing, outcome="not_filed").status_code == 200


def test_not_filed_carries_no_filing_details(app):
    filing = handed_back(app)
    response = resolve(app, filing, outcome="not_filed", case_number=NUMBER)
    assert response.status_code == 400


# ── Who, and how often ─────────────────────────────────────────


def test_only_the_filings_own_attorney_resolves_it(app):
    filing = handed_back(app)
    response = resolve(app, filing, COLLEAGUE, outcome="not_filed")
    assert response.status_code == 403
    assert denials(app) == ["not_the_attorney"]


def test_a_filing_is_resolved_once(app):
    filing = handed_back(app)
    assert resolve(app, filing, outcome="not_filed").status_code == 200
    second = resolve(app, filing, **filed_body())
    assert second.status_code == 409
    assert second.get_json()["reason"] == "not_resolvable"


@pytest.mark.parametrize("state", ["claimed", "submitted", "filed"])
def test_only_a_hand_back_or_unknown_outcome_is_resolvable(app, state):
    filing = handed_back(app, state=state)
    response = resolve(app, filing, outcome="not_filed")
    assert response.status_code == 409


def test_an_unknown_filing_is_not_found(app):
    handed_back(app)
    response = app.client.post(
        f"/v1/cases/{CASE_ID}/filings/00000000-0000-4000-8000-00000000ffff/resolution",
        json=filed_body(docket_checked=True),
        headers=auth(ATTORNEY),
    )
    assert response.status_code == 404


def test_a_case_that_moved_under_the_resolution_writes_nothing(app):
    filing = handed_back(app)
    store = app.world.deps.case_store
    original = app.world.filings.resolve

    def racing(record, **kwargs):
        current = store.cases[CASE_ID]
        store.cases[CASE_ID] = replace(
            current,
            status="intake" if current.status == "ready_to_file" else "ready_to_file",
        )
        return original(record, **kwargs)

    app.world.filings.resolve = racing  # type: ignore[method-assign]
    response = resolve(app, filing, **filed_body())
    assert response.status_code == 409
    assert app.world.filings.get(CASE_ID, filing.filing_id).resolution is None  # type: ignore[union-attr]
