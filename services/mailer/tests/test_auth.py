import pytest

from insolvia_mailer.adapters.aws.auth import (
    IamAuthorizer,
    authorize,
    normalize_principal,
    principal_from_event,
    reset_principal,
    set_principal,
)
from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import AuthorizationError


def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset(),
        allowed_message_classes=frozenset(),
        allowed_role_arns=frozenset(
            {"arn:aws:iam::123456789012:role/insolvia-api-lambda-role-production"}
        ),
    )


def test_assumed_role_is_normalized():
    assert (
        normalize_principal(
            "arn:aws:sts::123456789012:assumed-role/insolvia-api-lambda-role-production/session"
        )
        == "arn:aws:iam::123456789012:role/insolvia-api-lambda-role-production"
    )


def test_principal_is_read_from_http_api_event():
    event = {
        "requestContext": {
            "authorizer": {
                "iam": {
                    "userArn": (
                        "arn:aws:sts::123456789012:assumed-role/"
                        "insolvia-api-lambda-role-production/session"
                    )
                }
            }
        }
    }
    assert principal_from_event(event).endswith(
        "role/insolvia-api-lambda-role-production"
    )


def test_unregistered_role_is_rejected():
    with pytest.raises(AuthorizationError):
        authorize(service(), "arn:aws:iam::123456789012:role/storybook")


def test_iam_authorizer_uses_only_lambda_request_context():
    authorizer = IamAuthorizer()
    with pytest.raises(AuthorizationError):
        authorizer.authorize(service())

    token = set_principal(
        "arn:aws:iam::123456789012:role/insolvia-api-lambda-role-production"
    )
    try:
        authorizer.authorize(service())
    finally:
        reset_principal(token)
