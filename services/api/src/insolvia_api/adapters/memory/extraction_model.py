"""In-memory ExtractionModel — the deterministic stand-in for the Claude call.

ADR 0018's local story, applied to extraction's one unrunnable hop, exactly
as ScriptedReviewModel does for the review: the worker, the document gate,
the coercion and the candidate writes all run under pytest for real; only
the model generation itself is faked, with a canned answer the test chose.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

from insolvia_api.core.extraction import ExtractionModelResult, ExtractionRequest


class ScriptedExtractionModel:
    """Answers every extraction with the raw output it was built with, and
    records each request it was shown — which is how tests assert what would
    actually leave for the model API (the bytes, the media type, the exact
    prompt and schema) rather than trusting the worker's intentions."""

    def __init__(
        self, raw: Mapping[str, Any] | None = None, *, model: str = "scripted"
    ) -> None:
        self._raw = raw if raw is not None else {}
        self._model = model
        self.requests: list[ExtractionRequest] = []

    def extract(self, request: ExtractionRequest) -> ExtractionModelResult:
        self.requests.append(request)
        return ExtractionModelResult(model=self._model, raw=dict(self._raw))
