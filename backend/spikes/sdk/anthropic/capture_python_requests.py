"""Capture the exact HTTP requests the Python Anthropic adapters send (#515).

No network: the adapters' `anthropic.Anthropic(...)` is wrapped so every client
gets an httpx MockTransport that records the request and answers with a canned
Messages response. Writes fixtures/*.json (inputs + the captured wire request),
which the C# tests replay and compare against.

Run from services/api with the worker requirements installed:
  cd services/api && python ../../backend/spikes/sdk/anthropic/capture_python_requests.py
"""
from __future__ import annotations

import json
import pathlib
import sys
from typing import Any

import anthropic
import httpx2 as httpx  # anthropic 1.x runs on httpx2

OUT = pathlib.Path(__file__).resolve().parent / "fixtures"
captured: list[dict[str, Any]] = []


def _handler(request: httpx.Request) -> httpx.Response:
    captured.append(
        {
            "method": request.method,
            "path": request.url.path,
            "timeout": request.extensions.get("timeout"),
            "headers": {
                k: v
                for k, v in request.headers.items()
                if k.lower() in ("anthropic-version", "content-type", "anthropic-beta")
            },
            "body": json.loads(request.content),
        }
    )
    return httpx.Response(
        200,
        json={
            "id": "msg_x", "type": "message", "role": "assistant",
            "model": "claude-opus-5", "stop_reason": "end_turn", "stop_sequence": None,
            "content": [{"type": "text", "text": "{\"ok\": true}"}],
            "usage": {"input_tokens": 1, "output_tokens": 1},
        },
    )


_real = anthropic.Anthropic
clients: list[Any] = []


def _factory(**kwargs: Any) -> Any:
    client = _real(http_client=httpx.Client(transport=httpx.MockTransport(_handler)), **kwargs)
    clients.append(client)
    return client


sys.path.insert(0, str(pathlib.Path.cwd() / "src"))
from insolvia_api.adapters.anthropic import extraction_model, review_model  # noqa: E402
from insolvia_api.core.extraction import EXTRACTORS, ExtractionRequest  # noqa: E402
from insolvia_api.core.petition_review import (  # noqa: E402
    REVIEW_OUTPUT_SCHEMA,
    REVIEW_SYSTEM_PROMPT,
)

anthropic.Anthropic = _factory  # type: ignore[misc]

review_doc = "PETITION REVIEW INPUT\nline two é \"quoted\" <tag> & more\n"
review_model.AnthropicReviewModel("sk-test-not-a-key").review(review_doc)
review_cap = captured.pop()

pdf = b"%PDF-1.4\n%spike\x00\xff\xfe binary \n"
png = b"\x89PNG\r\n\x1a\n\x00\x00spike"
system_prompt, schema, _ = EXTRACTORS["credit_report"]
cases = {}
for name, media, data in (("pdf", "application/pdf", pdf), ("png", "image/png", png)):
    extraction_model.AnthropicExtractionModel("sk-test-not-a-key").extract(
        ExtractionRequest(media_type=media, data=data, system_prompt=system_prompt, schema=schema)
    )
    cases[name] = {"media_type": media, "data_hex": data.hex(), "request": captured.pop()}

OUT.mkdir(exist_ok=True)
(OUT / "review.json").write_text(
    json.dumps(
        {
            "model_constant": review_model.REVIEW_MODEL,
            "client_max_retries": clients[0].max_retries,
            "document": review_doc,
            "system_prompt": REVIEW_SYSTEM_PROMPT,
            "schema": REVIEW_OUTPUT_SCHEMA,
            "request": review_cap,
        },
        indent=1, ensure_ascii=False, sort_keys=True,
    )
)
(OUT / "extraction.json").write_text(
    json.dumps(
        {
            "client_max_retries": clients[1].max_retries,
            "system_prompt": system_prompt,
            "schema": dict(schema),
            "cases": cases,
        },
        indent=1, ensure_ascii=False, sort_keys=True,
    )
)
print("anthropic python sdk", anthropic.__version__, "| wrote", OUT)
