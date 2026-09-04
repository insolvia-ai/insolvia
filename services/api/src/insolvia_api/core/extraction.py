"""AI extraction — documents into candidate records (issues #87 / 8.7, #88 / 8.8).

The no-double-entry pitch, made concrete: a preparer uploads a credit report
or a pay stub, and this worker turns it into CANDIDATE records — creditors
and claims for Schedules D/E/F (8.7), employment and dated pay-period rows
for the means test's six-month lookback (8.8). Candidates, never case data:
every row lands in the shared candidate store (insolvia_core.candidates,
origin channel `extraction`) and enters the case only when a human accepts
it through the review flow (8.9) — the confirm-before-entry invariant
docs/reference/case-data-model.md enforces in the store itself.

This is a PIPELINE WORKER (ADR 0015/0018), the `document_extraction` job
kind: a document upload triggers it (api/routes/documents.py's complete
route) or a preparer requests it (POST /v1/cases/<id>/jobs with a
documentId), and the worker Lambda runs this. It inherits ADR 0019's Claude
posture whole: the call runs in this worker, directly against the Anthropic
API, on the model id adapters/anthropic/review_model.py owns, keyed by the
same SSM-sourced ANTHROPIC_API_KEY, under the API's no-training standing.

WHAT LEAVES FOR THE MODEL API, stated plainly because it differs from the
petition review's input: the DOCUMENT ITSELF — the uploaded PDF or image,
base64, plus a fixed instruction. A credit report carries the debtor's own
tax identifiers on its face and no scrub can remove them from bytes we must
send whole; ADR 0019's amendment for extraction records why that is the
honest shape and what covers it (the no-training standing and the ZDR
follow-up). What the scrub DOES govern is everything we construct: the
prompts carry no case data at all, and every extracted payload is passed
through core/petition_review.scrub before storage, so a full SSN or account
number the model echoes never lands in a candidate row. Account numbers are
additionally coerced to last-four structurally (`_last4`), the same rule the
claim parser enforces.

Structured outputs, per ADR 0019: one Messages call constrained to the
per-kind schema below — never prose fished for JSON — and the answer is then
re-validated through the SAME insolvia_core parse functions every other
write path uses, so a candidate that could not be confirmed is never stored.
A record the model got structurally wrong is dropped and counted (the
`skipped` figure on the job result) rather than failing the run: the human
reviews what parsed, and the count is the quality signal.

CROSS-RECORD LINKS use candidate-id indirection: a claim's `creditor_id` and
a pay period's `employment_id` name the CANDIDATE id of the creditor or
employment extracted alongside them (the store validates shape only, so an
unresolvable id persists exactly as a dangling reference does everywhere
else). The review flow resolves the indirection at acceptance — accepting a
claim whose creditor candidate was itself accepted rewrites the reference to
the real record's id. That keeps "which creditor does this claim belong to"
answerable across the review without inventing record ids before a human
has confirmed anything.

Everything except the model call and the store reads is pure and runs under
pytest with the memory adapters — the local story ADR 0018 requires.
"""

from __future__ import annotations

import logging
import re
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Final

from insolvia_core.access_log import record_access
from insolvia_core.candidates import (
    CandidateOrigin,
    ProposalDraft,
    create_candidate,
)
from insolvia_core.claims import CLAIM, CLAIM_CLASSES
from insolvia_core.creditors import CREDITOR
from insolvia_core.errors import FieldValidationError
from insolvia_core.income import (
    DEDUCTION_CATEGORIES,
    EMPLOYMENT,
    PAY_FREQUENCIES,
    PAY_PERIOD_RECORD,
)

from insolvia_api.core.jobs import Job, JobError
from insolvia_api.core.petition_review import scrub

if TYPE_CHECKING:
    from insolvia_core.case_entities import EntityKind
    from insolvia_core.documents import Document
    from insolvia_core.ports import (
        AccessLog,
        CandidateStore,
        CaseStore,
        DocumentBlobStore,
        DocumentStore,
    )

    from insolvia_api.core.ports import ExtractionModel

