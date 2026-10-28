"""The cross-language proof for sealed data (issue #523, plan 18.12):
an envelope sealed by Python opens in .NET, and one sealed by .NET opens in
Python (ADR 0028: "Sealed data keeps its KMS envelopes byte-for-byte").

    python sealed_envelopes.py generate   # → python-sealed.json, beside this file
    python sealed_envelopes.py check      # opens dotnet-sealed.json; exit 1 on any failure

Both halves run the REAL Python code — insolvia_core's adapters/envelope.py,
KmsTaxIdCipher, KmsCredentialSealer/Opener, LocalTaxIdCipher and
LocalCredentialSealer/Opener — so it needs a venv with the package installed:

    python3.12 -m venv .venv && .venv/bin/pip install -e packages/insolvia_core

NO AWS, NO NETWORK. The KMS client is `StubKms`: GenerateDataKey hands out
one FIXED data key and wraps it the way KMS binds a context (AES-GCM under a
fixed stub key, the context as associated data), so a Decrypt under any
other context is refused here as KMS refuses it there. The .NET tests'
FakeKms is the same stub, byte for byte.

Every value is synthetic: tax ids in the 9xx area and the SSA's advertising
block (never issued), placeholder passwords, the RFC 6238 demo TOTP seed.
`dotnet-sealed.json` is written by the .NET suite when
INSOLVIA_EXPORT_DOTNET_SEALED names a path (SealedFixtureTests).
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import sys
import types
from collections.abc import Callable, Mapping
from pathlib import Path
from typing import Any, Final

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from insolvia_core.adapters import envelope as envelope_module
from insolvia_core.adapters.aws.filing_credentials import (
    KmsCredentialOpener,
    KmsCredentialSealer,
)
from insolvia_core.adapters.aws.tax_id_cipher import KmsTaxIdCipher
from insolvia_core.adapters.envelope import (
    NONCE_BYTES,
    context_aad,
    open_with_data_key,
    seal_with_data_key,
)
from insolvia_core.adapters.memory.filing_credentials import (
    LocalCredentialOpener,
    LocalCredentialSealer,
)
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.filing_credentials import (
    encryption_context as credential_context,
)
from insolvia_core.tax_ids import Envelope
from insolvia_core.tax_ids import encryption_context as tax_id_context

HERE: Final = Path(__file__).resolve().parent

# TEST CONSTANTS — they secure nothing and are committed on purpose. Never a
# real key: derived from a fixed sentence so both languages can recompute them.
TEST_DATA_KEY: Final = hashlib.sha256(
    b"insolvia sealed-envelope fixture data key - TEST CONSTANT, never a real key"
).digest()
STUB_KMS_WRAPPING_KEY: Final = hashlib.sha256(
    b"insolvia stub KMS wrapping key - TEST CONSTANT, never a real key"
).digest()

CASE_KEY: Final = "alias/insolvia-test-cases"
VAULT_KEY: Final = "alias/insolvia-test-filing-credentials"

FIRM: Final = "00000000-0000-4000-8000-0000000f1a01"
ATTORNEY: Final = "00000000-0000-4000-8000-00000000a771"


class StubKms:
    """GenerateDataKey / Decrypt as boto3 answers them, with no network."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def generate_data_key(self, **kwargs: Any) -> dict[str, bytes]:
        self.calls.append(("generate_data_key", kwargs))
        nonce = os.urandom(NONCE_BYTES)
        wrapped = nonce + AESGCM(STUB_KMS_WRAPPING_KEY).encrypt(
            nonce, TEST_DATA_KEY, context_aad(kwargs["EncryptionContext"])
        )
        return {"Plaintext": TEST_DATA_KEY, "CiphertextBlob": wrapped}

    def decrypt(self, **kwargs: Any) -> dict[str, bytes]:
        self.calls.append(("decrypt", kwargs))
        blob: bytes = kwargs["CiphertextBlob"]
        key = AESGCM(STUB_KMS_WRAPPING_KEY).decrypt(
            blob[:NONCE_BYTES], blob[NONCE_BYTES:], context_aad(kwargs["EncryptionContext"])
        )
        return {"Plaintext": key}


