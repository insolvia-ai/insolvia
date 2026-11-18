"""The pipeline's workers, composed — shared by the two local consumers: the
development server's in-process queue (#624) and worker_poller.py against a
per-machine dev queue. The worker Lambda composes the same set itself."""

from __future__ import annotations

from collections.abc import Callable
from typing import Any

from insolvia_core.ports import (
    AccessLog,
    CandidateStore,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentBlobStore,
    DocumentStore,
    TaxIdCipher,
    TaxIdStore,
)

from insolvia_api.adapters.anthropic.extraction_model import AnthropicExtractionModel
from insolvia_api.adapters.anthropic.review_model import AnthropicReviewModel
from insolvia_api.core.extraction import (
    DOCUMENT_EXTRACTION_KIND,
    DocumentExtractionDeps,
    document_extraction_worker,
)
from insolvia_api.core.jobs import WORKERS, Job
from insolvia_api.core.packet_assembly import (
    PACKET_ASSEMBLY_KIND,
    PacketAssemblyDeps,
    packet_assembly_worker,
)
from insolvia_api.core.petition_review import (
    PETITION_REVIEW_KIND,
    PetitionReviewDeps,
    petition_review_worker,
)
from insolvia_api.core.ports import PacketStore


def compose_workers(
    *,
    case_store: CaseStore,
    debtor_store: DebtorStore,
    entity_store: CaseEntityStore,
    packet_store: PacketStore,
    document_store: DocumentStore,
    candidate_store: CandidateStore,
    blobs: DocumentBlobStore,
    access_log: AccessLog,
    tax_id_store: TaxIdStore,
    tax_id_cipher: TaxIdCipher,
    anthropic_api_key: str | None,
) -> dict[str, Callable[[Job], dict[str, Any]]]:
    # Without ANTHROPIC_API_KEY the AI jobs fail honestly with
    # `not_configured`; everything else runs.
    review_model = (
        AnthropicReviewModel(anthropic_api_key) if anthropic_api_key else None
    )
    extraction_model = (
        AnthropicExtractionModel(anthropic_api_key) if anthropic_api_key else None
    )
    return {
        **WORKERS,
        PACKET_ASSEMBLY_KIND: packet_assembly_worker(
            PacketAssemblyDeps(
                case_store=case_store,
                debtor_store=debtor_store,
                entity_store=entity_store,
                packet_store=packet_store,
                blobs=blobs,
                access_log=access_log,
                tax_id_store=tax_id_store,
                tax_id_cipher=tax_id_cipher,
            )
        ),
        PETITION_REVIEW_KIND: petition_review_worker(
            PetitionReviewDeps(
                case_store=case_store,
                debtor_store=debtor_store,
                entity_store=entity_store,
                packet_store=packet_store,
                access_log=access_log,
                tax_id_store=tax_id_store,
                tax_id_cipher=tax_id_cipher,
                model=review_model,
            )
        ),
        DOCUMENT_EXTRACTION_KIND: document_extraction_worker(
            DocumentExtractionDeps(
                case_store=case_store,
                document_store=document_store,
                blobs=blobs,
                candidate_store=candidate_store,
                access_log=access_log,
                model=extraction_model,
            )
        ),
    }