logger = logging.getLogger(__name__)

# The job kind the accept endpoint validates and the worker registries key on
# (core/jobs.KINDS and DOCUMENT_SCOPED_KINDS both name it).
DOCUMENT_EXTRACTION_KIND: Final = "document_extraction"

# Which document kinds have an extractor. The document's `kind` is the
# uploader's claim (core/documents.KINDS) — the prompt tells the model to
# answer empty when the bytes are not actually what the label says.
EXTRACTABLE_DOCUMENT_KINDS: Final = ("credit_report", "pay_stub")

# What the model API accepts as document input: PDFs as `document` blocks,
# JPEG/PNG as `image` blocks. HEIC and TIFF are on our upload allowlist
# (phone photos, fax-era scans) but not on the model API's — extraction of
# those fails deterministically with an honest message rather than sending
# bytes the API will refuse.
MODEL_INPUT_CONTENT_TYPES: Final = ("application/pdf", "image/jpeg", "image/png")

# Raw-byte ceiling for one extraction. Base64 grows bytes by 4/3 and the
# Messages API caps a request at 32 MB, so 20 MiB of document (~27 MB
# encoded, plus the prompt) stays safely under it. Documents up to the upload
# cap (50 MiB) exist; they fail deterministically with an honest message.
MAX_DOCUMENT_BYTES: Final = 20 * 1024 * 1024

# A runaway generation must not write hundreds of rows into a paralegal's
# review queue. Far beyond any real consumer credit report.
MAX_CANDIDATES_PER_RUN: Final = 200

# SSN-shaped strings are additionally scrubbed by core/petition_review.scrub
# (imported above); this pattern pulls the LAST FOUR from whatever shape an
# account number arrives in, so a model that echoes a full number despite the
# prompt still stores only what the schedules print.
_DIGITS: Final = re.compile(r"\d")

# What a money string may arrive as: the schema asks for "1234.56", and this
# strips the currency furniture ("$1,234.56") a model may add anyway. The
# core money parser then validates the result for real.
_MONEY_FURNITURE: Final = re.compile(r"[$,\s]")


# ── The structured-output contracts ─────────────────────────────
# One schema per document kind, kept beside its prompt (its other half) and
# imported by the Anthropic adapter — one owner, so the adapter and the
# coercion below cannot drift apart. Nullable-by-union everywhere a stub or
# report may simply not show the fact: absent survives as null and is pruned
# before validation, matching progressive intake.

_NULLABLE_STRING: Final = {"type": ["string", "null"]}
_PAGE: Final = {
    "type": ["integer", "null"],
    "description": "1-based page of the document this record was read from,"
    " or null when unclear.",
}
_CONFIDENCE: Final = {
    "type": "number",
    "description": "How certain you are this record is read correctly, 0-1.",
}
_ADDRESS_SCHEMA: Final = {
    "type": "object",
    "properties": {
        "line1": _NULLABLE_STRING,
        "line2": _NULLABLE_STRING,
        "city": _NULLABLE_STRING,
        "state": _NULLABLE_STRING,
        "postal_code": _NULLABLE_STRING,
        "raw": {
            "type": ["string", "null"],
            "description": "The address exactly as printed, when it does not"
            " split cleanly into the parts.",
        },
    },
    "required": ["line1", "line2", "city", "state", "postal_code", "raw"],
    "additionalProperties": False,
}

