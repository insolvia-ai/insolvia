"""The debtor's tax identifier (issue 13.12 / #382): what is stored, how it is
sealed, and the two reads — last four for screens, the logged full value for
the one form that prints it.

docs/reference/case-data-model.md makes the identifier a special case: B101
asks only for the last four digits, but B121 (and the IEPD's e-filing
package) print the whole number, so the full value must be stored — encrypted,
with only the last four ever served by default, and the full value behind an
explicit read that writes an audit record. `parse_debtor` refused a tax id
outright until this module existed, and this module is what makes refusing it
unnecessary.

## What is stored, and where

The DEBTOR carries a reference, never the number:

    tax_id: { kind: ssn | itin, last_four, ref }

The ciphertext is ITS OWN ITEM, addressed by that reference:

    PK  CASE#<case_id>          where it lives today
    SK  TAXID#<ref>             the reference IS the identity

THE REF, NOT THE LOCATION, IS THE CONTRACT. ADR 0022 (a client is not a
case) needs a client's later matter — a refiled Chapter 7, a conversion — to
point at the SAME sealed identifier rather than copying the secret into a
second case, so the debtor names the item by an opaque, generated id
(`tax_id_ref`) rather than by "the tax id of this case's debtor_1". The item
sits in the case's partition for now because nothing else exists to hold it;
the ADR's backfill will re-parent it, and nothing here may assume the
partition beyond the store's `get`/`put` taking a location hint. A ref is a
uuid4: unguessable, so a ciphertext cannot be re-addressed onto another
debtor by naming a ref someone might have.

## How it is sealed — envelope encryption under the case key

Every seal mints a fresh 256-bit data key under the environment's case KMS
key (infra/modules/case_store — the same key the table is encrypted under,
so one deny covers both), encrypts the digits with it (AES-256-GCM), and
stores the ciphertext beside the data key in its KMS-wrapped form. Plaintext
never lands in an item, a log line, or a response body.

ONE DATA KEY PER SEALED VALUE, deliberately — not per case and not per
debtor. A per-case key would have to live somewhere (a case-level item),
outlive the values it protects, and be rotated on its own schedule, all to
save a KMS call on a write path that runs once per debtor per intake and a
read path that runs once per B121 render. A key that lives exactly as long
as its ciphertext has no lifecycle of its own: re-sealing mints a new key,
and re-parenting the item under ADR 0022 moves the key with it.

THE ENCRYPTION CONTEXT BINDS THE FIRM AND THE REF: `{purpose, firm_id,
tax_id_ref}`. KMS refuses to unwrap the data key under any other context, and
the same context is the AES-GCM associated data, so the ciphertext cannot be
replayed onto another firm's debtor (the firm differs) or onto a different
identifier (the ref differs), while ONE ref legitimately serving two cases of
one client is exactly what the binding allows. The context is deliberately
NOT the case id: under ADR 0022 the case is where the item happens to live,
not what it belongs to. `purpose` is what the IAM grant conditions on
(`kms:EncryptionContext:purpose`), so the API role can generate and open
these data keys directly and nothing else — every other KMS use it holds is
fenced to DynamoDB or S3 as the calling service.

## The two reads

- `Debtor.tax_id.last_four` is on the plain item and travels with every
  debtor read: the screens, B101's line 3, the generic routes, the MCP
  tools. Nothing needs a key to show the last four.
- `read_tax_id` is the full value: it opens the envelope AND records a
  `taxid.read` access event naming who, which case, which debtor, and why
  (`purpose` — `b121` for the statement, `petition_review` for the review's
  byte-exact re-assembly). It is called by packet assembly and the single-form
  preview when B121 is rendered, and by nothing that answers a client: no
  route returns the full value, and the MCP surface has no tool for it
  (docs/reference/mcp-surface.md).

## The shape rules

Both kinds are nine digits; the caller may type the customary dashes and
they are stripped on the way in. An SSN's area (first three) is never 000,
666, or 900-999, its group (middle two) never 00, its serial (last four)
never 0000. An ITIN is the 900-999 block an SSN can never be, with a group
in the IRS's issued ranges (50-65, 70-88, 90-92, 94-99). So the first digit
decides the kind, and a value that claims to be one kind while shaped like
the other is refused rather than silently re-labelled.

ONE DELIBERATE EXCEPTION: the Social Security Administration has reserved
987-65-4320 through 987-65-4329 for advertising and has said it will never
issue them — the tax-id analogue of RFC 2606's `.test`. Those ten are
accepted as SSNs despite the 9xx rule, so a committed fixture
(seeds/fixtures/, the projection goldens) can carry a number that is
obviously synthetic AND passes the same parser a real request goes through.
No such block exists for ITINs; a synthetic ITIN in a fixture is merely
structurally valid, and the fixture says so.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import dataclass
from typing import TYPE_CHECKING, Final

from insolvia_core.errors import FieldValidationError

from .access_log import record_access
from .fields import mapping as _mapping
from .fields import timestamp as _timestamp

if TYPE_CHECKING:
    from .ports import AccessLog, TaxIdCipher, TaxIdStore

__all__ = [
    "SEALED_SCHEME",
    "TAX_ID_KINDS",
    "TAX_ID_PURPOSE",
    "Envelope",
    "SealedTaxId",
    "TaxIdInput",
    "TaxIdRef",
    "encryption_context",
    "format_for_b121",
    "new_tax_id_ref",
    "parse_tax_id",
    "read_tax_id",
    "sort_key",
    "store_tax_id",
    "tax_id_from_item",
    "tax_id_item",
    "tax_id_json",
]

TAX_ID_KINDS: Final = ("ssn", "itin")

# The one value the IAM grant conditions on (infra/modules/case_store's
# TaxIdKeyUse statement: `kms:EncryptionContext:purpose`). Renaming it here
# without renaming it there does not fail an apply — every seal starts
# failing with AccessDenied in the deployed environments and nowhere else.
TAX_ID_PURPOSE: Final = "debtor-tax-id"

# Stamped into every envelope so a later scheme (a different cipher, a
# different wrapping) can coexist with items sealed under this one.
SEALED_SCHEME: Final = "kms-aes256-gcm/1"

# The SSA's advertising block — never issued, and accepted here on purpose.
# See the module docstring.
_SSA_ADVERTISING_BLOCK: Final = frozenset(f"98765432{n}" for n in range(10))

# IRS-issued ITIN group ranges, inclusive.
_ITIN_GROUPS: Final = ((50, 65), (70, 88), (90, 92), (94, 99))


@dataclass(frozen=True)
class TaxIdInput:
    """What a write carries, after parsing. EXACTLY ONE of `value` and
    `last_four` is set:

    - `value`: the full nine digits, freshly typed. Sealed on the way in.
    - `last_four`: the client echoing the view it was given, meaning "keep
      what is stored". A PUT replaces the whole record (parse_debtor says
      why), and a client only ever holds the last four, so this is how a
      save that did not touch the number carries it across.
    """

    kind: str
    value: str | None = None
    last_four: str | None = None


@dataclass(frozen=True)
class TaxIdRef:
    """What the debtor record carries — the kind, the last four, and the
    reference to the sealed item. Never the number."""

    kind: str
    last_four: str
    ref: str


@dataclass(frozen=True)
class Envelope:
    """One sealed value: the ciphertext, the AES-GCM nonce, and the data key
    in its KMS-wrapped form — all base64 text, so the item is plain strings.
    Opaque to everything but the cipher adapters."""

    scheme: str
    ciphertext: str
    nonce: str
    wrapped_key: str


@dataclass(frozen=True)
class SealedTaxId:
    """The stored item: the envelope plus the facts the encryption context
    binds (`firm_id`, `ref`) and the two plain facts the views need."""

    ref: str
    firm_id: str
    kind: str
    last_four: str
    envelope: Envelope
    created_at: str
    updated_at: str


def new_tax_id_ref() -> str:
    return str(uuid.uuid4())


def encryption_context(*, firm_id: str, ref: str) -> dict[str, str]:
    """The KMS encryption context AND the AES-GCM associated data for one
    sealed value — the module docstring says what each member buys."""
    return {"purpose": TAX_ID_PURPOSE, "firm_id": firm_id, "tax_id_ref": ref}


# ── The shape rules ─────────────────────────────────────────────


def _digits(value: str) -> str | None:
    """Nine digits, or None when the text is not a tax id at all. Dashes
    and spaces between the groups are tolerated — the customary spelling —
    but nothing else is."""
    stripped = "".join(ch for ch in value if ch not in "- ")
    if len(stripped) != 9 or not stripped.isdigit():
        return None
    return stripped


def _ssn_problem(digits: str) -> str | None:
    if digits in _SSA_ADVERTISING_BLOCK:
        return None
    area, group, serial = digits[:3], digits[3:5], digits[5:]
    if area == "000" or area == "666" or area.startswith("9"):
        return "Not a Social Security number — no SSN begins with 000, 666 or 9."
    if group == "00" or serial == "0000":
        return "Not a Social Security number — the middle or last group is all zeros."
    return None


def _itin_problem(digits: str) -> str | None:
    if not digits.startswith("9"):
        return "Not an ITIN — every ITIN begins with 9."
    group = int(digits[3:5])
    if not any(low <= group <= high for low, high in _ITIN_GROUPS):
        return "Not an ITIN — the middle two digits are outside the IRS's ranges."
    return None


def parse_tax_id(value: object, path: str, errors: dict[str, str]) -> TaxIdInput | None:
    """Validate a `tax_id` body member: `{kind, value}` for a number being
    entered, `{kind, last_four}` for one being kept. Absent is fine — intake
    is progressive — and every problem lands in `errors` under `path`."""
    if value is None:
        return None
    raw = _mapping(value, path, errors)
    if not raw and path in errors:
        return None

    kind = raw.get("kind")
    if kind not in TAX_ID_KINDS:
        errors[f"{path}.kind"] = "Must be one of " + ", ".join(TAX_ID_KINDS) + "."
        return None

    full = raw.get("value")
    kept = raw.get("last_four")
    if full is not None and kept is not None:
        errors[path] = "Send either the number or the last four, not both."
        return None
    if full is None and kept is None:
        errors[path] = "A tax id needs its number (or the last four, to keep it)."
        return None

    if kept is not None:
        if not isinstance(kept, str) or len(kept) != 4 or not kept.isdigit():
            errors[f"{path}.last_four"] = "Must be exactly four digits."
            return None
        return TaxIdInput(kind=kind, last_four=kept)

    if not isinstance(full, str):
        errors[f"{path}.value"] = "Must be text."
        return None
    digits = _digits(full)
    if digits is None:
        errors[f"{path}.value"] = "Must be nine digits, with or without dashes."
        return None
    problem = _ssn_problem(digits) if kind == "ssn" else _itin_problem(digits)
    if problem is not None:
        errors[f"{path}.value"] = problem
        return None
    return TaxIdInput(kind=kind, value=digits)


def format_for_b121(kind: str, digits: str) -> str:
    """How B121 prints the number: an SSN as `000-00-0000`; an ITIN as the
    ten characters AFTER the pre-printed leading 9 (`00-00-0000`), because
    that is what the form's box holds (forms/specs/b121.json)."""
    if kind == "itin":
        return f"{digits[1:3]}-{digits[3:5]}-{digits[5:]}"
    return f"{digits[:3]}-{digits[3:5]}-{digits[5:]}"


