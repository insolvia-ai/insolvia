"""The real ExtractionModel: one structured-output Messages call over a
document (issues 8.7/8.8).

The petition review's adapter shape, inherited whole (ADR 0019): one
request, no tools, no streaming, the model constrained to the per-kind
schema so the answer parses or the call is wrong — never prose fished for
JSON. The differences are the input — the document's own bytes as a
`document` (PDF) or `image` (JPEG/PNG) content block, base64, placed before
the instruction text — and the max-tokens stop, which here is DETERMINISTIC
(the same document generates the same overflow) and so becomes a JobError
instead of a retry.

Same timeout arithmetic as the review (240s per attempt, one SDK retry,
inside the worker Lambda's 900s), same model id — imported from
review_model.py, the one place ADR 0019 says it lives.
"""

from __future__ import annotations

import base64
import json
import logging
from typing import Any, Final

import anthropic

from insolvia_api.adapters.anthropic.review_model import REVIEW_MODEL
from insolvia_api.core.extraction import ExtractionModelResult, ExtractionRequest
from insolvia_api.core.jobs import JobError

logger = logging.getLogger(__name__)

# Extraction answers are long lists — a credit report can carry dozens of
# tradelines — so the ceiling sits above the review's, still small enough
# that one attempt finishes inside the 240s client timeout.
MAX_OUTPUT_TOKENS: Final = 16000

_TIMEOUT_SECONDS: Final = 240.0
_MAX_RETRIES: Final = 1

# The one fixed user-turn instruction. The real steering lives in the
# per-kind system prompt; this block exists because the API wants a text
# block beside the document and the schema names the rest.
_INSTRUCTION: Final = (
    "Extract this document into the required output format, following your"
    " instructions exactly."
)


class AnthropicExtractionModel:
    """core/ports.ExtractionModel over the Anthropic Messages API.

    The failure split follows the port's contract: a failure a retry cannot
    change (rejected key, model refusal, an overflowing answer) becomes
    JobError — the job fails deterministically with a preparer-safe message —
    and everything transient (rate limits, 5xx, network) propagates so SQS
    redelivery retries the whole job. Exception text stays in CloudWatch
    either way.
    """

    def __init__(self, api_key: str, *, model: str = REVIEW_MODEL) -> None:
        self._client = anthropic.Anthropic(
            api_key=api_key, timeout=_TIMEOUT_SECONDS, max_retries=_MAX_RETRIES
        )
        self._model = model

    def extract(self, request: ExtractionRequest) -> ExtractionModelResult:
        encoded = base64.b64encode(request.data).decode("ascii")
        # PDFs are `document` blocks, images are `image` blocks — the worker
        # has already refused anything else
        # (core/extraction.MODEL_INPUT_CONTENT_TYPES). Typed as Any because
        # the SDK's per-block TypedDicts pin media_type to literals this
        # adapter selects at runtime; the wire shape is the documented one.
        document_block: Any = {
            "type": "document" if request.media_type == "application/pdf" else "image",
            "source": {
                "type": "base64",
                "media_type": request.media_type,
                "data": encoded,
            },
        }
        try:
            response = self._client.messages.create(
                model=self._model,
                max_tokens=MAX_OUTPUT_TOKENS,
                system=request.system_prompt,
                messages=[
                    {
                        "role": "user",
                        "content": [
                            document_block,
                            {"type": "text", "text": _INSTRUCTION},
                        ],
                    }
                ],
                output_config={
                    "format": {"type": "json_schema", "schema": dict(request.schema)}
                },
            )
        except (anthropic.AuthenticationError, anthropic.PermissionDeniedError) as err:
            # A key that does not work will not start working on redelivery;
            # the same honest failure as no key at all.
            logger.error(
                "anthropic credentials rejected", extra={"status": err.status_code}
            )
            raise JobError(
                "AI extraction is not configured correctly in this environment.",
                category="not_configured",
            ) from err

        # Metadata only (GLBA): token counts and identifiers, never content.
        logger.info(
            "extraction model call finished",
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
                "The extraction model declined to read this document.",
                category="model_refused",
            )
        if response.stop_reason == "max_tokens":
            # Deterministic, unlike the review's mid-generation surprises: the
            # same document overflows the same ceiling on every attempt, so
            # retrying only delays the person waiting.
            raise JobError(
                "This document produced more records than one extraction can"
                " hold — split it and try again.",
                category="too_dense",
            )
        text = next(
            (block.text for block in response.content if block.type == "text"), None
        )
        if response.stop_reason != "end_turn" or text is None:
            # Another surprise mid-generation — transient in practice (a
            # fresh generation answers differently), so let the pipeline
            # retry it.
            raise RuntimeError(
                f"extraction model stopped unexpectedly ({response.stop_reason})"
            )
        return ExtractionModelResult(model=response.model, raw=json.loads(text))