CREDIT_REPORT_OUTPUT_SCHEMA: Final[dict[str, Any]] = {
    "type": "object",
    "properties": {
        "creditors": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "The creditor as the mailing matrix"
                        " should print it — issuer or current servicer name.",
                    },
                    "address": _ADDRESS_SCHEMA,
                    "page": _PAGE,
                    "confidence": _CONFIDENCE,
                    "claims": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "account_last4": {
                                    "type": ["string", "null"],
                                    "description": "Last four digits of the"
                                    " account number ONLY — never more.",
                                },
                                "amount": {
                                    "type": ["string", "null"],
                                    "description": "Balance owed as a plain"
                                    ' decimal string, like "1234.56".',
                                },
                                "date_incurred": {
                                    "type": ["string", "null"],
                                    "description": "Date the account was"
                                    " opened, YYYY-MM-DD.",
                                },
                                "claim_class": {
                                    "type": "string",
                                    "enum": list(CLAIM_CLASSES),
                                    "description": "secured for collateralised"
                                    " debts (auto, mortgage);"
                                    " nonpriority_unsecured for ordinary"
                                    " consumer debt; priority_unsecured only"
                                    " for taxes or support obligations.",
                                },
                                "disputed": {
                                    "type": ["boolean", "null"],
                                    "description": "True only when the report"
                                    " marks the account disputed.",
                                },
                                "page": _PAGE,
                                "confidence": _CONFIDENCE,
                            },
                            "required": [
                                "account_last4",
                                "amount",
                                "date_incurred",
                                "claim_class",
                                "disputed",
                                "page",
                                "confidence",
                            ],
                            "additionalProperties": False,
                        },
                    },
                },
                "required": ["name", "address", "page", "confidence", "claims"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["creditors"],
    "additionalProperties": False,
}

PAY_STUB_OUTPUT_SCHEMA: Final[dict[str, Any]] = {
    "type": "object",
    "properties": {
        "employer": {
            "type": "object",
            "properties": {
                "name": _NULLABLE_STRING,
                "address": _ADDRESS_SCHEMA,
                "page": _PAGE,
                "confidence": _CONFIDENCE,
            },
            "required": ["name", "address", "page", "confidence"],
            "additionalProperties": False,
        },
        "pay_periods": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "period_start": {
                        "type": ["string", "null"],
                        "description": "First day of the pay period, YYYY-MM-DD.",
                    },
                    "period_end": {
                        "type": ["string", "null"],
                        "description": "Last day of the pay period, YYYY-MM-DD.",
                    },
                    "pay_date": {
                        "type": ["string", "null"],
                        "description": "The date the check was issued,"
                        " YYYY-MM-DD — distinct from the period; copy it"
                        " exactly, it drives a statutory lookback window.",
                    },
                    "gross": _NULLABLE_STRING,
                    "net": _NULLABLE_STRING,
                    "frequency": {
                        "type": ["string", "null"],
                        "enum": [*PAY_FREQUENCIES, None],
                    },
                    "deductions": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "category": {
                                    "type": "string",
                                    "enum": list(DEDUCTION_CATEGORIES),
                                },
                                "amount": _NULLABLE_STRING,
                                "description": {
                                    "type": ["string", "null"],
                                    "description": "The stub's own wording"
                                    " for this line.",
                                },
                            },
                            "required": ["category", "amount", "description"],
                            "additionalProperties": False,
                        },
                    },
                    "page": _PAGE,
                    "confidence": _CONFIDENCE,
                },
                "required": [
                    "period_start",
                    "period_end",
                    "pay_date",
                    "gross",
                    "net",
                    "frequency",
                    "deductions",
                    "page",
                    "confidence",
                ],
                "additionalProperties": False,
            },
        },
    },
    "required": ["employer", "pay_periods"],
    "additionalProperties": False,
}

# What the model is, to the document. Shared rules first; the per-kind half
# is appended below. "Answer empty when the label is wrong" is what makes
# the uploader's `kind` claim safe to dispatch on.
_SHARED_PROMPT_RULES: Final = """\
Rules, all of them binding:
- Extract only what the document actually shows. Never infer, estimate, or
  fill a value the page does not state; use null for anything absent.
- Amounts are plain decimal strings with two places, like "1234.56" — no
  currency symbols, no thousands separators.
- Dates are YYYY-MM-DD exactly as the document states them.
- NEVER output a Social Security number, taxpayer identification number, or
  full account number — account numbers are last four digits only, even when
  the document prints more.
- Give every record a page anchor (1-based) and a confidence between 0 and 1.
- If the document is not actually what it was described as, answer with the
  empty result rather than forcing records out of the wrong document.
"""

