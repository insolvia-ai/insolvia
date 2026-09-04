"""In-memory ReviewModel — the deterministic stand-in for the Claude call.

ADR 0018's local story, applied to the review pipeline's one unrunnable hop:
the worker, the gate, the document builder and the findings parsing all run
under pytest for real; only the model generation itself is faked, with a
canned answer the test chose. A laptop without an Anthropic key exercises
everything except the generation the same way.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

from insolvia_api.core.petition_review import ReviewModelResult


class ScriptedReviewModel:
    """Answers every review with the findings it was built with, and records
    each document it was shown — which is how tests assert what would
    actually leave for the model API (the scrub rule, the projected line
    keys) rather than trusting the builder's intentions."""

    def __init__(
        self, findings: tuple[Mapping[str, Any], ...] = (), *, model: str = "scripted"
    ) -> None:
        self._findings = findings
        self._model = model
        self.documents: list[str] = []

    def review(self, document: str) -> ReviewModelResult:
        self.documents.append(document)
        return ReviewModelResult(
            model=self._model,
            raw={"findings": [dict(finding) for finding in self._findings]},
        )
