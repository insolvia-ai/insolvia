"""The whole worker against the fake CM/ECF — ADR 0024 PR 7's done-when, in
the unit tier:

  1. THE REFERENCE CASE FILES END TO END: approved by its attorney (the API's
     own approve), consumed, signed in with a real TOTP, uploaded, re-checked,
     marked, submitted once, and its receipt stored with the case.
  2. EVERY FAULT MODE ends `handed_back` or `outcome_unknown`, with a stored
     reason and an action pointing at the filing-set checklist.
  3. NEVER A SECOND FILING: a redelivered message, a crash after the final
     submit, a crash before it, and two consumers racing each leave the fake
     with at most ONE submission.
"""

from __future__ import annotations

import threading

import pytest
from fake_cmecf import FAULTS
from insolvia_core.filings import STATES
from insolvia_filing.core.drivers.base import (
    CourtConfirmation,
    CourtDriver,
    CourtSession,
)
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver

# ── 1. end to end ───────────────────────────────────────────────


def test_the_reference_case_files_end_to_end_against_the_fake(filing):
    approval, body = filing.approve()

    result = filing.run(body)

    stored = filing.filing(approval)
    assert result.outcome == "filed"
    assert filing.court.submissions == 1
    assert [step.state for step in stored.history] == [
        "claimed",
        "signed_in",
        "uploading",
        "at_final_submit",
        "submitted",
        "filed",
    ]
    assert stored.confirmation is not None
    assert (
        stored.confirmation.case_number == filing.court.state_json()["caseNumbers"][0]
    )
    assert stored.confirmation.docket_entries
    assert stored.confirmation.page_ref is not None
    assert stored.follow_ups[0] == "pay_fee"
    assert stored.driver == "fake-cmecf/1"


def test_the_receipt_and_the_courts_page_are_stored_with_the_case(filing):
    approval, body = filing.approve()

    filing.run(body)

    stored = filing.filing(approval)
    receipt = filing.documents.get(filing.case_id, stored.receipt_document_id)
    assert receipt is not None
    assert receipt.kind == "court_notice"
    assert receipt.status == "stored"
    assert receipt.content_type == "application/pdf"
    pdf = filing.api.deps.blobs.get_bytes(receipt.storage_ref)
    assert pdf is not None
    assert pdf.startswith(b"%PDF")
    page = filing.api.deps.blobs.get_bytes(stored.confirmation.page_ref)
    assert page is not None
    assert stored.confirmation.case_number.encode() in page
    prefix = f"cases/{filing.case_id}/filings/{approval.filing_id}/"
    assert receipt.storage_ref.startswith(prefix)
    assert stored.confirmation.page_ref.startswith(prefix)


def test_every_document_the_court_received_is_the_approved_bytes(filing):
    _, body = filing.approve()
    packet_files = [
        d for d in filing.api.basis().filing_set.documents if d.source == "packet"
    ]

    filing.run(body)

    assert filing.court.uploads == len(
        [d for d in packet_files if d.handling != "not_filed"]
    )


def test_the_run_is_recorded_in_the_access_log(filing):
    _, body = filing.approve()

    filing.run(body)

    actions = filing.actions()
    assert ("filing.consume", "allowed", None) in actions
    assert ("credential.open", "allowed", "sign_in") in actions
    assert ("credential.open", "allowed", "final_submit_recheck") in actions
    assert ("filing.submit", "allowed", "submitted") in actions
    assert ("case.read", "allowed", "filing_digest") in actions


# ── 2. every fault mode stops ───────────────────────────────────

EXPECTED = {
    "bad_login": ("handed_back", "sign_in_failed", 0),
    "totp_rejected": ("handed_back", "mfa_rejected", 0),
    "slow": ("handed_back", "court_timeout", 0),
    "upload_500": ("handed_back", "court_error", 0),
    "duplicate_case": ("handed_back", "duplicate_case", 0),
    "unexpected_screen": ("handed_back", "unrecognised_screen", 0),
    "timeout_after_submit": ("outcome_unknown", "submit_timeout", 1),
    "receipt_missing": ("outcome_unknown", "receipt_missing", 1),
}


def test_every_fault_mode_has_an_expected_outcome():
    assert set(EXPECTED) == set(FAULTS) - {"none"}