# ── The item and the wire shape ─────────────────────────────────


def sort_key(ref: str) -> str:
    return f"TAXID#{ref}"


def tax_id_json(tax_id: TaxIdRef) -> dict[str, str]:
    """The API representation — the last-four VIEW. The ref is server
    plumbing and the number is never here; a client keeps a stored value by
    echoing exactly this back (see TaxIdInput)."""
    return {"kind": tax_id.kind, "last_four": tax_id.last_four}


def tax_id_item(sealed: SealedTaxId) -> dict[str, object]:
    """The stored item. The location (PK) is passed by the store that writes
    it — see the module docstring on why the ref, not the partition, is the
    identity."""
    return {
        "SK": sort_key(sealed.ref),
        "ref": sealed.ref,
        "firmId": sealed.firm_id,
        "kind": sealed.kind,
        "lastFour": sealed.last_four,
        "scheme": sealed.envelope.scheme,
        "ciphertext": sealed.envelope.ciphertext,
        "nonce": sealed.envelope.nonce,
        "wrappedKey": sealed.envelope.wrapped_key,
        "createdAt": sealed.created_at,
        "updatedAt": sealed.updated_at,
    }


def tax_id_from_item(item: Mapping[str, object]) -> SealedTaxId:
    def text(key: str) -> str:
        value = item.get(key)
        if not isinstance(value, str) or not value:
            raise ValueError(f"sealed tax id item is missing {key}")
        return value

    return SealedTaxId(
        ref=text("ref"),
        firm_id=text("firmId"),
        kind=text("kind"),
        last_four=text("lastFour"),
        envelope=Envelope(
            scheme=text("scheme"),
            ciphertext=text("ciphertext"),
            nonce=text("nonce"),
            wrapped_key=text("wrappedKey"),
        ),
        created_at=text("createdAt"),
        updated_at=text("updatedAt"),
    )


