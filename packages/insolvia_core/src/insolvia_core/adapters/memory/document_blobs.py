from __future__ import annotations

import hashlib
from dataclasses import dataclass
from urllib.parse import quote, urlencode

from insolvia_core.documents import UPLOAD_TAG, StoredBlob


@dataclass(frozen=True)
class MintedUrl:
    """One capability this store handed out — what the real one binds into a
    signature, kept where a test can read it."""

    method: str
    storage_ref: str
    expires_in: int
    content_type: str | None = None
    byte_size: int | None = None
    # `upload=unconfirmed` on a PUT, None on a GET. Recorded because it is a
    # term the real store binds into the signature exactly like the others, and
    # because it is the only thing that makes an abandoned object reapable — a
    # route that stopped asking for it would leave bytes nothing can find, and
    # that must fail here rather than on staging.
    tag: str | None = None


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
        # The objects a client "uploaded" — see accept_upload. Empty by
        # default, which is the honest starting state: minting a capability is
        # not an upload, and a test that confirms without calling accept_upload
        # is testing exactly the case the confirm route must refuse.
        self.objects: dict[str, StoredBlob] = {}
        # Which of those still carry `upload=unconfirmed`, and therefore which
        # the bucket's lifecycle rule would reap. A test can assert on the set
        # rather than on a call it hopes happened.
        self.tagged: set[str] = set()
        # Every clear_upload_tag, in order, including repeats — the route is
        # idempotent and a test should be able to see that it stayed so.
        self.cleared: list[str] = []
        # Bytes written server-side through put_bytes (the packet worker's
        # path), kept whole so a test can read back what was stored.
        self.contents: dict[str, bytes] = {}
        self.content_types: dict[str, str] = {}

    def accept_upload(
        self,
        storage_ref: str,
        *,
        byte_size: int,
        etag: str = "0" * 32,
        content: bytes | None = None,
    ) -> None:
        """Stand in for the client's PUT through a minted capability.

        NOT part of DocumentBlobStore, and deliberately not: nothing in the
        service ever writes an object — the client does, straight to S3. This
        is the seam a test uses to say "the bytes arrived", and it tags what it
        writes because every real PUT through one of our URLs does. `content`
        is optional because most tests only care THAT bytes arrived; a test
        driving the extraction worker passes the bytes it wants get_bytes to
        answer with.
        """
        self.objects[storage_ref] = StoredBlob(byte_size=byte_size, etag=etag)
        self.tagged.add(storage_ref)
        if content is not None:
            self.contents[storage_ref] = content

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
                tag=UPLOAD_TAG,
            )
        )
        return self._url(
            storage_ref,
            method="PUT",
            content_type=content_type,
            content_length=byte_size,
            expires_in=expires_in,
            tagging=UPLOAD_TAG,
        )

    def download_url(self, storage_ref: str, *, expires_in: int) -> str:
        self.minted.append(
            MintedUrl(method="GET", storage_ref=storage_ref, expires_in=expires_in)
        )
        return self._url(storage_ref, method="GET", expires_in=expires_in)

    def stat(self, storage_ref: str) -> StoredBlob | None:
        # None for an object nobody uploaded — the same answer the real store
        # gives, which reaches it by way of a 403 rather than a 404 because
        # this service holds no s3:ListBucket. The port forbids distinguishing
        # the two, so there is nothing to model here.
        return self.objects.get(storage_ref)

    def clear_upload_tag(self, storage_ref: str) -> None:
        # Idempotent, like the real PutObjectTagging with an empty set:
        # clearing tags that are already clear is a successful no-op, so a
        # retried confirm does not fail on its second pass.
        self.cleared.append(storage_ref)
        self.tagged.discard(storage_ref)

    def delete(self, storage_ref: str) -> None:
        # Idempotent, as the port requires and as S3 is: deleting an object
        # that never arrived is the ordinary outcome of removing a document
        # whose upload was abandoned.
        self.deleted.append(storage_ref)
        self.objects.pop(storage_ref, None)
        self.tagged.discard(storage_ref)
        self.contents.pop(storage_ref, None)

    def get_bytes(self, storage_ref: str) -> bytes | None:
        # None both for an object nobody uploaded and for one accepted
        # without content — the same "absent" the real store answers, which
        # cannot distinguish missing from denied either (see the port).
        return self.contents.get(storage_ref)

    def put_bytes(self, storage_ref: str, *, content: bytes, content_type: str) -> None:
        # The one server-side write (the packet worker's — see the port). The
        # bytes are kept so a test can open the zip it just assembled; no
        # unconfirmed tag, because no capability was minted.
        if not storage_ref:
            raise ValueError("a blob write needs a storage ref")
        self.objects[storage_ref] = StoredBlob(
            byte_size=len(content),
            # md5 because that IS S3's etag for a single-part put — identity,
            # not security.
            etag=hashlib.md5(content, usedforsecurity=False).hexdigest(),
        )
        self.contents[storage_ref] = content
        self.content_types[storage_ref] = content_type
