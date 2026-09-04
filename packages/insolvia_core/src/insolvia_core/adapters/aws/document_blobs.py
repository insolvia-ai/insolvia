from __future__ import annotations

import logging

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

from insolvia_core.documents import UPLOAD_TAG, StoredBlob

logger = logging.getLogger(__name__)

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
          stays fenced to `kms:ViaService = s3`. Naming a key would also have
          to satisfy the bucket's DenyForeignEncryptionKey statement, which
          exists precisely because a key id is a thing a policy has to fence.
        - `Tagging` marks the object `upload=unconfirmed`, which is what makes
          the bytes REAPABLE. This capability outlives the record that
          authorised it and its payload is not signed, so it can be replayed
          and it still works after the document is deleted — and the API holds
          no s3:ListBucket, so an object under a key no row names is one
          nothing in this system can ever find. The tag is the handle the
          bucket's `expire-unconfirmed-uploads` lifecycle rule reaps by. See
          core/documents.py: UPLOAD_TAG, which both this and the route read so
          the signature and the header the client is told to send cannot drift.

        The tag is why the API role carries s3:PutObjectTagging: S3 evaluates a
        presigned request against the SIGNER's permissions, and a PutObject
        bearing `x-amz-tagging` needs that action as well as s3:PutObject.
        Without it the URL is valid and every upload is a 403.

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
                    "Tagging": UPLOAD_TAG,
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

    def stat(self, storage_ref: str) -> StoredBlob | None:
        """HeadObject, mapped to "here are the facts" or "it is not there".

        A MISSING OBJECT IN THIS BUCKET ANSWERS 403, NOT 404, and treating that
        as an error would make the confirm route fail closed on the ordinary
        case it exists to detect. S3's rule: HeadObject on a key that does not
        exist returns 404 if the caller holds s3:ListBucket on the bucket and
        403 if it does not. This service deliberately holds no ListBucket — the
        case store is the record of what should be there, and listing would
        make an orphaned object look like a document — so the 403 branch is the
        NORMAL answer for "the client never uploaded", not an anomaly.

        What that costs, said plainly rather than discovered later: a genuine
        permission failure — a broken IAM grant, a KMS deny — is
        indistinguishable here from an absent object, and would present to the
        user as "your upload did not finish". Granting ListBucket would
        separate them and would cost the property above, which is worse. So the
        403 branch logs at warning instead: a burst of them is the signature of
        a misconfiguration, and a steady trickle is users abandoning uploads.

        NOT VERIFIED AGAINST A REAL BUCKET. Every other claim in this module
        was probed against the dev bucket; this one could not be, and the
        403-vs-404 split is exactly the kind of behaviour that only appears
        with real IAM. It is the documented behaviour and the branch handles
        both codes, but the first dev-environment run of the confirm route is
        what actually confirms it.
        """
        try:
            response = self.client.head_object(Bucket=self.bucket_name, Key=storage_ref)
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code == "403":
                logger.warning(
                    "head_object was denied; treating the object as absent",
                    extra={"bucket": self.bucket_name},
                )
            if code in ("403", "404", "NoSuchKey"):
                return None
            raise
        return StoredBlob(
            byte_size=int(response["ContentLength"]),
            # S3 quotes the etag in the header. Stripped here so the stored
            # value is the digest itself rather than a quoted string that every
            # later comparison would have to remember to unwrap.
            etag=str(response["ETag"]).strip('"'),
        )

    def clear_upload_tag(self, storage_ref: str) -> None:
        """Replace the tag set with an empty one.

        PutObjectTagging WITH AN EMPTY SET RATHER THAN DeleteObjectTagging, and
        the reason is the IAM grant rather than the API. Both do the same thing
        to the object. DeleteObjectTagging needs `s3:DeleteObjectTagging`,
        which would be a SECOND tagging action on this role; this needs
        `s3:PutObjectTagging`, which the role must already hold, because a
        presigned PUT carrying `x-amz-tagging` is evaluated against the
        signer's permissions and is refused without it. So the narrower grant
        is the one that adds nothing: the role can already write this object's
        tags, and clearing them is strictly less than it can already do.

        This is what takes the object out of the bucket's
        expire-unconfirmed-uploads filter, and it is the only thing that does.
        """
        self.client.put_object_tagging(
            Bucket=self.bucket_name, Key=storage_ref, Tagging={"TagSet": []}
        )

    def put_bytes(self, storage_ref: str, *, content: bytes, content_type: str) -> None:
        """A direct PutObject — the packet worker's write (issue #96), under
        the WORKER role's grant (infra/modules/case_documents,
        worker_role_name), not a presigned capability.

        `ServerSideEncryption` is stated for the same reason the presigned
        PUT states it: the bucket policy's DenyEncryptionDowngrade statement
        matches on the header, so an unencrypted-looking request is refused
        rather than quietly falling back. No Tagging — this write and its
        record land together (PacketStore.create), so the unconfirmed-upload
        reaper has no business with these bytes.
        """
        self.client.put_object(
            Bucket=self.bucket_name,
            Key=storage_ref,
            Body=content,
            ContentType=content_type,
            ServerSideEncryption="aws:kms",
        )

    def get_bytes(self, storage_ref: str) -> bytes | None:
        """A direct GetObject — the extraction worker's read (8.7/8.8), under
        the WORKER role's grant (infra/modules/case_documents,
        worker_role_name), not a presigned capability.

        The 403-means-absent rule is `stat`'s, inherited whole: this service
        holds no s3:ListBucket, so S3 answers 403 for a key that is not
        there, and a genuine permission failure is indistinguishable from an
        absent object at this boundary. The warning log is the tell — a burst
        of them is a misconfigured grant, not a run of vanished uploads.
        """
        try:
            response = self.client.get_object(Bucket=self.bucket_name, Key=storage_ref)
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code in ("403", "AccessDenied"):
                logger.warning(
                    "get_object was denied; treating the object as absent",
                    extra={"bucket": self.bucket_name},
                )
            if code in ("403", "AccessDenied", "404", "NoSuchKey"):
                return None
            raise
        return bytes(response["Body"].read())

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
