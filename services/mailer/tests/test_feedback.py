from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.entrypoints import feedback_lambda as feedback


def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset(),
        allowed_message_classes=frozenset(),
    )


def test_product_event_identity_comes_from_mailer_tags():
    result = feedback._event_identity(
        {
            "mail": {
                "tags": {
                    "mailer-service-id": ["insolvia_api"],
                    "mailer-application-message-id": ["ins_123"],
                    "mailer-category": ["welcome"],
                    "mailer-message-class": ["transactional"],
                }
            }
        },
        {"insolvia_api": service()},
        {},
    )

    assert result[1:] == ("ins_123", "welcome", "transactional")


def test_auth_event_identity_comes_from_configuration_set():
    result = feedback._event_identity(
        {
            "mail": {
                "messageId": "provider-123",
                "tags": {
                    "ses:configuration-set": ["mailer-insolvia_api-auth-production"]
                },
            }
        },
        {"insolvia_api": service()},
        {"mailer-insolvia_api-auth-production": "insolvia_api"},
    )

    assert result[1:] == ("auth_provider-123", "authentication", "authentication")


def test_bounce_recipients_are_extracted_without_logging_payload():
    assert feedback._recipients(
        {"bounce": {"bouncedRecipients": [{"emailAddress": "person@example.com"}]}},
        "bounce",
    ) == ["person@example.com"]


def test_only_permanent_bounces_and_complaints_suppress():
    assert feedback._suppresses_recipient(
        {"bounce": {"bounceType": "Permanent"}}, "bounce"
    )
    assert not feedback._suppresses_recipient(
        {"bounce": {"bounceType": "Transient"}}, "bounce"
    )
    assert feedback._suppresses_recipient({}, "complaint")


def test_guardduty_event_emits_only_safe_metric(monkeypatch):
    metrics = []
    monkeypatch.setattr(
        feedback, "_metric", lambda name, **_kwargs: metrics.append(name)
    )

    assert feedback.handler({"guardduty_status": "THREATS_FOUND"}, None) == {
        "batchItemFailures": []
    }
    assert metrics == ["AttachmentThreat"]
