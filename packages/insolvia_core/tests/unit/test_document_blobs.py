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
    # The tag is what makes an abandoned upload reapable: the capability
    # outlives its row, so an object can land under a key nothing names, and
    # with no s3:ListBucket the bucket's expire-unconfirmed-uploads lifecycle
    # rule is the only thing that can ever reach it. Signed rather than merely
    # documented, so a client cannot drop the header and write an object that
    # nothing reaps.
    assert "x-amz-tagging" in signed


def test_the_signed_headers_are_exactly_the_four_the_client_can_satisfy(blobs):
    """The whole set, pinned, and the one that is NOT in it is the point.

    THE INCIDENT: the adapter signed `x-amz-server-side-encryption: aws:kms`
    without naming a key, and every presigned upload since the feature
    shipped was refused with an explicit deny — S3 encrypts such a request
    under the AWS-managed `aws/s3` key rather than the bucket default, and
    the bucket's DenyForeignEncryptionKey statement refuses exactly that.
    The first run of the integration tier against staging was what found
    it. The module header in adapters/aws/document_blobs.py carries the
    probe; this pins its conclusion so the header cannot quietly return.
    """
    url = blobs.upload_url(
        KEY, content_type="application/pdf", byte_size=4096, expires_in=900
    )
    signed = query(url)["X-Amz-SignedHeaders"][0].split(";")
    assert signed == ["content-length", "content-type", "host", "x-amz-tagging"]


def test_the_presign_names_no_encryption_algorithm_and_no_key(blobs, monkeypatch):
    """The parameters as the adapter hands them to botocore, not the URL that
    comes out — because `ServerSideEncryption` alone (no key id) is the exact
    combination the bucket refuses, and a URL-level check would have to know
    how botocore spells it. Same incident as the test above."""
    seen = {}

    def capture(operation, **kwargs):
        seen.update(operation=operation, params=kwargs["Params"])
        return "https://example.invalid/"

    monkeypatch.setattr(blobs.client, "generate_presigned_url", capture)
    blobs.upload_url(
        KEY, content_type="application/pdf", byte_size=4096, expires_in=900
    )
    assert seen["operation"] == "put_object"
    assert "ServerSideEncryption" not in seen["params"]
    assert "SSEKMSKeyId" not in seen["params"]
    # And the bucket-default encryption is the ONLY route to the case key, so
    # the four terms that ARE decided here are the whole parameter set.
    assert set(seen["params"]) == {
        "Bucket",
        "Key",
        "ContentType",
        "ContentLength",
        "Tagging",
    }


def test_a_direct_write_names_no_encryption_algorithm_either(blobs, monkeypatch):
    """put_bytes — the packet worker's and the forms hub's write — carried the
    same header and was refused with the same explicit deny (probed on the
    dev bucket alongside the presign). Silence lands on the bucket default."""
    calls = []
    monkeypatch.setattr(
        blobs.client, "put_object", lambda **kwargs: calls.append(kwargs)
    )
    blobs.put_bytes(KEY, content=b"%PDF-1.7", content_type="application/pdf")
    assert calls == [
        {
            "Bucket": BUCKET,
            "Key": KEY,
            "Body": b"%PDF-1.7",
            "ContentType": "application/pdf",
        }
    ]


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


def test_get_bytes_reads_the_denied_answer_as_absent(blobs, monkeypatch):
    # Same rule as stat, same reason: no ListBucket means a missing key
    # answers 403, so the worker's read must treat denied as absent.
    from botocore.exceptions import ClientError

    def deny(**kwargs):
        raise ClientError(
            {"Error": {"Code": "403", "Message": "Forbidden"}}, "GetObject"
        )

    monkeypatch.setattr(blobs.client, "get_object", deny)
    assert blobs.get_bytes(KEY) is None


def test_get_bytes_returns_the_object_whole(blobs, monkeypatch):
    import io

    monkeypatch.setattr(
        blobs.client,
        "get_object",
        lambda **kwargs: {"Body": io.BytesIO(b"%PDF-1.7 bytes")},
    )
    assert blobs.get_bytes(KEY) == b"%PDF-1.7 bytes"


def test_get_bytes_does_not_swallow_a_real_failure(blobs, monkeypatch):
    from botocore.exceptions import ClientError

    def throttle(**kwargs):
        raise ClientError(
            {"Error": {"Code": "SlowDown", "Message": "Reduce request rate"}},
            "GetObject",
        )

    monkeypatch.setattr(blobs.client, "get_object", throttle)
    with pytest.raises(ClientError):
        blobs.get_bytes(KEY)
