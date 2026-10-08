"""Structured JSON logging — the same one-line-per-record shape services/api
uses (issue #69), duplicated here the way services/mailer duplicates it: the
formatter is fifty lines of stdlib and does not meet the core package's
admission rule.

Privacy rule (GLBA Safeguards Rule — see docs/adr/0001): log lines carry
ids, states and reasons only — never a credential, a TOTP code, a
court page, a document or case data. The worker logs a filing id and a
state, never what it typed into a court screen.
"""

from __future__ import annotations

import json
import logging
import sys
from datetime import UTC, datetime

# Attributes every LogRecord carries; anything else on the record arrived via
# `extra=` and belongs in the JSON payload.
_RESERVED = frozenset(vars(logging.LogRecord("", 0, "", 0, "", (), None)).keys()) | {
    "message",
    "asctime",
}


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "timestamp": datetime.fromtimestamp(record.created, UTC)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        for key, value in record.__dict__.items():
            if key not in _RESERVED:
                payload[key] = value
        if record.exc_info:
            payload["exception"] = self.formatException(record.exc_info)
        return json.dumps(payload, default=str)


def configure_logging(level: int = logging.INFO) -> None:
    """Route root logging through the JSON formatter, once, at composition time.

    The Lambda runtime pre-installs a root handler (which prefixes its own
    request-id format); reformatting existing handlers rather than stacking a
    new one keeps exactly one JSON line per record in both Lambda and the
    development server.
    """
    root = logging.getLogger()
    root.setLevel(level)
    if not root.handlers:
        root.addHandler(logging.StreamHandler(sys.stdout))
    for handler in root.handlers:
        handler.setFormatter(JsonFormatter())