def credential_payload(password: str, totp_seed: str) -> str:
    # filing_credentials._payload, spelled out: it is private there.
    return json.dumps(
        {"password": password, "totp_seed": totp_seed},
        sort_keys=True,
        separators=(",", ":"),
    )


def b64(raw: bytes) -> str:
    return base64.b64encode(raw).decode("ascii")


def envelope_json(sealed: Envelope) -> dict[str, str]:
    return {
        "scheme": sealed.scheme,
        "ciphertext": sealed.ciphertext,
        "nonce": sealed.nonce,
        "wrapped_key": sealed.wrapped_key,
    }


def envelope_from(data: Mapping[str, str]) -> Envelope:
    return Envelope(
        scheme=data["scheme"],
        ciphertext=data["ciphertext"],
        nonce=data["nonce"],
        wrapped_key=data["wrapped_key"],
    )


# Contexts that exercise the canonical form: unsorted insertion order,
# non-ASCII (ensure_ascii escapes), JSON's short escapes, a control
# character, DEL, an astral character (a surrogate pair), and keys whose
# code-point order and UTF-16 order disagree (U+E000 vs U+1F600).
ODD_CONTEXTS: Final[list[tuple[str, dict[str, str], str]]] = [
    (
        "envelope-unsorted-ascii",
        {"tax_id_ref": "r-1", "purpose": "debtor-tax-id", "firm_id": FIRM},
        "987654321",
    ),
    (
        "envelope-escapes",
        {
            "purpose": "debtor-tax-id",
            "firm_id": "café \U0001f600 ☃",
            "tax_id_ref": 'q"b\\s/n\nt\tc\u0001d\u007fe\u0085',
        },
        "Zoë ☃ \U0001f600",
    ),
    (
        "envelope-code-point-order",
        {"\U0001f600": "astral", "": "private-use", "a": "ascii"},
        "900000002",
    ),
    ("envelope-empty-context", {}, "900000003"),
    ("envelope-empty-plaintext", {"purpose": "debtor-tax-id"}, ""),
]


def fixed_nonce(index: int) -> bytes:
    return hashlib.sha256(f"fixture nonce {index} - TEST ONLY".encode()).digest()[
        :NONCE_BYTES
    ]


def seal_with_nonce(
    plaintext: str, context: Mapping[str, str], nonce: bytes, wrapped: bytes
) -> Envelope:
    """The real seal_with_data_key, with its os.urandom pinned for one call
    so .NET can reproduce the ciphertext byte for byte."""
    real_os = envelope_module.os
    envelope_module.os = types.SimpleNamespace(urandom=lambda n: nonce[:n])  # type: ignore[assignment]
    try:
        return seal_with_data_key(TEST_DATA_KEY, plaintext, context=context, wrapped_key=wrapped)
    finally:
        envelope_module.os = real_os  # type: ignore[assignment]


