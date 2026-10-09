"""Why a filing stopped, and what the attorney does next — in words.

ADR 0024: "Failure stops and hands back ... It stores what it saw, moves the
filing to `handed_back` (or `outcome_unknown`), and tells the attorney where
it stopped and what the court said. The attorney finishes in their own
CM/ECF session from the same filing set and checklist the hand-off path
always produced."

Every reason the worker can stop for is a key here, so the record can never
carry a reason nobody wrote an instruction for (tests/unit/test_hand_back.py
walks every `HandBackError` the worker and the drivers raise). Each `action`
ends at the filing-set checklist (`link = "packet"`, the packet screen).

Two families, and the difference is the one that matters:

- HAND-BACK reasons — the run stopped BEFORE the final submit. Nothing
  reached the court's docket; the attorney files from the checklist.
- OUTCOME-UNKNOWN reasons — the run stopped AFTER writing `at_final_submit`.
  The court may have docketed the filing. Never retried: the attorney (or
  the worker, once a driver can) looks the debtor up on the court's own
  query FIRST, and files only if the case is not there.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Final

from insolvia_core.filings import HandBackNote

_FROM_CHECKLIST: Final = (
    " Nothing reached the court. File the case from the packet screen's"
    " checklist in your own CM/ECF session, then record the court's case"
    " number on this filing — or record that you have not filed it, which"
    " lets you approve a new filing."
)


@dataclass(frozen=True)
class Reason:
    title: str
    action: str


HAND_BACK_REASONS: Final[Mapping[str, Reason]] = {
    "court_not_verified": Reason(
        "Insolvia does not file in this court yet",
        "No filing driver has been verified on this court's training database,"
        " so Insolvia prepares the case and you file it." + _FROM_CHECKLIST,
    ),
    "host_not_allowed": Reason(
        "The court's address is outside what this environment may reach",
        "The worker refused before opening your login." + _FROM_CHECKLIST,
    ),
    "submissions_disabled": Reason(
        "Automated filing is switched off",
        "Insolvia's filing kill switch is off in this environment; nothing was"
        " opened or sent." + _FROM_CHECKLIST,
    ),
    "approval_unavailable": Reason(
        "The approval could not be used",
        "It had expired, been cancelled, been used, or the filing set changed"
        " after you approved it. Review the filing set and approve again, or"
        " file it yourself." + _FROM_CHECKLIST,
    ),
    "credential_unavailable": Reason(
        "Your court login could not be used",
        "It was revoked, or your filing authorization was withdrawn, before the"
        " filing went in." + _FROM_CHECKLIST,
    ),
    "outside_document_required": Reason(
        "A document prepared outside Insolvia must go in with the petition",
        "The court takes it as part of the opening filing and Insolvia does not"
        " hold it." + _FROM_CHECKLIST,
    ),
    "case_upload_invalid": Reason(
        "The Case Upload file could not be built from this case",
        "Debtor.txt failed the court's specification (or a fact it needs — a"
        " tax id, the county, the division's office code — is missing), so the"
        " worker stopped before signing in. Nothing reached the court."
        + _FROM_CHECKLIST,
    ),
    "document_mismatch": Reason(
        "A stored document no longer matches what you approved",
        "Its bytes do not have the digest the approval was bound to; nothing"
        " was filed. Re-assemble the packet and review it again.",
    ),
    "changed_after_approval": Reason(
        "The filing set changed while it was being filed",
        "The worker re-checked the approval's digest immediately before the"
        " final submit, and the case no longer matches it. Review the filing"
        " set and approve again, or file it yourself." + _FROM_CHECKLIST,
    ),
    "sign_in_failed": Reason(
        "The court refused the sign-in",
        "Check your PACER login and password, update them in Insolvia, and"
        " approve again." + _FROM_CHECKLIST,
    ),
    "mfa_rejected": Reason(
        "The court refused the authenticator code",
        "Re-enrol your PACER authenticator seed in Insolvia, then approve"
        " again." + _FROM_CHECKLIST,
    ),
    "unrecognised_screen": Reason(
        "The court showed a screen Insolvia does not recognise",
        "The worker stopped rather than guess." + _FROM_CHECKLIST,
    ),
    "court_message": Reason(
        "The court stopped the filing with a message",
        "Read what the court said, below." + _FROM_CHECKLIST,
    ),
    "duplicate_case": Reason(
        "The court reports a possible duplicate case for this debtor",
        "Check the court's records for an existing case before filing"
        " anything." + _FROM_CHECKLIST,
    ),
    "court_error": Reason(
        "The court's system returned an error",
        "Nothing was submitted." + _FROM_CHECKLIST,
    ),
    "court_timeout": Reason(
        "The court did not answer in time",
        "The worker gave up before the final submit." + _FROM_CHECKLIST,
    ),
    "upload_mismatch": Reason(
        "The court's list of uploaded documents does not match the filing set",
        "The worker stopped before the final submit." + _FROM_CHECKLIST,
    ),
    "interrupted": Reason(
        "The filing run was interrupted before the final submit",
        "The worker stopped (a crash or a timeout) before anything was"
        " submitted, and a redelivered job never restarts a filing." + _FROM_CHECKLIST,
    ),
    "worker_error": Reason(
        "The filing worker hit an unexpected error before the final submit",
        "It stopped rather than retry." + _FROM_CHECKLIST,
    ),
}

_RECONCILE: Final = (
    " Look the debtor up on the court's own case query before doing anything"
    " else, and record what it shows on this filing: the case number if the"
    " case is there; that it is not, only once you have checked — that is"
    " what lets a new filing be approved."
)

OUTCOME_UNKNOWN_REASONS: Final[Mapping[str, Reason]] = {
    "submit_timeout": Reason(
        "The court did not answer the final submit",
        "The filing may or may not have been docketed." + _RECONCILE,
    ),
    "submit_error": Reason(
        "The final submit failed in a way that does not say what the court did",
        "The filing may or may not have been docketed." + _RECONCILE,
    ),
    "receipt_missing": Reason(
        "The court's confirmation had no case number",
        "The filing may have been docketed without a confirmation Insolvia"
        " could read." + _RECONCILE,
    ),
    "unrecognised_confirmation": Reason(
        "The court answered the final submit with a screen Insolvia does not recognise",
        "The filing may or may not have been docketed." + _RECONCILE,
    ),
    "interrupted_after_submit_mark": Reason(
        "The filing run stopped at the final submit",
        "The worker had marked the filing as about to submit, so it cannot know"
        " whether the court received it, and it never submits twice." + _RECONCILE,
    ),
    "capture_interrupted": Reason(
        "The court confirmed the filing, but the receipt could not be stored",
        "The case number below is the court's own." + _RECONCILE,
    ),
    "case_not_recorded": Reason(
        "The court confirmed the filing, but the case could not be marked filed",
        "The court's confirmation is stored with the filing, but its case"
        " number or date could not be read, or the case changed while it was"
        " being recorded. The case number below is the court's own." + _RECONCILE,
    ),
}


def hand_back_note(
    reason: str, *, stage: str, court_said: str | None = None
) -> HandBackNote:
    """The stored note for a hand-back. An unknown reason is a programming
    error, caught by the catalogue test rather than at a court."""
    entry = HAND_BACK_REASONS[reason]
    return HandBackNote(
        reason=reason,
        stage=stage,
        title=entry.title,
        action=entry.action,
        court_said=court_said,
    )


def outcome_unknown_note(
    reason: str, *, stage: str = "at_final_submit", court_said: str | None = None
) -> HandBackNote:
    """The stored note for an `outcome_unknown` stop — the same shape as a
    hand-back's, so the attorney's screen reads one kind of record; its
    action is always to reconcile first."""
    entry = OUTCOME_UNKNOWN_REASONS[reason]
    return HandBackNote(
        reason=reason,
        stage=stage,
        title=entry.title,
        action=entry.action,
        court_said=court_said,
    )
