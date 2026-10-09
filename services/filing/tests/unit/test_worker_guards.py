"""The worker's stops before the final submit — each one proved to stop
BEFORE the court sees a submission, and the ones that can, before the
credential is even opened:

  - the kill switch, at the start and again immediately before the submit;
  - a revoked credential (a withdrawn authorization) mid-run;
  - a case edited mid-run (the digest re-check);
  - a court with no verified driver, and a fake composed outside local;
  - a driver whose host the fence does not allow — refused with no socket;
  - an expired approval; a message that is not a filing job, or names no
    approval of its filing;
  - and nothing secret in a log line, a stored record or an access row —
    the court password, the TOTP seed and codes, and the full SSN the Case
    Upload file carries (ADR 0024 PR 9).
"""

from __future__ import annotations

import json
import logging
import re
import socket
import time
from dataclasses import replace

import pytest
from insolvia_core.filing_credentials import revoke_credential
from insolvia_filing.core.drivers.fake import FakeCmEcfDriver
from insolvia_filing.core.totp import totp_at
from insolvia_filing.core.worker import run_filing

# The reference case's tax ids (services/api tests: REFERENCE_TAX_IDS — the
# SSA's advertising block, never issued).
SSNS = ("987654321", "987654322")


class UploadHook:
    """A driver whose upload, once done, runs `hook` — the moment between the
    last upload and the re-check, where a revocation, an edit or a flip of
    the kill switch would land in real life."""

    def __init__(self, base_url: str, hook) -> None:
        self._inner = FakeCmEcfDriver(base_url)
        self._hook = hook

    @property
    def driver_id(self):
        return self._inner.driver_id

    @property
    def base_urls(self):
        return self._inner.base_urls

    @property
    def case_upload(self):
        return self._inner.case_upload

    def start(self, http):
        session = self._inner.start(http)
        upload = session.upload
        hook = self._hook

        def upload_then_hook(files):
            upload(files)
            hook()

        session.upload = upload_then_hook
        return session


def hook_upload(filing, hook) -> None:
    base = filing.court.base_url
    filing.driver = lambda: UploadHook(base, hook)


def opened(filing) -> list[str | None]:
    return [p for a, _, p in filing.actions() if a == "credential.open"]


def test_the_kill_switch_off_hands_back_before_anything_is_opened(filing):
    filing.kill_switch.enabled = False
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "submissions_disabled")
    assert opened(filing) == []
    assert filing.court.submissions == 0


def test_the_kill_switch_is_read_again_before_the_final_submit(filing):
    hook_upload(filing, lambda: setattr(filing.kill_switch, "enabled", False))
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "submissions_disabled")
    assert filing.court.uploads > 0
    assert filing.court.submissions == 0


def test_a_credential_revoked_mid_run_stops_before_the_final_submit(filing):
    def revoke() -> None:
        revoke_credential(
            filing.credential_id,
            firm_id=filing.api.firm_id,
            attorney_id=filing.api.approvals.current(filing.case_id).attorney_id,
            store=filing.api.credentials,
            access_log=filing.api.log,
        )

    hook_upload(filing, revoke)
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "credential_unavailable")
    assert opened(filing) == ["sign_in", "final_submit_recheck"]
    assert filing.court.submissions == 0


def test_a_case_edited_mid_run_stops_before_the_final_submit(filing):
    hook_upload(filing, lambda: filing.api.edit_debtor_phone("555-0100"))
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "changed_after_approval")
    assert filing.court.submissions == 0


def test_a_court_with_no_verified_driver_hands_back_without_opening(filing):
    filing.driver = None
    deps_without_fake = filing.deps()
    _, body = filing.approve()

    result = run_filing(body, replace(deps_without_fake, fake_driver=None))

    assert (result.outcome, result.reason) == ("handed_back", "court_not_verified")
    assert opened(filing) == []


@pytest.mark.parametrize("environment", ["staging", "production"])
def test_a_fake_driver_composed_outside_local_is_refused(filing, environment):
    filing.environment = environment
    _, body = filing.approve()

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "court_not_verified")
    assert opened(filing) == []
    assert filing.court.submissions == 0


def test_a_driver_aimed_outside_the_fence_is_refused_before_any_socket(
    filing, monkeypatch
):
    filing.driver = lambda: FakeCmEcfDriver("https://ecf.flmb.uscourts.gov")
    _, body = filing.approve()

    def no_network(*args, **kwargs):
        raise AssertionError("a socket was opened")

    monkeypatch.setattr(socket, "socket", no_network)
    monkeypatch.setattr(socket, "create_connection", no_network)
    monkeypatch.setattr(socket, "getaddrinfo", no_network)

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "host_not_allowed")
    assert opened(filing) == []


def test_an_expired_approval_is_handed_back_and_never_signs_in(filing):
    _, body = filing.approve()
    filing.clock.now += 3600 + 1

    result = filing.run(body)

    assert (result.outcome, result.reason) == ("handed_back", "approval_unavailable")
    assert opened(filing) == []


@pytest.mark.parametrize(
    "body",
    [
        "not json",
        json.dumps({"kind": "packet.assemble"}),
        json.dumps(
            {
                "kind": "filing.submit",
                "version": 1,
                "approval_id": "00000000-0000-4000-8000-000000000000",
                "filing_id": "00000000-0000-4000-8000-000000000001",
                "case_id": "00000000-0000-4000-8000-000000000002",
            }
        ),
    ],
)
def test_a_message_naming_no_approval_is_dropped_and_writes_nothing(filing, body):
    result = filing.run(body)

    assert result.outcome == "dropped"
    assert filing.filings.items() == []


def test_a_message_whose_filing_id_is_not_its_approvals_is_dropped(filing):
    _, body = filing.approve()
    forged = json.loads(body) | {"filing_id": "00000000-0000-4000-8000-0000000000ff"}

    result = filing.run(json.dumps(forged))

    assert (result.outcome, result.reason) == ("dropped", "no_such_approval")
    assert filing.filings.items() == []


def test_no_secret_reaches_a_log_line_a_record_or_an_access_row(filing, caplog):
    caplog.set_level(logging.DEBUG)
    account = filing.court.account_json()
    _, body = filing.approve()

    filing.run(body)

    codes = {totp_at(account["totp_seed"], time.time() + d) for d in (-30, 0, 30)}
    haystack = "\n".join(
        [
            caplog.text,
            json.dumps(filing.filings.items(), default=str),
            repr(filing.api.log.events),
            json.dumps([vars(e) for e in filing.api.log.events], default=str),
            # What the worker stored with the case: the court's confirmation
            # page and the receipt (the packet's B121 carries the SSN by
            # design and is not the worker's to search).
            *(
                content.decode("latin-1")
                for key, content in filing.api.deps.blobs.contents.items()
                if "/filings/" in key
            ),
        ]
    )
    for secret in (account["password"], account["totp_seed"]):
        assert secret not in haystack
    # The reference debtors' tax ids — in the file the court received
    # (`case_uploads == 1`), and nowhere else.
    assert filing.court.case_uploads == 1
    for digits in SSNS:
        assert digits not in haystack
        assert f"{digits[:3]}-{digits[3:5]}-{digits[5:]}" not in haystack
    for code in codes:
        assert not re.search(rf"\b{code}\b", haystack)