# ── The write, and the logged read ──────────────────────────────


def store_tax_id(
    given: TaxIdInput | None,
    *,
    existing: TaxIdRef | None,
    firm_id: str,
    case_id: str,
    cipher: TaxIdCipher,
    store: TaxIdStore,
) -> TaxIdRef | None:
    """Turn a parsed write into what the debtor record will carry.

    - Nothing given: the debtor carries no tax id. The sealed item, if one
      exists, is LEFT IN PLACE — under ADR 0022 it may be another matter's
      pointer too, and its lifecycle belongs to the client, not to this
      case's debtor record. Nothing here deletes.
    - A number: sealed under the debtor's EXISTING ref when it has one (a
      correction of the client's identifier reaches every matter that points
      at it) or a fresh ref when it has none; the item is written whole.
    - The last four alone: the client is keeping what is stored. Refused —
      as the same 400 any malformed field gets — when nothing is stored or
      the echo does not match, because silently keeping a value the client
      described differently would be storing something nobody asserted.
    """
    if given is None:
        return None
    if given.value is None:
        if (
            existing is None
            or existing.kind != given.kind
            or existing.last_four != given.last_four
        ):
            raise FieldValidationError(
                {
                    "tax_id": "The stored tax id does not match — enter the "
                    "full number to replace it."
                }
            )
        return existing

    ref = existing.ref if existing is not None else new_tax_id_ref()
    now = _timestamp()
    previous = store.get(case_id, ref)
    envelope = cipher.seal(
        given.value, context=encryption_context(firm_id=firm_id, ref=ref)
    )
    store.put(
        case_id,
        SealedTaxId(
            ref=ref,
            firm_id=firm_id,
            kind=given.kind,
            last_four=given.value[-4:],
            envelope=envelope,
            created_at=previous.created_at if previous is not None else now,
            updated_at=now,
        ),
    )
    return TaxIdRef(kind=given.kind, last_four=given.value[-4:], ref=ref)


