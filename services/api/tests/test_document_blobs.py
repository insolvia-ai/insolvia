"""What the S3 adapter actually signs (issue 8.6).

The one adapter in this service whose correctness is invisible from the port.
`generate_presigned_url` does no network I/O — it is signing arithmetic over
static credentials — so this exercises the real botocore signer rather than a
fake, and needs nothing beyond credentials in the environment.

It exists because the failure it guards against is silent. Under botocore's
default signature version the same call produced a legacy SigV2 URL that DROPPED
the ContentLength parameter without a word: a working URL, carrying the right
content type, that would have accepted an upload of any size. The size cap would
simply not have existed, and no test that only checked "a URL came back" could
have told.
"""

from __future__ import annotations

from urllib.parse import parse_qs, urlparse

import pytest
from insolvia_api.adapters.aws.document_blobs import S3DocumentBlobStore

BUCKET = "insolvia-case-documents-example"
KEY = "cases/00000000-0000-4000-8000-0000000000ca/00000000-0000-4000-8000-0000000000d0"


@pytest.fixture
def blobs(monkeypatch):
    # Obviously fake, and never used against anything: signing is arithmetic.
    monkeypatch.setenv("AWS_ACCESS_KEY_ID", "AKIAEXAMPLEEXAMPLE00")
    monkeypatch.setenv("AWS_SECRET_ACCESS_KEY", "examplesecretkey0000000000000000")
    monkeypatch.delenv("AWS_SESSION_TOKEN", raising=False)
    monkeypatch.setenv("AWS_DEFAULT_REGION", "us-east-1")
    return S3DocumentBlobStore(BUCKET)


def query(url):
    return parse_qs(urlparse(url).query)


def test_an_upload_url_binds_every_term_the_server_decided(blobs):
    url = blobs.upload_url(
        KEY, content_type="application/pdf", byte_size=4096, expires_in=900
    )
    signed = query(url)["X-Amz-SignedHeaders"][0].split(";")
    # content-length is the whole point: it is what makes MAX_BYTE_SIZE a limit
    # S3 enforces rather than a number the client volunteered.
    assert "content-length" in signed
    assert "content-type" in signed
    assert "x-amz-server-side-encryption" in signed


def test_an_upload_url_is_signature_version_4(blobs):
    # The SigV2 fallback is what silently drops content-length, so the
    # algorithm is asserted directly rather than inferred.
    url = blobs.upload_url(KEY, content_type="image/png", byte_size=10, expires_in=900)
    assert query(url)["X-Amz-Algorithm"] == ["AWS4-HMAC-SHA256"]


def test_a_url_names_exactly_one_object_in_exactly_one_bucket(blobs):
    parsed = urlparse(
        blobs.upload_url(
            KEY, content_type="application/pdf", byte_size=1, expires_in=60
        )
    )
    assert parsed.scheme == "https"
    assert parsed.netloc.startswith(f"{BUCKET}.s3")
    assert parsed.path == f"/{KEY}"


@pytest.mark.parametrize("expires_in", [60, 300, 900])
def test_the_expiry_is_the_one_the_route_asked_for(blobs, expires_in):
    url = blobs.download_url(KEY, expires_in=expires_in)
    assert query(url)["X-Amz-Expires"] == [str(expires_in)]


def test_a_download_url_signs_nothing_the_client_must_reproduce(blobs):
    # A GET carries no body and no headers of ours, so the only signed header
    # is host. Anything else here would be a header the app has to guess.
    assert query(blobs.download_url(KEY, expires_in=300))["X-Amz-SignedHeaders"] == [
        "host"
    ]


def test_no_response_override_puts_a_file_name_in_the_query_string(blobs):
    """ResponseContentDisposition is the obvious convenience and is refused:
    it would carry the client's own file name — routinely a person's name — in
    a query string that lands in history and proxy logs."""
    assert "response-content-disposition" not in blobs.download_url(KEY, expires_in=300)