CREDIT_REPORT_SYSTEM_PROMPT: Final = (
    """\
You are reading a consumer credit report uploaded as source material for a
U.S. consumer bankruptcy filing. Extract every tradeline into the schema you
were given: one creditor entry per distinct creditor (name and mailing
address as the report shows them — collections and servicers count as the
creditor currently holding the account), each carrying its claims (one per
account/tradeline). Include open, delinquent, charged-off and collection
accounts; skip inquiries, employment history and score commentary — they are
not debts. Do not merge creditors that merely look similar; a human reviews
duplicates.

"""
    + _SHARED_PROMPT_RULES
)

PAY_STUB_SYSTEM_PROMPT: Final = (
    """\
You are reading one or more pay stubs uploaded as income evidence for a U.S.
consumer bankruptcy filing. Extract the employer (name and address as
printed) and one pay-period record per stub in the document: the period's
start and end, the pay date, gross and net pay for THE PERIOD (never the
year-to-date column), and each itemised deduction line mapped onto the
category list in the schema — put a line that fits no category under
"other" and carry the stub's own wording in its description.

"""
    + _SHARED_PROMPT_RULES
)


@dataclass(frozen=True)
class ExtractionRequest:
    """What the ExtractionModel port is shown: the document's bytes and the
    fixed per-kind instruction pair. Nothing else — no case data reaches the
    prompt, which is what keeps the scrub argument about outputs only."""

    media_type: str
    data: bytes
    system_prompt: str
    schema: Mapping[str, Any]


@dataclass(frozen=True)
class ExtractionModelResult:
    """What the port answers: which model actually ran (recorded on the job
    result and as the candidates' origin client_id, so "what extracted this"
    stays answerable) and the raw structured output, still to be coerced and
    validated by the per-kind parser."""

    model: str
    raw: Mapping[str, Any]


@dataclass(frozen=True)
class CandidateSpec:
    """One record the coercion distilled from the model output, before the
    store stamps identity: the payload (already validated through the entity
    parser), plus the extraction stream's extras."""

    entity_type: str
    payload: Mapping[str, object]
    confidence: float | None
    page: int | None
    note: str | None = None


# ── Coercion: model output → validated candidate specs ─────────
# Defensive by construction: structured outputs mean a well-behaved answer
# already matches the schema, but these functions treat the raw mapping as
# untrusted anyway — a drifted adapter or a mid-generation surprise must
# yield skipped records, not stored junk.


def _text(value: object) -> str | None:
    if isinstance(value, str) and value.strip():
        return value.strip()
    return None


def _money_string(value: object) -> str | None:
    text = _text(value)
    if text is None:
        return None
    return _MONEY_FURNITURE.sub("", text) or None


def _last4(value: object) -> str | None:
    text = _text(value)
    if text is None:
        return None
    digits = _DIGITS.findall(text)
    if not digits:
        return None
    return "".join(digits[-4:])


def _confidence(value: object) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return max(0.0, min(1.0, float(value)))


def _page(value: object) -> int | None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        return None
    return value


def _address(value: object) -> dict[str, object]:
    if not isinstance(value, Mapping):
        return {}
    return {
        key: text
        for key in ("line1", "line2", "city", "state", "postal_code", "raw")
        if (text := _text(value.get(key))) is not None
    }


def _entries(value: object) -> Sequence[Mapping[str, Any]]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        return ()
    return [entry for entry in value if isinstance(entry, Mapping)]


@dataclass(frozen=True)
class CoercionOutcome:
    specs: tuple[CandidateSpec, ...]
    skipped: int


