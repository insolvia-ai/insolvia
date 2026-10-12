"""The Case Upload file in the worker (ADR 0024 PR 9), against the fake court:

  - a court configured for Case Upload receives Debtor.txt FIRST, built from
    the approved case with each debtor's tax id opened through a logged
    `taxid.read` (purpose `case_upload`), and files end to end;
  - a screen-entry-only court receives no Debtor.txt, and no tax id is
    opened at all;
  - a case whose file cannot be built hands back BEFORE the credential is
    opened, with nothing sent to the court;
  - a file the court refuses (a wrong statistics field count — the AO
    spec's own refusal) stops the run with the court's words, before the
    final submit.

The SSN never appearing in a log line, record, access row or stored page is
test_worker_guards.py's `test_no_secret_reaches_...`.
"""

from __future__ import annotations

import time
from dataclasses import replace

import pytest
from fake_cmecf.server import CASE_UPLOAD_WRONG_COUNT, case_upload_refusal
from insolvia_filing.adapters.http.fenced_client import FencedHttpClient
from insolvia_filing.core.drivers.base import CaseOpening, FilingFile, HandBackError
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver
from insolvia_filing.core.fence import fence_for
from insolvia_filing.core.totp import totp_at


def taxid_reads(filing):
    return [
        (e.principal, e.purpose, e.filing_role)
        for e in filing.api.log.events
        if e.action == "taxid.read"
    ]


def opened(filing):
    return [p for a, _, p in filing.actions() if a == "credential.open"]


def test_debtor_txt_is_uploaded_first_and_the_case_files(filing):
    approval, body = filing.approve()

    result = filing.run(body)

    assert result.outcome == "filed"
    assert filing.court.case_uploads == 1
    stored = filing.filing(approval)
    assert stored.confirmation is not None
    assert stored.confirmation.docket_entries[0] == "0 Debtor.txt"
    assert taxid_reads(filing) == [
        (approval.attorney_id, "case_upload", "debtor_1"),
        (approval.attorney_id, "case_upload", "debtor_2"),
    ]
    assert ("case.read", "allowed", "case_upload") in filing.actions()


def test_a_screen_entry_court_gets_no_debtor_txt_and_no_tax_id_is_opened(filing):
    base = filing.court.base_url
    filing.driver = lambda: FakeCmEcfDriver(base, case_upload=False)
    _, body = filing.approve()

    result = filing.run(body)

    assert result.outcome == "filed"
    assert filing.court.case_uploads == 0
    assert taxid_reads(filing) == []


def test_a_file_that_cannot_be_built_hands_back_before_the_login_is_opened(
    filing,
):
    # A debtor whose residence county the district does not serve: field 17
    # cannot land. Edited BEFORE the approval, so the digest is the case's.
    debtors = filing.api.deps.debtor_store
    first = debtors.get(filing.case_id, filing_role="debtor_1")
    assert first is not None
    debtors.put(
        replace(
            first,
            residence_address=replace(first.residence_address, county="Fulton"),
        )
    )
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "case_upload_invalid")
    assert opened(filing) == []
    assert filing.court.uploads == 0
    assert filing.court.submissions == 0


def test_the_fake_court_refuses_a_wrong_field_count_and_the_driver_stops(court):
    account = court.account_json()
    session = FakeCmEcfDriver(court.base_url).start(
        FencedHttpClient(fence_for("local"), timeout=2.0)
    )
    session.sign_in(
        account["login"],
        account["password"],
        lambda: totp_at(account["totp_seed"], time.time()),
    )
    session.open_case(CaseOpening(court="flmb", division="3", chapter=7, joint=False))
    short = b"stat|" + b"|" * 70 + b"\ndebt|db|A||B|||000-00-0000||3|||||||||||||\n"
    with pytest.raises(HandBackError) as stopped:
        session.upload(
            [
                FilingFile(
                    position=0,
                    key="case_upload",
                    file_name="Debtor.txt",
                    handling="case_upload",
                    content=short,
                    sha256="0" * 64,
                )
            ]
        )
    assert stopped.value.reason == "court_message"
    assert "correct number of data items" in (stopped.value.court_said or "")
    assert court.case_uploads == 0
    assert court.submissions == 0


def test_the_fake_courts_own_checks():
    assert case_upload_refusal(b"stat|" + b"|" * 79 + b"\n") is not None  # no debt
    assert case_upload_refusal(b"debt|db\n") == CASE_UPLOAD_WRONG_COUNT
    assert case_upload_refusal("stat|é\n".encode()) is not None
