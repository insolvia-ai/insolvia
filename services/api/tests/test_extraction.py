"""AI extraction (issues #87/8.7, #88/8.8): the worker, what leaves for the
model API, and the candidates that come back — end to end against the memory
adapters and the scripted model, ADR 0018's local story with the one
genuinely unrunnable hop (the generation itself) faked at its port.

Every fixture below is SYNTHETIC — invented names, invented amounts, invented
account fragments. This repo is public; no real credit report or pay stub, or
anything derived from one, may ever appear here.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.extraction_model import ScriptedExtractionModel
from insolvia_api.core.extraction import (
    CREDIT_REPORT_SYSTEM_PROMPT,
    DOCUMENT_EXTRACTION_KIND,
    EXTRACTABLE_DOCUMENT_KINDS,
    EXTRACTORS,
    MAX_CANDIDATES_PER_RUN,
    MAX_DOCUMENT_BYTES,
    PAY_STUB_SYSTEM_PROMPT,
    DocumentExtractionDeps,
    parse_credit_report_output,
    parse_pay_stub_output,
    run_document_extraction,
)
from insolvia_api.core.jobs import JobError, new_job
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.candidate_store import MemoryCandidateStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.adapters.memory.document_store import MemoryDocumentStore
from insolvia_core.cases import create_case, parse_case_creation
from insolvia_core.documents import (
    StoredBlob,
    confirm_document,
    create_document,
    parse_document_upload,
)

FIRM = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"

# Synthetic stand-ins for uploaded bytes. Content never matters — the model
# is scripted — but the worker must pass EXACTLY these bytes to the port.
PDF_BYTES = b"%PDF-1.7 synthetic fixture bytes"

# A hand-labeled synthetic credit-report extraction: what a well-behaved
# structured-output answer looks like, and below, what the queue must hold
# afterwards. Two creditors, three tradelines, one of them secured.
CREDIT_REPORT_RAW = {
    "creditors": [
        {
            "name": "First Example Bank",
            "address": {
                "line1": "1 Example Way",
                "line2": None,
                "city": "Exampleville",
                "state": "FL",
                "postal_code": "33101",
                "raw": None,
            },
            "page": 2,
            "confidence": 0.95,
            "claims": [
                {
                    "account_last4": "4321",
                    "amount": "1234.56",
                    "date_incurred": "2021-06-01",
                    "claim_class": "nonpriority_unsecured",
                    "disputed": False,
                    "page": 2,
                    "confidence": 0.9,
                },
                {
                    # A model echoing a longer number despite the prompt:
                    # the worker must store last-four only.
                    "account_last4": "9999-0000-1111-2222",
                    "amount": "$8,050.00",
                    "date_incurred": None,
                    "claim_class": "secured",
                    "disputed": None,
                    "page": 3,
                    "confidence": 0.8,
                },
            ],
        },
        {
            "name": "Example Collections LLC",
            "address": {
                "line1": None,
                "line2": None,
                "city": None,
                "state": None,
                "postal_code": None,
                "raw": "PO Box 9, Exampleville FL 33101",
            },
            "page": 5,
            "confidence": 0.7,
            "claims": [
                {
                    "account_last4": "0007",
                    "amount": "310.00",
                    "date_incurred": "2023-01-15",
                    "claim_class": "nonpriority_unsecured",
                    "disputed": True,
                    "page": 5,
                    "confidence": 0.75,
                }
            ],
        },
    ]
}

PAY_STUB_RAW = {
    "employer": {
        "name": "Example Industries Inc",
        "address": {
            "line1": "200 Example Blvd",
            "line2": None,
            "city": "Exampleville",
            "state": "FL",
            "postal_code": "33101",
            "raw": None,
        },
        "page": 1,
        "confidence": 0.98,
    },
    "pay_periods": [
        {
            "period_start": "2026-08-01",
            "period_end": "2026-08-14",
            "pay_date": "2026-08-19",
            "gross": "2400.00",
            "net": "1890.50",
            "frequency": "biweekly",
            "deductions": [
                {"category": "tax", "amount": "410.00", "description": "Fed W/H"},
                {
                    "category": "insurance",
                    "amount": "99.50",
                    "description": "Medical premium",
                },
            ],
            "page": 1,
            "confidence": 0.92,
        },
        {
            "period_start": "2026-08-15",
            "period_end": "2026-08-28",
            "pay_date": "2026-09-02",
            "gross": "2400.00",
            "net": "1890.50",
            "frequency": "biweekly",
            "deductions": [],
            "page": 2,
            "confidence": 0.9,
        },
    ],
}


def build_deps(model=None):
    case_store = MemoryCaseStore()
    case, assignment = create_case(
        parse_case_creation({"chapter": 7, "district": "Middle District of Florida"}),
        firm_id=FIRM,
        created_by=ALICE,
    )
    case_store.create(case, assignment)
    deps = DocumentExtractionDeps(
        case_store=case_store,
        document_store=MemoryDocumentStore(),
        blobs=MemoryDocumentBlobStore(),
        candidate_store=MemoryCandidateStore(),
        access_log=MemoryAccessLog(),
        model=model,
    )
    return case, deps


def stored_document(
    deps,
    case_id,
    *,
    kind="credit_report",
    content_type="application/pdf",
    data=PDF_BYTES,
    byte_size=None,
    confirm=True,
):
    document = create_document(
        parse_document_upload(
            {
                "kind": kind,
                "fileName": "synthetic.pdf",
                "contentType": content_type,
                "byteSize": byte_size if byte_size is not None else len(data),
            }
        ),
        case_id=case_id,
        uploaded_by=ALICE,
    )
    if confirm:
        deps.blobs.accept_upload(
            document.storage_ref, byte_size=len(data), content=data
        )
        document = confirm_document(
            document, StoredBlob(byte_size=len(data), etag="e" * 32)
        )
        # A confirmed row keeps the size S3 counted; the size-cap tests below
        # override it explicitly.
        if byte_size is not None:
            document = replace(document, byte_size=byte_size)
    deps.document_store.create(document)
    return document


def accept(case_id, document_id):
    return new_job(
        DOCUMENT_EXTRACTION_KIND,
        case_id=case_id,
        created_by=ALICE,
        document_id=document_id,
    )


# ── Deterministic refusals ──────────────────────────────────────


def test_an_unconfigured_environment_fails_the_job_honestly():
    case, deps = build_deps(model=None)
    document = stored_document(deps, case.id)
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "not_configured"


def test_a_vanished_case_is_a_deterministic_failure():
    _, deps = build_deps(ScriptedExtractionModel())
    job = accept("00000000-0000-4000-8000-000000000404", "doc-1")
    with pytest.raises(JobError) as caught:
        run_document_extraction(job, deps)
    assert caught.value.category == "case_not_found"


def test_a_vanished_document_is_a_deterministic_failure():
    case, deps = build_deps(ScriptedExtractionModel())
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, "not-a-document"), deps)
    assert caught.value.category == "document_not_found"


def test_a_kind_with_no_extractor_is_refused():
    case, deps = build_deps(ScriptedExtractionModel())
    document = stored_document(deps, case.id, kind="bank_statement")
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "not_extractable"


def test_a_pending_upload_is_refused():
    case, deps = build_deps(ScriptedExtractionModel())
    document = stored_document(deps, case.id, confirm=False)
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "not_uploaded"


@pytest.mark.parametrize("content_type", ["image/heic", "image/tiff"])
def test_a_format_the_model_api_cannot_take_is_refused(content_type):
    # HEIC and TIFF are on OUR upload allowlist but not the model API's —
    # honest refusal, not a doomed request.
    case, deps = build_deps(ScriptedExtractionModel())
    document = stored_document(deps, case.id, content_type=content_type)
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "unsupported_format"


def test_an_oversized_document_is_refused():
    case, deps = build_deps(ScriptedExtractionModel())
    document = stored_document(deps, case.id, byte_size=MAX_DOCUMENT_BYTES + 1)
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "too_large"


def test_missing_bytes_are_a_deterministic_failure():
    case, deps = build_deps(ScriptedExtractionModel())
    document = stored_document(deps, case.id, confirm=False)
    confirmed = confirm_document(
        document, StoredBlob(byte_size=len(PDF_BYTES), etag="e" * 32)
    )
    deps.document_store.update(confirmed)
    with pytest.raises(JobError) as caught:
        run_document_extraction(accept(case.id, document.id), deps)
    assert caught.value.category == "bytes_missing"


# ── What leaves for the model API ───────────────────────────────


def test_the_model_is_shown_the_document_and_nothing_else():
    case, deps = build_deps(ScriptedExtractionModel({"creditors": []}))
    document = stored_document(deps, case.id)

    run_document_extraction(accept(case.id, document.id), deps)

    (request,) = deps.model.requests
    assert request.data == PDF_BYTES
    assert request.media_type == "application/pdf"
    assert request.system_prompt == CREDIT_REPORT_SYSTEM_PROMPT
    # The prompt is FIXED — it carries the rules and no case data at all.
    assert case.id not in request.system_prompt
    assert "Social Security" in request.system_prompt


def test_each_document_kind_gets_its_own_prompt_and_schema():
    assert set(EXTRACTORS) == set(EXTRACTABLE_DOCUMENT_KINDS)
    case, deps = build_deps(
        ScriptedExtractionModel({"employer": {}, "pay_periods": []})
    )
    document = stored_document(deps, case.id, kind="pay_stub")
    run_document_extraction(accept(case.id, document.id), deps)
    (request,) = deps.model.requests
    assert request.system_prompt == PAY_STUB_SYSTEM_PROMPT


# ── Credit report → creditor and claim candidates (8.7) ────────


def test_a_credit_report_yields_creditor_and_claim_candidates():
    model = ScriptedExtractionModel(CREDIT_REPORT_RAW, model="scripted-extractor")
    case, deps = build_deps(model)
    document = stored_document(deps, case.id)

    result = run_document_extraction(accept(case.id, document.id), deps)

    assert result["outcome"] == "extracted"
    assert result["documentId"] == document.id
    assert result["documentKind"] == "credit_report"
    assert result["model"] == "scripted-extractor"
    assert result["skipped"] == 0

    candidates = deps.candidate_store.list_for_case(case.id)
    assert [c.entity_type for c in candidates] == [
        "creditors",
        "claims",
        "claims",
        "creditors",
        "claims",
    ]
    assert {c["id"] for c in result["candidates"]} == {c.id for c in candidates}

    bank, bank_card, bank_auto, collections, collections_claim = candidates
    assert bank.payload["name"] == "First Example Bank"
    assert bank.payload["address"]["city"] == "Exampleville"
    assert bank.confidence == 0.95
    assert bank.locator == {"document_id": document.id, "page": 2}
    assert bank.document_id == document.id
    # Origin: the extraction channel, attributed to the model that produced
    # the batch and the preparer whose accept caused the run.
    assert bank.origin.channel == "extraction"
    assert bank.origin.client_id == "scripted-extractor"
    assert bank.origin.subject == ALICE
    assert bank.status == "pending"

    # Candidate-id indirection: each claim names its creditor CANDIDATE.
    assert bank_card.payload["creditor_id"] == bank.id
    assert bank_auto.payload["creditor_id"] == bank.id
    assert collections_claim.payload["creditor_id"] == collections.id
    assert bank_card.payload["amount"] == "1234.56"
    assert bank_card.payload["disputed"] is False
    # The raw-address fallback survives.
    assert collections.payload["address"]["raw"].startswith("PO Box 9")


def test_account_numbers_are_stored_last_four_only():
    model = ScriptedExtractionModel(CREDIT_REPORT_RAW)
    case, deps = build_deps(model)
    document = stored_document(deps, case.id)
    run_document_extraction(accept(case.id, document.id), deps)
    candidates = deps.candidate_store.list_for_case(case.id)
    secured = next(c for c in candidates if c.payload.get("claim_class") == "secured")
    assert secured.payload["account_last4"] == "2222"
    # The dollar furniture was stripped, and the parser canonicalised it.
    assert secured.payload["amount"] == "8050.00"


def test_a_tax_id_shaped_value_never_lands_in_a_candidate():
    # Defence in depth (ADR 0019's scrub, applied to extraction OUTPUT): a
    # model that echoes an SSN into a free-text field stores a redaction.
    raw = {
        "creditors": [
            {
                "name": "Example Bank re 123-45-6789",
                "address": {
                    "line1": None,
                    "line2": None,
                    "city": None,
                    "state": None,
                    "postal_code": None,
                    "raw": None,
                },
                "page": 1,
                "confidence": 0.5,
                "claims": [],
            }
        ]
    }
    case, deps = build_deps(ScriptedExtractionModel(raw))
    document = stored_document(deps, case.id)
    run_document_extraction(accept(case.id, document.id), deps)
    (candidate,) = deps.candidate_store.list_for_case(case.id)
    assert "123-45-6789" not in str(candidate.payload)
    assert "[tax id removed]" in candidate.payload["name"]


def test_a_record_the_parser_refuses_is_skipped_and_counted():
    raw = {
        "creditors": [
            {
                "name": "First Example Bank",
                "address": CREDIT_REPORT_RAW["creditors"][0]["address"],
                "page": 1,
                "confidence": 0.9,
                "claims": [
                    {
                        "account_last4": "1111",
                        "amount": "not-a-number",  # the parser refuses this
                        "date_incurred": None,
                        "claim_class": "nonpriority_unsecured",
                        "disputed": None,
                        "page": 1,
                        "confidence": 0.9,
                    },
                    {
                        "account_last4": "2222",
                        "amount": "50.00",
                        "date_incurred": None,
                        "claim_class": "nonpriority_unsecured",
                        "disputed": None,
                        "page": 1,
                        "confidence": 0.9,
                    },
                ],
            }
        ]
    }
    case, deps = build_deps(ScriptedExtractionModel(raw))
    document = stored_document(deps, case.id)
    result = run_document_extraction(accept(case.id, document.id), deps)
    assert result["skipped"] == 1
    candidates = deps.candidate_store.list_for_case(case.id)
    assert [c.entity_type for c in candidates] == ["creditors", "claims"]


def test_an_unusable_creditor_skips_its_claims_too():
    # A claim without its creditor would be unreviewable; the whole group is
    # counted so the preparer can see the report needs a second look.
    raw = {
        "creditors": [
            {
                "name": None,
                "address": CREDIT_REPORT_RAW["creditors"][0]["address"],
                "page": 1,
                "confidence": 0.2,
                "claims": CREDIT_REPORT_RAW["creditors"][0]["claims"],
            }
        ]
    }
    case, deps = build_deps(ScriptedExtractionModel(raw))
    document = stored_document(deps, case.id)
    result = run_document_extraction(accept(case.id, document.id), deps)
    assert result["skipped"] == 3
    assert deps.candidate_store.list_for_case(case.id) == ()


def test_an_empty_answer_stores_nothing_and_succeeds():
    # "If the document is not actually what it was described as, answer with
    # the empty result" — a mislabeled upload is a successful job with an
    # empty queue, not a failure.
    case, deps = build_deps(ScriptedExtractionModel({"creditors": []}))
    document = stored_document(deps, case.id)
    result = run_document_extraction(accept(case.id, document.id), deps)
    assert result["candidates"] == []
    assert result["skipped"] == 0


def test_a_runaway_generation_is_capped():
    address = dict.fromkeys(("line1", "line2", "city", "state"))
    address.update({"postal_code": None, "raw": None})
    raw = {
        "creditors": [
            {
                "name": f"Creditor {n}",
                "address": address,
                "page": None,
                "confidence": 0.5,
                "claims": [],
            }
            for n in range(MAX_CANDIDATES_PER_RUN + 25)
        ]
    }
    case, deps = build_deps(ScriptedExtractionModel(raw))
    document = stored_document(deps, case.id)
    result = run_document_extraction(accept(case.id, document.id), deps)
    assert len(result["candidates"]) == MAX_CANDIDATES_PER_RUN
    assert result["skipped"] == 25


def test_the_run_is_access_logged_against_the_preparer():
    case, deps = build_deps(ScriptedExtractionModel({"creditors": []}))
    document = stored_document(deps, case.id)
    run_document_extraction(accept(case.id, document.id), deps)
    (event,) = deps.access_log.events
    assert event.case_id == case.id
    assert event.principal == ALICE
    assert event.action == "document.extract"


# ── Pay stubs → employment and pay-period candidates (8.8) ─────


def test_pay_stubs_yield_employment_and_dated_pay_period_candidates():
    model = ScriptedExtractionModel(PAY_STUB_RAW, model="scripted-extractor")
    case, deps = build_deps(model)
    document = stored_document(deps, case.id, kind="pay_stub")

    result = run_document_extraction(accept(case.id, document.id), deps)

    assert result["documentKind"] == "pay_stub"
    assert result["skipped"] == 0
    employment, first, second = deps.candidate_store.list_for_case(case.id)
    assert employment.entity_type == "employments"
    assert employment.payload["employer_name"] == "Example Industries Inc"
    assert employment.payload["status"] == "employed"

    # The CMI lookback's raw material: every period dated, all three dates,
    # linked to the employment CANDIDATE by id.
    assert first.entity_type == "pay_period_records"
    assert first.payload["employment_id"] == employment.id
    assert second.payload["employment_id"] == employment.id
    assert (first.payload["period_start"], first.payload["period_end"]) == (
        "2026-08-01",
        "2026-08-14",
    )
    assert first.payload["pay_date"] == "2026-08-19"
    assert second.payload["pay_date"] == "2026-09-02"
    assert first.payload["gross"] == "2400.00"
    assert first.payload["net"] == "1890.50"
    assert first.payload["frequency"] == "biweekly"
    # Deductions arrive itemised, mapped onto 106I's category vocabulary,
    # with worker-minted addressable ids.
    deductions = first.payload["deductions"]
    assert [d["category"] for d in deductions] == ["tax", "insurance"]
    assert [d["id"] for d in deductions] == ["d1", "d2"]
    assert deductions[1]["description"] == "Medical premium"
    assert first.locator == {"document_id": document.id, "page": 1}


def test_a_stub_without_a_readable_employer_skips_everything():
    raw = {
        "employer": {"name": None, "address": {}, "page": None, "confidence": 0.1},
        "pay_periods": PAY_STUB_RAW["pay_periods"],
    }
    case, deps = build_deps(ScriptedExtractionModel(raw))
    document = stored_document(deps, case.id, kind="pay_stub")
    result = run_document_extraction(accept(case.id, document.id), deps)
    assert result["skipped"] == 3
    assert deps.candidate_store.list_for_case(case.id) == ()


# ── The coercion layer, directly ────────────────────────────────


def test_credit_report_coercion_survives_junk():
    # Structured outputs make this shape unlikely, but a drifted adapter
    # must yield skipped records, never stored junk.
    outcome = parse_credit_report_output({"creditors": ["nonsense", 7, None]})
    assert outcome.specs == ()
    assert outcome.skipped == 0  # non-mapping entries are not records at all


def test_pay_stub_coercion_survives_a_missing_shape():
    outcome = parse_pay_stub_output({})
    assert outcome.specs == ()
    assert outcome.skipped == 1  # the employer that was not there