def _validated(
    kind: EntityKind[Any], payload: dict[str, object]
) -> Mapping[str, object] | None:
    """The payload as it will be stored, or None when the entity parser
    refuses it — the SAME parser acceptance will run, so nothing lands in
    the queue that could never leave it. Scrubbed FIRST, so what is
    validated is what is stored."""
    cleaned = scrub(payload)
    assert isinstance(cleaned, dict)
    pruned = {key: value for key, value in cleaned.items() if value not in (None, {})}
    if not pruned:
        return None
    try:
        kind.parse_body(pruned)
    except FieldValidationError:
        return None
    return pruned


def parse_credit_report_output(raw: Mapping[str, Any]) -> CoercionOutcome:
    """Creditor + claim specs from the credit-report schema's output.

    A claim's `creditor_id` is filled in later (candidate-id indirection —
    the module docstring), so the specs come out creditor-first with each
    creditor's claims immediately after it; `link_group` is positional: the
    worker walks the tuple and remembers the last creditor candidate id.
    """
    specs: list[CandidateSpec] = []
    skipped = 0
    for entry in _entries(raw.get("creditors")):
        name = _text(entry.get("name"))
        payload = _validated(
            CREDITOR, {"name": name, "address": _address(entry.get("address"))}
        )
        if payload is None or name is None:
            skipped += 1 + len(_entries(entry.get("claims")))
            continue
        specs.append(
            CandidateSpec(
                entity_type=CREDITOR.collection,
                payload=payload,
                confidence=_confidence(entry.get("confidence")),
                page=_page(entry.get("page")),
            )
        )
        for claim in _entries(entry.get("claims")):
            claim_payload = _validated(
                CLAIM,
                {
                    "claim_class": _text(claim.get("claim_class")),
                    "account_last4": _last4(claim.get("account_last4")),
                    "amount": _money_string(claim.get("amount")),
                    "date_incurred": _text(claim.get("date_incurred")),
                    "disputed": claim.get("disputed")
                    if isinstance(claim.get("disputed"), bool)
                    else None,
                },
            )
            if claim_payload is None:
                skipped += 1
                continue
            specs.append(
                CandidateSpec(
                    entity_type=CLAIM.collection,
                    payload=claim_payload,
                    confidence=_confidence(claim.get("confidence")),
                    page=_page(claim.get("page")),
                    note=f"Claim of extracted creditor: {name}",
                )
            )
    return CoercionOutcome(specs=tuple(specs), skipped=skipped)


def parse_pay_stub_output(raw: Mapping[str, Any]) -> CoercionOutcome:
    """Employment + pay-period specs from the pay-stub schema's output.
    Same creditor-first ordering rule: the employment spec leads, and every
    pay period links to it by position."""
    specs: list[CandidateSpec] = []
    skipped = 0
    employer = raw.get("employer")
    employer = employer if isinstance(employer, Mapping) else {}
    employer_name = _text(employer.get("name"))
    employment_payload = (
        _validated(
            EMPLOYMENT,
            {
                "employer_name": employer_name,
                "employer_address": _address(employer.get("address")),
                "status": "employed",
            },
        )
        # A nameless employer is not a record a human can review a pay
        # period against — the group is unusable, like a nameless creditor.
        if employer_name is not None
        else None
    )
    periods = _entries(raw.get("pay_periods"))
    if employment_payload is None:
        return CoercionOutcome(specs=(), skipped=1 + len(periods))
    specs.append(
        CandidateSpec(
            entity_type=EMPLOYMENT.collection,
            payload=employment_payload,
            confidence=_confidence(employer.get("confidence")),
            page=_page(employer.get("page")),
        )
    )
    for period in periods:
        deductions = []
        for index, deduction in enumerate(_entries(period.get("deductions"))):
            deductions.append(
                {
                    # Worker-minted, satisfying the ADDRESSABLE_ID_RE contract
                    # the parser demands — stable within the row, which is all
                    # provenance addressing needs.
                    "id": f"d{index + 1}",
                    "category": _text(deduction.get("category")),
                    "amount": _money_string(deduction.get("amount")),
                    "description": _text(deduction.get("description")),
                }
            )
        period_payload = _validated(
            PAY_PERIOD_RECORD,
            {
                "period_start": _text(period.get("period_start")),
                "period_end": _text(period.get("period_end")),
                "pay_date": _text(period.get("pay_date")),
                "gross": _money_string(period.get("gross")),
                "net": _money_string(period.get("net")),
                "frequency": _text(period.get("frequency")),
                "deductions": deductions,
            },
        )
        if period_payload is None:
            skipped += 1
            continue
        specs.append(
            CandidateSpec(
                entity_type=PAY_PERIOD_RECORD.collection,
                payload=period_payload,
                confidence=_confidence(period.get("confidence")),
                page=_page(period.get("page")),
                note=f"Pay period of extracted employer: {employer_name}"
                if employer_name
                else None,
            )
        )
    return CoercionOutcome(specs=tuple(specs), skipped=skipped)


