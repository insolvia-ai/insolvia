from io import BytesIO

import pytest

from insolvia_mailer.core.errors import AttachmentBlockedError, RetryableError
from insolvia_mailer.entrypoints.sender_lambda import _attachments


class FakeS3:
    def __init__(self, status, data=b"pdf"):
        self.status = status
        self.data = data

    def get_object_tagging(self, **_kwargs):
        if self.status is None:
            return {"TagSet": []}
        return {"TagSet": [{"Key": "GuardDutyMalwareScanStatus", "Value": self.status}]}

    def get_object(self, **_kwargs):
        return {"Body": BytesIO(self.data)}


class FakeStore:
    bucket = "bucket"

    def __init__(self, status, data=b"pdf"):
        self.s3 = FakeS3(status, data)


def manifest():
    import hashlib

    data = b"pdf"
    return {
        "attachments": [
            {
                "attachment_id": "att_123",
                "object_key": "attachments/insolvia_api/ins_123/att_123",
                "file_name": "guide.pdf",
                "content_type": "application/pdf",
                "size_bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
                "disposition": "attachment",
                "content_id": None,
            }
        ]
    }


def test_pending_attachment_is_retryable():
    with pytest.raises(RetryableError):
        _attachments(FakeStore(None), manifest())


@pytest.mark.parametrize(
    "status", ["THREATS_FOUND", "UNSUPPORTED", "ACCESS_DENIED", "FAILED"]
)
def test_unsafe_attachment_status_is_terminal(status):
    with pytest.raises(AttachmentBlockedError):
        _attachments(FakeStore(status), manifest())


def test_clean_attachment_is_loaded_and_verified():
    result = _attachments(FakeStore("NO_THREATS_FOUND"), manifest())
    assert result[0].data == b"pdf"
