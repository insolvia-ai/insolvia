"""Structured JSON logging (issue #69).

andreas-services/mailer logs plain text via logging.basicConfig; this service
needs machine-queryable request logs in CloudWatch, so it renders every stdlib
log record as one JSON object per line instead. The formatter is plain stdlib
— no third-party logging dependency — and any `extra={...}` keys a call site
passes are merged into the JSON object.

Privacy rule (GLBA Safeguards Rule — see docs/adr/0001): log lines carry
request metadata only, never request or response bodies and never PII. In
particular, waitlist field values (name, firm, email, ...) are NEVER logged;
the generated submission id is the only waitlist datum that may appear.
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
