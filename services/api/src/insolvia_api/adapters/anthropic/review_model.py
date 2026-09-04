"""The real ReviewModel: one structured-output Messages call to Claude.

The repo's first Anthropic API call, and deliberately its dullest possible
shape (ADR 0019): one request, no tools, no streaming, the model constrained
to core/petition_review.REVIEW_OUTPUT_SCHEMA so the answer parses or the
call is wrong — never a prose reply fished for JSON. Extraction (8.7)
inherits this adapter's decisions; change them there and here together.

Timeout arithmetic, because the worker Lambda's ceiling is 900s: the client
is capped at 240s per attempt with ONE SDK retry, so the worst case
(~2 x 240s plus backoff) still leaves the Lambda room to write the job's
failure row instead of being killed mid-flight — a killed attempt costs an
extra SQS redelivery cycle for the same answer.
"""

from __future__ import annotations

import json
import logging
from typing import Final

import anthropic

from insolvia_api.core.jobs import JobError
from insolvia_api.core.petition_review import (
    REVIEW_OUTPUT_SCHEMA,
    REVIEW_SYSTEM_PROMPT,
    ReviewModelResult,
)

logger = logging.getLogger(__name__)

# The reviewing model (ADR 0019). Opus-tier deliberately: the review's value
# is catching subtle cross-schedule inconsistencies, and — the GLBA half —
# this tier remains eligible for a zero-data-retention agreement, which the
# newest research-tier model is not.
REVIEW_MODEL: Final = "claude-opus-5"

# Findings are a short list of sentences; this bounds a runaway generation,
# not a legitimate answer.
MAX_OUTPUT_TOKENS: Final = 16000

_TIMEOUT_SECONDS: Final = 240.0
_MAX_RETRIES: Final = 1


class AnthropicReviewModel:
    """core/ports.ReviewModel over the Anthropic Messages API.

    The failure split follows the port's contract: a failure a retry cannot
    change (rejected key, model refusal) becomes JobError — the job fails
    deterministically with a preparer-safe message — and everything
    transient (rate limits, 5xx, network) propagates so SQS redelivery
    retries the whole job. Exception text stays in CloudWatch either way.
    """

    def __init__(self, api_key: str, *, model: str = REVIEW_MODEL) -> None:
        self._client = anthropic.Anthropic(
            api_key=api_key, timeout=_TIMEOUT_SECONDS, max_retries=_MAX_RETRIES
        )
        self._model = model

    def review(self, document: str) -> ReviewModelResult:
        try:
            response = self._client.messages.create(
                model=self._model,
                max_tokens=MAX_OUTPUT_TOKENS,
                system=REVIEW_SYSTEM_PROMPT,
                messages=[{"role": "user", "content": document}],
                output_config={
                    "format": {"type": "json_schema", "schema": REVIEW_OUTPUT_SCHEMA}
                },
            )
        except (anthropic.AuthenticationError, anthropic.PermissionDeniedError) as err:
            # A key that does not work will not start working on redelivery;
            # the same honest failure as no key at all.
            logger.error(
                "anthropic credentials rejected", extra={"status": err.status_code}
            )
            raise JobError(
                "AI review is not configured correctly in this environment.",
                category="not_configured",
            ) from err

        # Metadata only (GLBA): token counts and identifiers, never content.
        logger.info(
            "review model call finished",
            extra={
                "model": response.model,
                "stop_reason": response.stop_reason,
                "input_tokens": response.usage.input_tokens,
                "output_tokens": response.usage.output_tokens,
                "request_id": response._request_id,
            },
        )
        if response.stop_reason == "refusal":
            # The safety layer declined this content; a redelivery would be
            # declined the same way.
            raise JobError(
                "The review model declined to review this case.",
                category="model_refused",
            )
        text = next(
            (block.text for block in response.content if block.type == "text"), None
        )
        if response.stop_reason != "end_turn" or text is None:
            # max_tokens or another surprise mid-generation — transient in
            # practice (a fresh generation answers differently), so let the
            # pipeline retry it.
            raise RuntimeError(
                f"review model stopped unexpectedly ({response.stop_reason})"
            )
        return ReviewModelResult(model=response.model, raw=json.loads(text))