def generate() -> None:
    cases: list[dict[str, Any]] = []

    def kms_case(name: str, kind: str, key_id: str, context: dict[str, str], plaintext: str) -> None:
        kms = StubKms()
        if kind == "kms_tax_id":
            sealed = KmsTaxIdCipher(key_id, client=kms).seal(plaintext, context=context)
        else:
            sealed = KmsCredentialSealer(key_id, client=kms).seal(plaintext, context=context)
        (verb, request), = kms.calls
        assert verb == "generate_data_key"
        cases.append(
            {
                "name": name,
                "kind": kind,
                "key_id": key_id,
                "context": context,
                "plaintext": plaintext,
                "aad": context_aad(context).decode("ascii"),
                "envelope": envelope_json(sealed),
                "generate_data_key_request": request,
            }
        )

    kms_case("kms-tax-id-ssa-block", "kms_tax_id", CASE_KEY,
             tax_id_context(firm_id=FIRM, ref="00000000-0000-4000-8000-0000000000e1"), "987654320")
    kms_case("kms-tax-id-9xx-area", "kms_tax_id", CASE_KEY,
             tax_id_context(firm_id=FIRM, ref="00000000-0000-4000-8000-0000000000e2"), "900000001")
    kms_case("kms-tax-id-itin-shaped", "kms_tax_id", CASE_KEY,
             tax_id_context(firm_id=FIRM, ref="00000000-0000-4000-8000-0000000000e3"), "900700001")
    kms_case("kms-credential", "kms_credential", VAULT_KEY,
             credential_context(firm_id=FIRM, attorney_id=ATTORNEY, credential_id="00000000-0000-4000-8000-0000000000c1"),
             credential_payload("FAKE-ECF-PASSWORD-fixture-1", "JBSWY3DPEHPK3PXP"))
    kms_case("kms-credential-escaped-password", "kms_credential", VAULT_KEY,
             credential_context(firm_id=FIRM, attorney_id=ATTORNEY, credential_id="00000000-0000-4000-8000-0000000000c2"),
             credential_payload('FAKE "quoted" \\ passé', "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"))

    local_tax = tax_id_context(firm_id=FIRM, ref="00000000-0000-4000-8000-0000000000e4")
    cases.append({
        "name": "local-tax-id", "kind": "local_tax_id", "context": local_tax, "plaintext": "987654329",
        "aad": context_aad(local_tax).decode("ascii"),
        "envelope": envelope_json(LocalTaxIdCipher().seal("987654329", context=local_tax)),
    })
    local_cred = credential_context(firm_id=FIRM, attorney_id=ATTORNEY, credential_id="00000000-0000-4000-8000-0000000000c3")
    local_payload = credential_payload("FAKE-ECF-PASSWORD-fixture-2", "JBSWY3DPEHPK3PXP")
    cases.append({
        "name": "local-credential", "kind": "local_credential", "context": local_cred, "plaintext": local_payload,
        "aad": context_aad(local_cred).decode("ascii"),
        "envelope": envelope_json(LocalCredentialSealer().seal(local_payload, context=local_cred)),
    })

    for index, (name, context, plaintext) in enumerate(ODD_CONTEXTS):
        nonce = fixed_nonce(index)
        cases.append({
            "name": name, "kind": "envelope", "context": context, "plaintext": plaintext,
            "aad": context_aad(context).decode("ascii"),
            "fixed_nonce": b64(nonce),
            "envelope": envelope_json(seal_with_nonce(plaintext, context, nonce, b"stub-wrapped-key")),
        })

    document = {
        "_comment": (
            "Sealed by insolvia_core (Python) with TEST CONSTANT keys - synthetic data only. "
            "Regenerate: python sealed_envelopes.py generate"
        ),
        "test_data_key": b64(TEST_DATA_KEY),
        "stub_kms_wrapping_key": b64(STUB_KMS_WRAPPING_KEY),
        "cases": cases,
    }
    target = HERE / "python-sealed.json"
    target.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(cases)} Python-sealed envelopes to {target.name}")


# ── check: open what .NET sealed ─────────────────────────────────


def opener_for(case: Mapping[str, Any], kms: StubKms) -> Callable[[Envelope, Mapping[str, str]], str]:
    kind = case["kind"]
    if kind == "kms_tax_id":
        cipher = KmsTaxIdCipher(case["key_id"], client=kms)
        return lambda e, c: cipher.open(e, context=c)
    if kind == "kms_credential":
        opener = KmsCredentialOpener(case["key_id"], client=kms)
        return lambda e, c: opener.open(e, context=c)
    if kind == "local_tax_id":
        return lambda e, c: LocalTaxIdCipher().open(e, context=c)
    if kind == "local_credential":
        return lambda e, c: LocalCredentialOpener().open(e, context=c)
    if kind == "envelope":
        return lambda e, c: open_with_data_key(TEST_DATA_KEY, e, context=c)
    raise ValueError(f"unknown kind {kind!r}")


def flip(text: str, index: int) -> str:
    raw = bytearray(base64.b64decode(text))
    raw[index] ^= 0x01
    return b64(bytes(raw))