# Which parser, prompt and schema serve each document kind — the dispatch
# table the worker walks. One row per member of EXTRACTABLE_DOCUMENT_KINDS,
# pinned by tests so the two cannot drift.
_Extractor = tuple[
    str, Mapping[str, Any], Callable[[Mapping[str, Any]], CoercionOutcome]
]
EXTRACTORS: Final[Mapping[str, _Extractor]] = {
    "credit_report": (
        CREDIT_REPORT_SYSTEM_PROMPT,
        CREDIT_REPORT_OUTPUT_SCHEMA,
        parse_credit_report_output,
    ),
    "pay_stub": (
        PAY_STUB_SYSTEM_PROMPT,
        PAY_STUB_OUTPUT_SCHEMA,
        parse_pay_stub_output,
    ),
}

# Which entity types lead a link group (the module docstring's candidate-id
# indirection), and which reference field their followers carry.
_LINK_FIELD: Final = {
    CLAIM.collection: "creditor_id",
    PAY_PERIOD_RECORD.collection: "employment_id",
}
_LINK_LEADERS: Final = (CREDITOR.collection, EMPLOYMENT.collection)


# ── The worker ──────────────────────────────────────────────────


@dataclass(frozen=True)
class DocumentExtractionDeps:
    """What the worker composes — the entrypoints build this from the AWS
    adapters and the Anthropic adapter, tests from the memory ones. `model`
    is None in an environment whose key is not configured, and the worker
    turns that into an honest deterministic failure rather than a retry
    loop — the petition review's rule, inherited."""

    case_store: CaseStore
    document_store: DocumentStore
    blobs: DocumentBlobStore
    candidate_store: CandidateStore
    access_log: AccessLog
    model: ExtractionModel | None


def run_document_extraction(job: Job, deps: DocumentExtractionDeps) -> dict[str, Any]:
    """The worker: Job in, JSON-shaped result out (core/jobs.py's contract).

    The result carries IDENTIFIERS AND COUNTS only — candidate ids, entity
    types, the model, the skipped count — never a payload: the job row lives
    in the case partition but the candidates are where extracted values
    belong, and the review flow is how they are read.

    JobError (-> job `failed`, no retry) covers every deterministic refusal:
    no key, a vanished case or document, bytes that never landed, a kind
    with no extractor, a format the model API cannot take, an oversized
    document. An Anthropic API failure raises through and takes the run_job
    retry path, because a retry genuinely may succeed.
    """
    if deps.model is None:
        raise JobError(
            "AI extraction is not configured in this environment yet.",
            category="not_configured",
        )
    if job.document_id is None:
        # Unreachable through the accept endpoint (parse_job_acceptance
        # requires it); a hand-injected message lands here honestly.
        raise JobError(
            "This extraction job names no document.", category="document_not_found"
        )
    case = deps.case_store.read_for_worker(job.case_id)
    if case is None:
        raise JobError(
            "The case this job was accepted against no longer exists.",
            category="case_not_found",
        )
    # Reading a source document IS a case-data read, recorded against the
    # preparer whose accept caused it — the packet/review pair rule.
    deps.access_log.record(
        record_access(
            case_id=case.id, principal=job.created_by, action="document.extract"
        )
    )

    document = deps.document_store.get(case.id, job.document_id)
    if document is None:
        raise JobError(
            "The document this job was accepted against no longer exists.",
            category="document_not_found",
        )
    outcome = _extract_document(document, deps, accepted_by=job.created_by)
    logger.info(
        # GLBA: identifiers, the kind, and counts — never a value.
        "document extracted",
        extra={
            "case_id": case.id,
            "job_id": job.id,
            "document_id": document.id,
            "document_kind": document.kind,
            "model": outcome["model"],
            "candidates": len(outcome["candidates"]),
            "skipped": outcome["skipped"],
        },
    )
    return outcome