def read_tax_id(
    tax_id: TaxIdRef,
    *,
    firm_id: str,
    case_id: str,
    filing_role: str,
    principal: str,
    purpose: str,
    cipher: TaxIdCipher,
    store: TaxIdStore,
    access_log: AccessLog,
) -> str | None:
    """THE FULL VALUE — the nine digits — and the one place it is produced.

    Records a `taxid.read` access event FIRST, naming who, which case, which
    debtor and why, so a read that then fails still shows as attempted; then
    opens the envelope under the context the item was sealed with. A ref the
    store does not hold answers None (the debtor points at an item that has
    not been re-parented yet, or was never written), which the projection
    renders as a blank line rather than a 500.

    Never call this from anything that answers a client. Every caller today
    is a form render: packet assembly and the single-form preview for B121,
    and the review worker's byte-exact re-assembly of the packet.
    """
    # THE ONE CALL SITE that writes a taxid.* row.
    access_log.record(
        record_access(
            case_id=case_id,
            principal=principal,
            action="taxid.read",
            filing_role=filing_role,
            purpose=purpose,
        )
    )
    sealed = store.get(case_id, tax_id.ref)
    if sealed is None:
        return None
    return cipher.open(
        sealed.envelope,
        context=encryption_context(firm_id=firm_id, ref=sealed.ref),
    )