def tampers(case: Mapping[str, Any]) -> list[tuple[str, Envelope, dict[str, str]]]:
    sealed = envelope_from(case["envelope"])
    context = dict(case["context"])
    variants: list[tuple[str, Envelope, dict[str, str]]] = [
        ("extra context field", sealed, {**context, "case_id": "x"}),
        ("ciphertext bit flipped", Envelope(sealed.scheme, flip(sealed.ciphertext, 0), sealed.nonce, sealed.wrapped_key), context),
        ("tag bit flipped", Envelope(sealed.scheme, flip(sealed.ciphertext, -1), sealed.nonce, sealed.wrapped_key), context),
        ("ciphertext truncated", Envelope(sealed.scheme, b64(base64.b64decode(sealed.ciphertext)[:-1]), sealed.nonce, sealed.wrapped_key), context),
        ("nonce bit flipped", Envelope(sealed.scheme, sealed.ciphertext, flip(sealed.nonce, 0), sealed.wrapped_key), context),
        ("unknown scheme", Envelope("kms-aes256-gcm/2", sealed.ciphertext, sealed.nonce, sealed.wrapped_key), context),
    ]
    if "firm_id" in context:
        variants.append(("firm_id swapped", sealed, {**context, "firm_id": "00000000-0000-4000-8000-0000000f1a02"}))
    if "purpose" in context:
        variants.append(("purpose changed", sealed, {**context, "purpose": "something-else"}))
    if context:
        variants.append(("a context field dropped", sealed, dict(list(context.items())[1:])))
    return variants


def check() -> int:
    source = HERE / "dotnet-sealed.json"
    document = json.loads(source.read_text(encoding="utf-8"))
    assert base64.b64decode(document["test_data_key"]) == TEST_DATA_KEY, "the .NET test data key differs"
    assert base64.b64decode(document["stub_kms_wrapping_key"]) == STUB_KMS_WRAPPING_KEY
    failures: list[str] = []
    opened = refused = tamper_total = 0
    cases = document["cases"]
    for case in cases:
        name = case["name"]
        context: dict[str, str] = case["context"]
        if context_aad(context).decode("ascii") != case["aad"]:
            failures.append(f"{name}: .NET AAD {case['aad']!r} != Python {context_aad(context)!r}")
        kms = StubKms()
        open_ = opener_for(case, kms)
        try:
            plaintext = open_(envelope_from(case["envelope"]), context)
        except Exception as error:  # noqa: BLE001 - every failure is a finding
            failures.append(f"{name}: did not open: {error!r}")
            continue
        if plaintext != case["plaintext"]:
            failures.append(f"{name}: opened to the wrong plaintext")
            continue
        if case["kind"].startswith("kms_"):
            (verb, request), = kms.calls
            if verb != "decrypt" or request["KeyId"] != case["key_id"] or request["EncryptionContext"] != context:
                failures.append(f"{name}: unexpected Decrypt request {request!r}")
            if case["generate_data_key_request"] != {"KeyId": case["key_id"], "KeySpec": "AES_256", "EncryptionContext": context}:
                failures.append(f"{name}: .NET GenerateDataKey request differs from Python's")
        if "fixed_nonce" in case:
            mine = seal_with_nonce(case["plaintext"], context, base64.b64decode(case["fixed_nonce"]), b"stub-wrapped-key")
            if envelope_json(mine) != case["envelope"]:
                failures.append(f"{name}: Python sealed different bytes under the same key and nonce")
        opened += 1
        for label, tampered, tampered_context in tampers(case):
            tamper_total += 1
            try:
                opener_for(case, StubKms())(tampered, tampered_context)
            except Exception:  # noqa: BLE001 - refusing is the point
                refused += 1
            else:
                failures.append(f"{name}: tamper '{label}' OPENED")
    print(f".NET -> Python: opened {opened}/{len(cases)}")
    print(f"tamper cases refused by Python: {refused}/{tamper_total}")
    for failure in failures:
        print(f"FAIL {failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "generate":
        generate()
    elif command == "check":
        sys.exit(check())
    else:
        print(__doc__)
        sys.exit(2)