def _extract_document(
    document: Document, deps: DocumentExtractionDeps, *, accepted_by: str
) -> dict[str, Any]:
    if document.kind not in EXTRACTORS:
        raise JobError(
            "This document kind has no extractor. Extraction reads: "
            + ", ".join(EXTRACTABLE_DOCUMENT_KINDS)
            + ".",
            category="not_extractable",
        )
    if document.status != "stored":
        raise JobError(
            "This document's upload has not completed; finish the upload and"
            " try again.",
            category="not_uploaded",
        )
    if document.content_type not in MODEL_INPUT_CONTENT_TYPES:
        raise JobError(
            "This file format cannot be read by extraction yet — upload the"
            " document as a PDF, JPEG or PNG.",
            category="unsupported_format",
        )
    if document.byte_size > MAX_DOCUMENT_BYTES:
        raise JobError(
            "This document is too large for extraction"
            f" (over {MAX_DOCUMENT_BYTES // (1024 * 1024)} MB).",
            category="too_large",
        )
    data = deps.blobs.get_bytes(document.storage_ref)
    if data is None:
        raise JobError(
            "The document's bytes are not in the store; upload it again.",
            category="bytes_missing",
        )

    assert deps.model is not None  # checked by the caller
    system_prompt, schema, parse_output = EXTRACTORS[document.kind]
    result = deps.model.extract(
        ExtractionRequest(
            media_type=document.content_type,
            data=data,
            system_prompt=system_prompt,
            schema=schema,
        )
    )
    coerced = parse_output(result.raw)
    specs = coerced.specs[:MAX_CANDIDATES_PER_RUN]
    truncated = len(coerced.specs) - len(specs)

    origin = CandidateOrigin(
        channel="extraction",
        # The model that produced the batch — attribution the way an MCP
        # proposal records its OAuth client.
        client_id=result.model,
        # The preparer whose accept caused this run; review displays it.
        subject=accepted_by,
    )
    stored: list[dict[str, str]] = []
    leader_id: str | None = None
    for spec in specs:
        payload: Mapping[str, object] = spec.payload
        if spec.entity_type in _LINK_FIELD and leader_id is not None:
            payload = {**payload, _LINK_FIELD[spec.entity_type]: leader_id}
        candidate = create_candidate(
            ProposalDraft(
                entity_type=spec.entity_type,
                payload=payload,
                external_ref=None,
                note=spec.note,
            ),
            case_id=document.case_id,
            origin=origin,
            document_id=document.id,
            confidence=spec.confidence,
            locator={"document_id": document.id, "page": spec.page}
            if spec.page is not None
            else {"document_id": document.id},
        )
        deps.candidate_store.create(candidate)
        if spec.entity_type in _LINK_LEADERS:
            leader_id = candidate.id
        stored.append({"id": candidate.id, "entityType": candidate.entity_type})
    return {
        "outcome": "extracted",
        "documentId": document.id,
        "documentKind": document.kind,
        "model": result.model,
        "candidates": stored,
        "skipped": coerced.skipped + truncated,
    }


def document_extraction_worker(
    deps: DocumentExtractionDeps,
) -> Callable[[Job], dict[str, Any]]:
    """The registry entry: closes the dependencies over the plain
    Job-in/result-out callable the worker registries expect."""

    def worker(job: Job) -> dict[str, Any]:
        return run_document_extraction(job, deps)

    return worker
