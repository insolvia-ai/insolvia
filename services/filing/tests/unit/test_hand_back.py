"""Every reason the worker or a driver can stop for has words for the
attorney — read off the source, so a new `raise HandBackError("...")` with no
catalogue entry fails here rather than at a court."""

from __future__ import annotations

import ast
from pathlib import Path

from insolvia_filing.core.hand_back import (
    HAND_BACK_REASONS,
    OUTCOME_UNKNOWN_REASONS,
    hand_back_note,
)

PACKAGE = Path(__file__).resolve().parents[2] / "src" / "insolvia_filing"

HAND_BACK_CALLS = {"HandBackError", "_StopError", "hand_back"}
UNKNOWN_CALLS = {"SubmitUncertainError", "outcome_unknown"}


def _reasons(calls: set[str]) -> set[str]:
    found: set[str] = set()
    for path in PACKAGE.rglob("*.py"):
        for node in ast.walk(ast.parse(path.read_text())):
            if not isinstance(node, ast.Call) or not node.args:
                continue
            func = node.func
            name = (
                func.attr
                if isinstance(func, ast.Attribute)
                else getattr(func, "id", "")
            )
            first = node.args[0]
            if name in calls and isinstance(first, ast.Constant):
                found.add(str(first.value))
    return found


def test_every_hand_back_reason_raised_has_an_instruction():
    raised = _reasons(HAND_BACK_CALLS)
    assert raised
    assert raised <= set(HAND_BACK_REASONS)


def test_every_outcome_unknown_reason_raised_has_an_instruction():
    raised = _reasons(UNKNOWN_CALLS)
    assert raised
    assert raised <= set(OUTCOME_UNKNOWN_REASONS)


def test_every_outcome_unknown_instruction_says_reconcile_first():
    for reason in OUTCOME_UNKNOWN_REASONS.values():
        assert "court's own case query" in reason.action


def test_every_hand_back_lands_on_the_filing_set_checklist():
    for reason in HAND_BACK_REASONS:
        assert hand_back_note(reason, stage="claimed").link == "packet"