@pytest.mark.parametrize("fault", sorted(EXPECTED))
def test_a_fault_mode_ends_handed_back_or_outcome_unknown(filing, fault):
    state, reason, submissions = EXPECTED[fault]
    filing.court.set_fault(fault)
    approval, body = filing.approve()

    result = filing.run(body)

    stored = filing.filing(approval)
    assert (result.outcome, result.reason) == (state, reason)
    assert stored.state == state
    assert stored.hand_back is not None
    assert stored.hand_back.reason == reason
    assert stored.hand_back.link == "packet"
    assert stored.hand_back.action
    assert filing.court.submissions == submissions


@pytest.mark.parametrize("fault", sorted(EXPECTED))
def test_a_fault_mode_redelivered_never_submits_again(filing, fault):
    _, _, submissions = EXPECTED[fault]
    filing.court.set_fault(fault)
    _, body = filing.approve()
    filing.run(body)
    filing.court.set_fault("none")

    again = filing.run(body)

    assert again.outcome == "noop"
    assert filing.court.submissions == submissions


# ── 3. never a second filing ────────────────────────────────────


def test_a_redelivered_job_after_filing_is_a_noop(filing):
    _, body = filing.approve()
    filing.run(body)

    again = filing.run(body)

    assert again.outcome == "noop"
    assert filing.court.submissions == 1


class Crash(BaseException):
    """A process dying — not an Exception, so nothing in the worker catches
    it, exactly as nothing catches a killed Lambda."""


class CrashingSession:
    def __init__(self, inner: CourtSession, at: str) -> None:
        self._inner = inner
        self._at = at

    def sign_in(self, login, password, totp):
        self._inner.sign_in(login, password, totp)

    def open_case(self, opening):
        self._inner.open_case(opening)

    def upload(self, files):
        self._inner.upload(files)
        if self._at == "after_upload":
            raise Crash

    def final_submit(self) -> CourtConfirmation:
        confirmation = self._inner.final_submit()
        if self._at == "after_submit":
            raise Crash
        return confirmation


class CrashingDriver:
    def __init__(self, inner: CourtDriver, at: str) -> None:
        self._inner = inner
        self._at = at

    @property
    def driver_id(self):
        return self._inner.driver_id

    @property
    def base_urls(self):
        return self._inner.base_urls

    def start(self, http):
        return CrashingSession(self._inner.start(http), self._at)


def crash(filing, at: str):
    base = filing.court.base_url
    filing.driver = lambda: CrashingDriver(FakeCmEcfDriver(base), at)


def test_a_crash_after_the_final_submit_ends_outcome_unknown_and_never_resubmits(
    filing,
):
    approval, body = filing.approve()
    crash(filing, "after_submit")
    with pytest.raises(Crash):
        filing.run(body)
    assert filing.filing(approval).state == "at_final_submit"
    filing.driver = None

    redelivered = filing.run(body)

    assert (redelivered.outcome, redelivered.reason) == (
        "outcome_unknown",
        "interrupted_after_submit_mark",
    )
    assert filing.run(body).outcome == "noop"
    assert filing.court.submissions == 1


def test_a_crash_before_the_mark_waits_out_the_lease_then_hands_back(filing):
    approval, body = filing.approve()
    crash(filing, "after_upload")
    with pytest.raises(Crash):
        filing.run(body)
    filing.driver = None

    within_lease = filing.run(body)
    filing.clock.now += filing.lease_seconds + 1
    after_lease = filing.run(body)

    assert filing.filing(approval).state == "handed_back"
    assert (within_lease.outcome, within_lease.reason) == ("noop", "attempt_in_flight")
    assert (after_lease.outcome, after_lease.reason) == ("handed_back", "interrupted")
    assert filing.court.submissions == 0


def test_two_consumers_racing_one_message_submit_once(filing):
    _, body = filing.approve()
    results = []

    def consume() -> None:
        results.append(filing.run(body))

    threads = [threading.Thread(target=consume) for _ in range(2)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert sorted(r.outcome for r in results) == ["filed", "noop"]
    assert filing.court.submissions == 1


def test_the_states_are_the_adrs():
    assert STATES == (
        "claimed",
        "signed_in",
        "uploading",
        "at_final_submit",
        "submitted",
        "filed",
        "handed_back",
        "outcome_unknown",
    )
