"""ADR 0024 PR 8 on the worker's side: a filing that reaches `filed` files
the CASE in the same write — `status=filed`, the court's number and the
petition date, a history row naming the filing — once, under redelivery and
crashes, without overwriting what a person recorded, and never silently
when it cannot."""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.cases import FILING_WORKER_ACTOR, case_item
from insolvia_filing.core.drivers.base import CourtConfirmation
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver


def test_the_reference_case_is_filed_with_the_courts_number_and_date(filing):
    approval, body = filing.approve()
    before = filing.api.case()

    assert filing.run(body).outcome == "filed"

    stored = filing.filing(approval)
    case = filing.api.case()
    confirmation = stored.confirmation
    assert confirmation is not None
    assert case.status == "filed"
    assert case.case_number == confirmation.case_number
    assert case.filed_at == confirmation.filed_at[:10]
    # The number the notice matcher keys on (#369).
    assert case_item(case)["caseNumberKey"] == f"flmb:{confirmation.case_number}"
    # The pins are what the approved packet used, and stay so.
    assert case.form_revisions == before.form_revisions
    assert case.constants_set_id == before.constants_set_id


def test_the_move_is_a_lifecycle_transition_with_its_history_row(filing):
    approval, body = filing.approve()
    before = filing.api.case().status

    filing.run(body)

    history = filing.api.deps.case_store.status_history(filing.case_id)
    assert [(h.from_status, h.to_status) for h in history] == [(before, "filed")]
    assert history[0].changed_by == FILING_WORKER_ACTOR
    assert history[0].filing_id == approval.filing_id
    assert history[0].changed_at == filing.api.case().updated_at
    assert ("filing.submit", "allowed", "case_filed") in filing.actions()


def test_once_filed_the_filing_set_refuses_a_new_approval(filing):
    _, body = filing.approve()

    filing.run(body)

    assert "filed" in filing.api.basis().blockers


def test_a_redelivery_after_filing_changes_nothing(filing):
    _, body = filing.approve()
    filing.run(body)
    case = filing.api.case()

    assert filing.run(body).outcome == "noop"
    assert filing.api.case() == case
    assert len(filing.api.deps.case_store.status_history(filing.case_id)) == 1


class Crash(BaseException):
    """A process dying between two writes."""


class CrashOnFiled:
    """The record store, crashing the FIRST time the run tries to file — the
    receipt is stored, the transaction never sent."""

    def __init__(self, inner) -> None:
        self.inner = inner
        self.crashed = False

    def get(self, case_id, filing_id):
        return self.inner.get(case_id, filing_id)

    def claim(self, filing):
        return self.inner.claim(filing)

    def resolve(self, filing, **kwargs):
        return self.inner.resolve(filing, **kwargs)

    def transition(self, filing, **kwargs):
        if filing.state == "filed" and not self.crashed:
            self.crashed = True
            raise Crash
        return self.inner.transition(filing, **kwargs)


def test_a_crash_between_the_receipt_and_the_case_is_finished_once_on_redelivery(
    filing,
):
    approval, body = filing.approve()
    inner = filing.filings
    filing.filings = CrashOnFiled(inner)
    with pytest.raises(Crash):
        filing.run(body)
    assert inner.get(filing.case_id, approval.filing_id).state == "submitted"
    assert filing.api.case().status != "filed"

    redelivered = filing.run(body)

    assert redelivered.outcome == "filed"
    assert filing.api.case().status == "filed"
    assert len(filing.documents.list_for_case(filing.case_id)) == 1
    assert len(filing.api.deps.case_store.status_history(filing.case_id)) == 1
    assert filing.court.submissions == 1


def test_a_case_already_recorded_by_hand_is_left_as_it_was_written(filing):
    approval, body = filing.approve()
    store = filing.api.deps.case_store
    by_hand = replace(
        store.cases[filing.case_id],
        status="filed",
        case_number="6:26-bk-99999-XYZ",
        filed_at="2099-01-14",
    )
    # A person PATCHes the case filed while the run is between its approval
    # and its capture.
    original = filing.filings.transition

    def transition(record, **kwargs):
        if record.state == "submitted":
            store.cases[filing.case_id] = by_hand
        return original(record, **kwargs)

    filing.filings.transition = transition

    assert filing.run(body).outcome == "filed"
    assert filing.filing(approval).state == "filed"
    assert store.cases[filing.case_id] == by_hand
    assert store.status_history(filing.case_id) == ()


def test_a_case_that_moves_under_the_write_is_read_again(filing):
    _, body = filing.approve()
    store = filing.api.deps.case_store
    original = filing.filings.transition
    moved = []

    def transition(record, **kwargs):
        if kwargs.get("filed_case") is not None and not moved:
            # Somebody moves the status between the worker's read and its
            # write: the first transaction fails on the case's condition.
            current = store.cases[filing.case_id]
            other = "intake" if current.status == "ready_to_file" else "ready_to_file"
            store.cases[filing.case_id] = replace(current, status=other)
            moved.append(other)
        return original(record, **kwargs)

    filing.filings.transition = transition

    assert filing.run(body).outcome == "filed"
    history = store.status_history(filing.case_id)
    assert [(h.from_status, h.to_status) for h in history] == [(moved[0], "filed")]


class Misreading:
    """A driver whose confirmation's case number cannot be read whole."""

    def __init__(self, inner: FakeCmEcfDriver, number: str) -> None:
        self.inner = inner
        self.number = number

    @property
    def driver_id(self):
        return self.inner.driver_id

    @property
    def base_urls(self):
        return self.inner.base_urls

    @property
    def case_upload(self):
        return self.inner.case_upload

    def start(self, http):
        session = self.inner.start(http)
        number = self.number

        class Session:
            def sign_in(self, *args):
                session.sign_in(*args)

            def open_case(self, opening):
                session.open_case(opening)

            def upload(self, files):
                session.upload(files)

            def final_submit(self) -> CourtConfirmation:
                return replace(session.final_submit(), case_number=number)

        return Session()


@pytest.mark.parametrize("number", ["Case pending", "26-10000"])
def test_a_number_that_cannot_be_read_whole_is_left_for_the_attorney(filing, number):
    approval, body = filing.approve()
    base = filing.court.base_url
    filing.driver = lambda: Misreading(FakeCmEcfDriver(base), number)

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("outcome_unknown", "case_not_recorded")
    stored = filing.filing(approval)
    # The court's word is kept — and with it, "not filed" is refused later.
    assert stored.confirmation is not None
    assert stored.confirmation.case_number == number
    assert filing.api.case().status != "filed"
    assert filing.court.submissions == 1
