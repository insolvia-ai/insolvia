from __future__ import annotations

import boto3
from botocore.config import Config

# SIGNATURE VERSION 4, EXPLICITLY, AND THIS LINE IS LOAD-BEARING.
#
# Verified against botocore rather than reasoned about: with the default
# configuration, `generate_presigned_url` in us-east-1 produced a legacy SigV2
# URL whose query string was
#   AWSAccessKeyId, Signature, Expires, content-type, x-amz-server-side-encryption
# — note what is missing. The ContentLength parameter was DROPPED SILENTLY. The
# URL still worked, still carried the content type, and would have accepted an
# upload of any size at all, so nothing about the failure would have looked
# like a failure; the size cap would simply not have existed.
#
# With s3v4 the same call signs
#   content-length;content-type;host;x-amz-server-side-encryption
# and every one of those becomes a term S3 checks. tests/test_document_blobs.py
# asserts that list for exactly this reason. Virtual addressing is spelled out
# alongside because path-style URLs are deprecated for new buckets.
_SIGNING = Config(signature_version="s3v4", s3={"addressing_style": "virtual"})


class S3DocumentBlobStore:
    """DocumentBlobStore backed by S3 — the bucket infra/modules/case_documents
    creates, encrypted under the case key.

    Credentials come from the runtime's default provider chain: the Lambda
    execution role in AWS, or in local dev the short-lived credentials
    scripts/dev-up.sh exports from the developer's AWS profile. There is no
    local emulator and no fake bucket — `infra/envs/dev` provisions this
    machine's real one, exactly as it does the case table.

    The role behind those credentials holds GetObject, PutObject and
    DeleteObject on this bucket's objects and NO s3:ListBucket, which is why
    nothing here enumerates: what documents a case has is a question the case
    store answers. An object with no row is what a half-finished upload leaves
    behind, and listing the bucket would make one look like a document.
    """

    def __init__(self, bucket_name: str) -> None:
        self.bucket_name = bucket_name
        self.client = boto3.client("s3", config=_SIGNING)

    def upload_url(
        self, storage_ref: str, *, content_type: str, byte_size: int, expires_in: int
    ) -> str:
        """One PUT, one object, one size, one content type, one encryption mode.

        Everything the server decided is a parameter here, and every parameter
        becomes a signed header the client must reproduce exactly or S3 refuses
        the request:

        - `ContentType` is the allowlisted, normalised value. A client cannot
          store a .docx through a URL minted for a PDF.
        - `ContentLength` is the declared size the route already checked
          against MAX_BYTE_SIZE. This is what turns the cap from a claim the
          client made into a limit S3 enforces — the request must carry exactly
          this many bytes.
        - `ServerSideEncryption` is the header the bucket policy's
          DenyEncryptionDowngrade statement is written against. The KEY is not
          named: S3 resolves the customer-managed key from the bucket's default
          encryption, so this service never holds a key id and its KMS grant
          stays fenced to `kms:ViaService = s3`.

        Note what is NOT a parameter: the bucket and the key. Both come from
        this instance and from the record, never from a caller.
        """
        return str(
            self.client.generate_presigned_url(
                "put_object",
                Params={
                    "Bucket": self.bucket_name,
                    "Key": storage_ref,
                    "ContentType": content_type,
                    "ContentLength": byte_size,
                    "ServerSideEncryption": "aws:kms",
                },
                ExpiresIn=expires_in,
            )
        )

    def download_url(self, storage_ref: str, *, expires_in: int) -> str:
        """One GET of one object.

        No response-header overrides, and the absent one is deliberate:
        `ResponseContentDisposition` would be the obvious way to make a browser
        download the file under the name the client originally gave it, and it
        would put that file name in a query string. File names are the one
        place a client's own words reach this feature —
        "smith-jane-2023-return.pdf" — and a query string is copied into
        browser history, proxy logs and referrer chains. The same rule that
        keeps names out of object keys keeps them out of URLs; the client
        already has the name from the record and can label its own download.
        """
        return str(
            self.client.generate_presigned_url(
                "get_object",
                Params={"Bucket": self.bucket_name, "Key": storage_ref},
                ExpiresIn=expires_in,
            )
        )

    def delete(self, storage_ref: str) -> None:
        # Deleting an object that is not there succeeds — S3's own behaviour,
        # and the one the port asks for, because this runs after the row is
        # already gone and an upload that never completed leaves no object to
        # delete.
        #
        # The bucket is versioned, so this writes a delete marker rather than
        # destroying bytes. That is the bucket's answer to "a delete that
        # should not have happened", and it is why this service needs no
        # soft-delete of its own.
        self.client.delete_object(Bucket=self.bucket_name, Key=storage_ref)
