"""Bytes in and out of the real bucket, through the capabilities the API mints.

THE ONE ROUND TRIP NOTHING ELSE EXERCISES END TO END. The unit tier proves
the presign signs the right headers against a faked client; the bucket
policy (infra/modules/case_documents) refuses a PUT that arrives without
`x-amz-server-side-encryption: aws:kms`, the CORS rule decides what a
browser may send, and the KMS grant decides whether the API's role may mint
a data key at all. Every one of those has failed on its own before and
every one looks identical from a unit test: "the upload did not finish".

Four calls, in the order the app makes them: authorise → PUT → complete →
download. Then the record is deleted, in teardown, however the assertions
went — a document is the one thing in the scratch case that CAN be removed,
so this test leaves nothing behind.
"""

from __future__ import annotations

import urllib.error
import urllib.request
from typing import Any

import pytest

from tests.integration.conftest import Api

# Small on purpose: this proves the four-call shape and the bytes surviving
# S3, not throughput. A real PDF header so the content type is honest.
BODY = b"%PDF-1.4\n% insolvia integration tier: bytes that went to S3 and back\n"
CONTENT_TYPE = "application/pdf"


def _send(
    url: str, method: str, *, headers: dict[str, str], body: bytes | None
) -> tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, method=method)
    for name, value in headers.items():
        request.add_header(name, value)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


@pytest.fixture
def document(admin: Api, scratch_case: dict[str, Any]):
    """A pending record plus its upload capability; deleted afterwards."""
    case_id = scratch_case["id"]
    created = admin.post(
        f"/v1/cases/{case_id}/documents",
        {
            "kind": "other",
            "fileName": "integration-probe.pdf",
            "contentType": CONTENT_TYPE,
            "byteSize": len(BODY),
        },
    )
    yield created
    admin.delete(
        f"/v1/cases/{case_id}/documents/{created['document']['id']}",
        expect=(204, 404),
    )


def test_an_upload_round_trips_through_the_bucket(
    admin: Api, scratch_case: dict[str, Any], document: dict[str, Any]
):
    case_id = scratch_case["id"]
    record, upload = document["document"], document["upload"]
    assert record["status"] == "pending"
    assert upload["method"] == "PUT"

    # 1. The PUT, with EXACTLY the headers the signature demands. A 403 here
    #    is the bucket policy, the CORS rule, or a signed header the client
    #    was not told about — and the body S3 returns says which.
    status, reply = _send(upload["url"], "PUT", headers=upload["headers"], body=BODY)
    assert status == 200, f"the presigned PUT was refused ({status}): {reply[:300]!r}"

    # 2. Complete: HeadObject under the API's own role, tag cleared, row stored.
    completed = admin.post(
        f"/v1/cases/{case_id}/documents/{record['id']}/complete", expect=200
    )
    assert completed["status"] == "stored"
    assert completed["byteSize"] == len(BODY), (
        "the stored size is what S3 counted, not what the client claimed — a "
        "mismatch means the content-length stopped being bound into the signature"
    )

    # 3. The listing shows it stored, which is what the app branches on.
    listed = admin.get(f"/v1/cases/{case_id}/documents")["documents"]
    mine = next(d for d in listed if d["id"] == record["id"])
    assert mine["status"] == "stored"

    # 4. A download capability, and the bytes behind it are the bytes sent.
    ticket = admin.get(f"/v1/cases/{case_id}/documents/{record['id']}/url")
    status, fetched = _send(ticket["url"], "GET", headers={}, body=None)
    assert status == 200
    assert fetched == BODY


def test_completing_before_the_bytes_landed_is_refused(
    admin: Api, scratch_case: dict[str, Any], document: dict[str, Any]
):
    """The reaper's safety rests on this: `complete` must not turn a row
    `stored` on the caller's word alone."""
    record = document["document"]
    admin.post(
        f"/v1/cases/{scratch_case['id']}/documents/{record['id']}/complete",
        expect=409,
    )
