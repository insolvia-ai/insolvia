import json

import pytest


@pytest.fixture(autouse=True)
def local_registry(monkeypatch):
    monkeypatch.setenv(
        "MAILER_DEVELOPMENT_SERVICES_JSON",
        json.dumps(
            {
                "insolvia_api": {
                    "sender_name": "Insolvia",
                    "sender_address": "no-reply@insolvia.ai",
                    "allowed_categories": ["welcome", "email_verification"],
                    "allowed_message_classes": ["transactional"],
                }
            }
        ),
    )
