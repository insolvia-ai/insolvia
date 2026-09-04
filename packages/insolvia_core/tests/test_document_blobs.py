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
from botocore.exceptions import ClientError
from insolvia_core.adapters.aws.document_blobs import S3DocumentBlobStore
from insolvia_core.documents import UPLOAD_TAG, StoredBlob

BUCKET = "insolvia-example-case-documents-us-east-1"
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
    # The tag is what makes an abandoned upload reapable: the capability
    # outlives its row, so an object can land under a key nothing names, and
    # with no s3:ListBucket the bucket's expire-unconfirmed-uploads lifecycle
    # rule is the only thing that can ever reach it. Signed rather than merely
    # documented, so a client cannot drop the header and write an object that
    # nothing reaps.
    assert "x-amz-tagging" in signed


def test_the_upload_tag_is_the_one_the_lifecycle_rule_filters_on(blobs):
    """A tag S3 stores but the bucket rule does not match is worse than none:
    the upload succeeds and the bytes are never reaped. The value here is also
    written by hand into a Terraform filter
    (infra/modules/case_documents/main.tf, expire-unconfirmed-uploads), so it
    is asserted rather than assumed."""
    url = blobs.upload_url(
        KEY, content_type="application/pdf", byte_size=4096, expires_in=900
    )
    assert UPLOAD_TAG == "upload=unconfirmed"
    # Signing does not put the header VALUE in the query string, only its name
    # in the signed set — so what is asserted is that the adapter passed the
    # constant the route also advertises, not a second spelling of it.
    assert "x-amz-tagging" in query(url)["X-Amz-SignedHeaders"][0]


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


# ── What the confirm path asks S3 ───────────────────────────────
# The transport is monkeypatched at the boto3 client, matching how
# test_mailer_client.py and test_jwks_provider.py fake their I/O. No moto.


def client_error(code):
    return ClientError({"Error": {"Code": code, "Message": "x"}}, "HeadObject")


def test_a_missing_object_reads_as_absent_even_though_s3_says_403(blobs, monkeypatch):
    """THE BRANCH THAT IS EASY TO GET WRONG AND IMPOSSIBLE TO SEE LOCALLY.

    S3 answers HeadObject on a key that does not exist with 404 if the caller
    holds s3:ListBucket and 403 if it does not. This service deliberately holds
    no ListBucket — the case store is the record of what should be there — so
    403 is the ORDINARY answer for "the client never uploaded", which is the
    exact case the confirm route exists to catch. Letting it raise would turn
    every abandoned upload into a 500.
    """

    def denied(**_):
        raise client_error("403")

    monkeypatch.setattr(blobs.client, "head_object", denied)
    assert blobs.stat(KEY) is None


@pytest.mark.parametrize("code", ["404", "NoSuchKey"])
def test_the_404_shaped_answers_also_read_as_absent(blobs, monkeypatch, code):
    """The same bucket answers 404 to a principal that does hold ListBucket —
    a developer's own IAM user in infra/envs/dev, for one — so both codes have
    to mean the same thing here."""

    def missing(**_):
        raise client_error(code)

    monkeypatch.setattr(blobs.client, "head_object", missing)
    assert blobs.stat(KEY) is None


def test_a_real_failure_is_not_swallowed_as_an_abandoned_upload(blobs, monkeypatch):
    """The cost of the branch above is that it hides one class of error, so it
    must hide as little as possible: anything that is not a 403/404 is a fault,
    not a missing object, and must reach the 500 handler rather than telling
    the user their upload did not finish."""

    def broken(**_):
        raise client_error("InternalError")

    monkeypatch.setattr(blobs.client, "head_object", broken)
    with pytest.raises(ClientError):
        blobs.stat(KEY)


def test_stat_reports_the_size_and_an_unquoted_etag(blobs, monkeypatch):
    """S3 quotes the ETag in the header. Stored unquoted, so no later
    comparison has to remember to strip it."""
    digest = "d41d8cd98f00b204e9800998ecf8427e"
    monkeypatch.setattr(
        blobs.client,
        "head_object",
        lambda **_: {"ContentLength": 4096, "ETag": f'"{digest}"'},
    )
    stored = blobs.stat(KEY)
    assert stored == StoredBlob(byte_size=4096, etag=digest)


def test_clearing_the_tag_writes_an_empty_tag_set(blobs, monkeypatch):
    """PutObjectTagging with an empty set rather than DeleteObjectTagging, and
    the choice is about IAM rather than behaviour: this needs
    s3:PutObjectTagging, which the role must already hold for the tagged PUT,
    while DeleteObjectTagging would be a second tagging action granted for
    nothing extra."""
    calls = []
    monkeypatch.setattr(
        blobs.client, "put_object_tagging", lambda **kwargs: calls.append(kwargs)
    )
    blobs.clear_upload_tag(KEY)
    assert calls == [{"Bucket": BUCKET, "Key": KEY, "Tagging": {"TagSet": []}}]


def test_no_response_override_puts_a_file_name_in_the_query_string(blobs):
    """ResponseContentDisposition is the obvious convenience and is refused:
    it would carry the client's own file name — routinely a person's name — in
    a query string that lands in history and proxy logs."""
    assert "response-content-disposition" not in blobs.download_url(KEY, expires_in=300)
