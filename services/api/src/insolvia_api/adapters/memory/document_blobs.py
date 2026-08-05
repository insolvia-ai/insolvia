from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import quote, urlencode


@dataclass(frozen=True)
class MintedUrl:
    """One capability this store handed out — what the real one binds into a
    signature, kept where a test can read it."""

    method: str
    storage_ref: str
    expires_in: int
    content_type: str | None = None
    byte_size: int | None = None


class MemoryDocumentBlobStore:
    """Ephemeral DocumentBlobStore for tests and the plain development server.

    It mints `memory+s3://` URLs that no HTTP client can fetch, and that is the
    honest shape: there is no local S3 and this adapter is not pretending to be
    one. A developer who wants bytes to actually move runs
    scripts/dev-aws-setup.sh, which provisions this machine's real bucket and
    writes CASE_DOCUMENT_BUCKET into services/api/.env — after which the S3
    adapter is what the development server composes.

    NO LOOSER THAN THE REAL ONE, in the one respect a fake here can be. The
    constraints S3 binds into a signature — the object, the verb, the content
    type, the exact byte count, the expiry — are all carried in the URL this
    mints and recorded in `minted`, so a route that forgot to pass one fails a
    test rather than passing silently and failing on staging. What it cannot do
    is REFUSE a mismatched upload, because nothing here receives one; the
    binding itself is asserted against botocore in
    tests/test_document_blobs.py.
    """

    def __init__(self) -> None:
        self.minted: list[MintedUrl] = []
        self.deleted: list[str] = []

    def _url(self, storage_ref: str, **terms: object) -> str:
        if not storage_ref:
            # The real store would happily sign a URL for the bucket root, and
            # that is a capability nobody should ever hold. A key is always
            # derived from two server-minted uuids, so an empty one is a bug in
            # this service and should be loud here rather than subtle there.
            raise ValueError("a blob URL needs a storage ref")
        query = urlencode({key: str(value) for key, value in terms.items()})
        return f"memory+s3://documents/{quote(storage_ref)}?{query}"

    def upload_url(
        self, storage_ref: str, *, content_type: str, byte_size: int, expires_in: int
    ) -> str:
        self.minted.append(
            MintedUrl(
                method="PUT",
                storage_ref=storage_ref,
                expires_in=expires_in,
                content_type=content_type,
                byte_size=byte_size,
            )
        )
        return self._url(
            storage_ref,
            method="PUT",
            content_type=content_type,
            content_length=byte_size,
            expires_in=expires_in,
        )

    def download_url(self, storage_ref: str, *, expires_in: int) -> str:
        self.minted.append(
            MintedUrl(method="GET", storage_ref=storage_ref, expires_in=expires_in)
        )
        return self._url(storage_ref, method="GET", expires_in=expires_in)

    def delete(self, storage_ref: str) -> None:
        # Idempotent, as the port requires and as S3 is: deleting an object
        # that never arrived is the ordinary outcome of removing a document
        # whose upload was abandoned.
        self.deleted.append(storage_ref)
