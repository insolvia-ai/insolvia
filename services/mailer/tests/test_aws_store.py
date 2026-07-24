import pytest
from botocore.exceptions import ClientError

from insolvia_mailer.adapters.aws.store import AwsStore
from insolvia_mailer.core.config import ServiceConfig
from insolvia_mailer.core.errors import ConflictError
from insolvia_mailer.core.models import MessageRequest


class FakeDdb:
    def __init__(self):
        self.item = None

    def put_item(self, **kwargs):
        if self.item is not None:
            raise ClientError(
                {
                    "Error": {
                        "Code": "ConditionalCheckFailedException",
                        "Message": "exists",
                    }
                },
                "PutItem",
            )
        self.item = kwargs["Item"]

    def get_item(self, **_kwargs):
        return {"Item": self.item} if self.item else {}

    def update_item(self, **kwargs):
        values = kwargs["ExpressionAttributeValues"]
        if ":queued" in values:
            self.item["status"] = values[":queued"]


class FakeS3:
    def __init__(self):
        self.puts = []

    def put_object(self, **kwargs):
        self.puts.append(kwargs)


class FakeSqs:
    def __init__(self):
        self.messages = []

    def send_message(self, **kwargs):
        self.messages.append(kwargs)


def service():
    return ServiceConfig(
        service_id="insolvia_api",
        sender_name="Insolvia",
        sender_address="no-reply@insolvia.ai",
        allowed_categories=frozenset({"welcome"}),
        allowed_message_classes=frozenset({"transactional"}),
        configuration_set="mailer-insolvia_api-product-production",
        send_queue_url="https://sqs.example/send",
    )


def message(subject="Welcome"):
    return MessageRequest.from_dict(
        {
            "schema_version": 1,
            "application_message_id": "ins_123",
            "category": "welcome",
            "message_class": "transactional",
            "to_address": "recipient@example.com",
            "subject": subject,
            "html_body": "<p>Hello</p>",
            "text_body": "Hello",
            "attachments": [],
        },
        service(),
    )


def store():
    value = AwsStore.__new__(AwsStore)
    value.bucket = "bucket"
    value.messages_table = "messages"
    value.suppressions_table = "suppressions"
    value.ddb = FakeDdb()
    value.s3 = FakeS3()
    value.sqs = FakeSqs()
    return value


def test_admission_is_idempotent_and_dynamodb_is_privacy_safe():
    value = store()

    value.admit_message(service(), message())
    value.admit_message(service(), message())

    assert len(value.s3.puts) == 1
    assert len(value.sqs.messages) == 1
    assert not {
        "to_address",
        "subject",
        "html_body",
        "text_body",
        "file_name",
        "object_key",
    }.intersection(value.ddb.item)


def test_retry_during_submission_never_requeues():
    value = store()
    value.admit_message(service(), message())
    value.ddb.item["status"] = {"S": "submitting"}

    value.admit_message(service(), message())

    assert len(value.sqs.messages) == 1


def test_id_reuse_with_different_content_conflicts():
    value = store()
    value.admit_message(service(), message())

    with pytest.raises(ConflictError):
        value.admit_message(service(), message(subject="Changed"))
