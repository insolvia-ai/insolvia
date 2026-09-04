"""Chapter 7 packet assembly — the completeness gate, the deterministic
render, and the pipeline worker that runs both (issue #96).

The milestone's definition of done: intake data in, a complete, filed-ready
Chapter 7 packet out. This module is the end of that pipe, and it is a
PIPELINE WORKER, not an endpoint (ADR 0015/0018): rendering thirteen official
PDFs takes longer than a request should, so the API accepts a `packet_assembly`
job (api/routes/jobs.py) and this worker runs it.

Three stages, in a fixed order:

1. **The completeness gate runs before any PDF is produced.** It collects
   EVERY reason the case cannot yield a compliant packet — the checks the
   entity framework deliberately deferred here (issue #276: dangling
   references, the petition's one-per-case and the households' one-or-two
   cardinality), the creditor matrix's own problem list, and every form
   projection's errors — and reports them per item, exactly as the matrix
   does, so an attorney fixes the list in one pass. A partial packet is never
   produced: a filing with a silently missing schedule is the failure this
   gate exists to prevent.
2. **Assembly is deterministic to the byte.** Every form resolves its release
   as of the assembly date (effective-dating.md's float rule — the case is
   not yet filed, so today's revisions are the ones in force), projects
   through the revision's own mapping, and fills through the engine whose
   goldens pin sha256s. The parts are zipped STORED (uncompressed) with a
   fixed timestamp, so the same case data always produces the same bytes and
   a re-render is diffable.
3. **The pins are written in the same operation as the packet.** The packet
   record and the case's `form_revisions` land in one transactional write
   (core/ports.PacketStore.create) — a packet whose pins were lost, or pins
   whose packet was, would each make "what did this filing use" unanswerable.
   Re-assembly re-pins; a FILED case refuses to assemble at all, because a
   filed case never re-resolves.

A gate refusal is a SUCCESSFUL job whose result says `blocked` — the
creditor-matrix route's rule ("both are the same successful act") carried
over: running the gate and learning the answer is the work the preparer
asked for, and `failed` stays reserved for the pipeline itself breaking.

Everything except the worker's own store calls is pure and runs under pytest
with the memory adapters — the local story ADR 0018 requires.
"""

from __future__ import annotations

import hashlib
import io
import logging
import zipfile
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any, Final

from insolvia_core.access_log import record_access
from insolvia_core.assets import ASSET, AssetBody
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import Case, pin_case
from insolvia_core.claims import CLAIM, ClaimBody
from insolvia_core.codebtors import (
    CODEBTOR,
    COMMUNITY_HOUSEHOLD_MEMBER,
    CodebtorBody,
    CommunityHouseholdMemberBody,
)
from insolvia_core.contract_leases import CONTRACT_LEASE, ContractLeaseBody
from insolvia_core.creditors import CREDITOR, CreditorBody
from insolvia_core.debtors import Debtor
from insolvia_core.exemption_claims import EXEMPTION, ExemptionBody
from insolvia_core.expenses import (
    DEPENDENT,
    EXPENSE,
    HOUSEHOLD,
    DependentBody,
    ExpenseBody,
    HouseholdBody,
)
from insolvia_core.income import (
    EMPLOYMENT,
    INCOME_SUMMARY,
    EmploymentBody,
    IncomeSummaryBody,
)
from insolvia_core.petitions import (
    FILING_PROFESSIONAL,
    PETITION,
    PRIOR_CASE,
    RELATED_CASE,
    SOLE_PROPRIETORSHIP,
    FilingProfessionalBody,
    PetitionBody,
    PriorCaseBody,
    RelatedCaseBody,
    SoleProprietorshipBody,
)
from insolvia_core.sofa import SOFA_ENTRY, SofaEntryBody

from insolvia_api.core.creditor_matrix import (
    MATRIX_FILE_NAME,
    generate_creditor_matrix,
)
from insolvia_api.core.form_fill import FormFillError, fill_form
from insolvia_api.core.form_projections import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    project,
)
from insolvia_api.core.form_templates import FormRelease, resolve_form
from insolvia_api.core.jobs import Job, JobError
from insolvia_api.core.packets import PACKET_CONTENT_TYPE, new_packet, packet_json

if TYPE_CHECKING:
    from datetime import date

    from insolvia_core.ports import (
        AccessLog,
        CaseEntityStore,
        CaseStore,
        DebtorStore,
        DocumentBlobStore,
    )

    from insolvia_api.core.ports import PacketStore

logger = logging.getLogger(__name__)

# The job kind the accept endpoint validates and the worker registries key on.
PACKET_ASSEMBLY_KIND: Final = "packet_assembly"

# The individual Chapter 7 set, in filing order — the order the clerk's
# checklist reads and the order the zip lists. B122A slots in here when the
# means-test milestone lands it (the issue says so in as many words).
PACKET_FORM_SERIES: Final = (
    "form/b101",
    "form/b106sum",
    "form/b106ab",
    "form/b106c",
    "form/b106d",
    "form/b106ef",
    "form/b106g",
    "form/b106h",
    "form/b106i",
    "form/b106j",
    "form/b106j2",
    "form/b106dec",
    "form/b107",
)

# The one fixed zip timestamp (1980-01-01, DOS epoch): determinism demands a
# constant, and an obviously-synthetic constant beats a plausible-looking one.
_ZIP_EPOCH: Final = (1980, 1, 1, 0, 0, 0)


@dataclass(frozen=True)
class PacketProblem:
    """One reason the case cannot yield a compliant packet — the matrix's
    per-item reporting shape, widened with `source` so the client knows which
    collection (or which form) the fix belongs to.

    `source` is a collection name from core/case_collections.py ("claims",
    "households", …), "case"/"debtors" for the two non-generic records, or a
    form series id ("form/b101") for a projection or fill refusal. `item_id`
    names the record when one record owns the fix; `field` is the body path
    the entity endpoints validate, so the client can put the message next to
    the input — both None/empty where the problem is collection-level.
    """

    source: str
    item_id: str | None
    field: str
    message: str


def problem_json(problem: PacketProblem) -> dict[str, object]:
    """The wire shape — matrix_json's optional-key rule: absent, never null."""
    body: dict[str, object] = {"source": problem.source, "message": problem.message}
    if problem.item_id is not None:
        body["itemId"] = problem.item_id
    if problem.field:
        body["field"] = problem.field
    return body


@dataclass(frozen=True)
class CaseData:
    """Everything assembly reads, as the stores hand it over — entities with
    their ids, in the collections' own listing order (creation order). The
    gate needs the wrappers (a problem names the record to fix); the
    projections get the bodies via `to_case_file`."""

    case: Case
    debtors: tuple[Debtor, ...] = ()
    petitions: tuple[CaseEntity[PetitionBody], ...] = ()
    prior_cases: tuple[CaseEntity[PriorCaseBody], ...] = ()
    related_cases: tuple[CaseEntity[RelatedCaseBody], ...] = ()
    sole_proprietorships: tuple[CaseEntity[SoleProprietorshipBody], ...] = ()
    filing_professionals: tuple[CaseEntity[FilingProfessionalBody], ...] = ()
    employments: tuple[CaseEntity[EmploymentBody], ...] = ()
    income_summaries: tuple[CaseEntity[IncomeSummaryBody], ...] = ()
    assets: tuple[CaseEntity[AssetBody], ...] = ()
    exemptions: tuple[CaseEntity[ExemptionBody], ...] = ()
    creditors: tuple[CaseEntity[CreditorBody], ...] = ()
    claims: tuple[CaseEntity[ClaimBody], ...] = ()
    contract_leases: tuple[CaseEntity[ContractLeaseBody], ...] = ()
    codebtors: tuple[CaseEntity[CodebtorBody], ...] = ()
    community_household_members: tuple[
        CaseEntity[CommunityHouseholdMemberBody], ...
    ] = ()
    households: tuple[CaseEntity[HouseholdBody], ...] = ()
    expenses: tuple[CaseEntity[ExpenseBody], ...] = ()
    dependents: tuple[CaseEntity[DependentBody], ...] = ()
    sofa_entries: tuple[CaseEntity[SofaEntryBody], ...] = ()


def read_case_data(
    case: Case, *, debtor_store: DebtorStore, entity_store: CaseEntityStore
) -> CaseData:
    """One read per collection, in the stores' own listing order — which IS
    printed row order (core/case_entities.list_order)."""
    return CaseData(
        case=case,
        debtors=debtor_store.list_for_case(case.id),
        petitions=entity_store.list_for_case(case.id, PETITION),
        prior_cases=entity_store.list_for_case(case.id, PRIOR_CASE),
        related_cases=entity_store.list_for_case(case.id, RELATED_CASE),
        sole_proprietorships=entity_store.list_for_case(case.id, SOLE_PROPRIETORSHIP),
        filing_professionals=entity_store.list_for_case(case.id, FILING_PROFESSIONAL),
        employments=entity_store.list_for_case(case.id, EMPLOYMENT),
        income_summaries=entity_store.list_for_case(case.id, INCOME_SUMMARY),
        assets=entity_store.list_for_case(case.id, ASSET),
        exemptions=entity_store.list_for_case(case.id, EXEMPTION),
        creditors=entity_store.list_for_case(case.id, CREDITOR),
        claims=entity_store.list_for_case(case.id, CLAIM),
        contract_leases=entity_store.list_for_case(case.id, CONTRACT_LEASE),
        codebtors=entity_store.list_for_case(case.id, CODEBTOR),
        community_household_members=entity_store.list_for_case(
            case.id, COMMUNITY_HOUSEHOLD_MEMBER
        ),
        households=entity_store.list_for_case(case.id, HOUSEHOLD),
        expenses=entity_store.list_for_case(case.id, EXPENSE),
        dependents=entity_store.list_for_case(case.id, DEPENDENT),
        sofa_entries=entity_store.list_for_case(case.id, SOFA_ENTRY),
    )


def to_case_file(data: CaseData) -> CaseFile:
    """The projections' input: bodies in listing order, id-paired where other
    records reference them (form_projections/shared.CaseFile's contract).

    The petition collapses to the FIRST record — the gate has already refused
    a case with more than one, so by the time a projection reads this the
    first is the only."""
    return CaseFile(
        case=data.case,
        debtors=data.debtors,
        petition=data.petitions[0].body if data.petitions else None,
        prior_cases=tuple(e.body for e in data.prior_cases),
        related_cases=tuple(e.body for e in data.related_cases),
        sole_proprietorships=tuple(e.body for e in data.sole_proprietorships),
        filing_professionals=tuple(e.body for e in data.filing_professionals),
        employments=tuple(e.body for e in data.employments),
        income_summaries=tuple(e.body for e in data.income_summaries),
        assets=tuple((e.id, e.body) for e in data.assets),
        exemptions=tuple(e.body for e in data.exemptions),
        creditors=tuple((e.id, e.body) for e in data.creditors),
        claims=tuple((e.id, e.body) for e in data.claims),
        contract_leases=tuple((e.id, e.body) for e in data.contract_leases),
        codebtors=tuple(e.body for e in data.codebtors),
        community_household_members=tuple(
            e.body for e in data.community_household_members
        ),
        households=tuple((e.id, e.body) for e in data.households),
        expenses=tuple(e.body for e in data.expenses),
        dependents=tuple(e.body for e in data.dependents),
        sofa_entries=tuple(e.body for e in data.sofa_entries),
    )


def _reference_problems(data: CaseData) -> list[PacketProblem]:
    """The dangling references issue #276 deliberately left unchecked at
    entry: a reference is validated for shape when typed and deletes do not
    cascade, so a claim can outlive its creditor — and a form printed from it
    would silently drop the creditor's name. Only PRESENT ids are checked;
    None is an absent fact, which is intake's business, not the gate's."""
    problems: list[PacketProblem] = []
    debtor_ids = {debtor.id for debtor in data.debtors}
    creditor_ids = {e.id for e in data.creditors}
    asset_ids = {e.id for e in data.assets}
    household_ids = {e.id for e in data.households}
    claim_ids = {e.id for e in data.claims}
    contract_ids = {e.id for e in data.contract_leases}

    def dangle(source: str, item_id: str, field: str, target: str) -> None:
        problems.append(
            PacketProblem(
                source=source,
                item_id=item_id,
                field=field,
                message=f"References a {target} record that does not exist —"
                " fix the reference or re-enter the record it pointed at.",
            )
        )

    for claim in data.claims:
        ref = claim.body.creditor_id
        if ref is not None and ref not in creditor_ids:
            dangle("claims", claim.id, "creditor_id", "creditor")
    for exemption in data.exemptions:
        ref = exemption.body.asset_id
        if ref is not None and ref not in asset_ids:
            dangle("exemptions", exemption.id, "asset_id", "property (asset)")
    for employment in data.employments:
        ref = employment.body.debtor_id
        if ref is not None and ref not in debtor_ids:
            dangle("employments", employment.id, "debtor_id", "debtor")
    for summary in data.income_summaries:
        ref = summary.body.debtor_id
        if ref is not None and ref not in debtor_ids:
            dangle("income_summaries", summary.id, "debtor_id", "debtor")
    for expense in data.expenses:
        ref = expense.body.household_id
        if ref is not None and ref not in household_ids:
            dangle("expenses", expense.id, "household_id", "household")
    for dependent in data.dependents:
        ref = dependent.body.household_id
        if ref is not None and ref not in household_ids:
            dangle("dependents", dependent.id, "household_id", "household")
    for codebtor in data.codebtors:
        for ref in codebtor.body.claim_ids:
            if ref not in claim_ids:
                dangle("codebtors", codebtor.id, "claim_ids", "claim")
        for ref in codebtor.body.contract_lease_ids:
            if ref not in contract_ids:
                dangle("codebtors", codebtor.id, "contract_lease_ids", "contract/lease")
    return problems


def _cardinality_problems(data: CaseData) -> list[PacketProblem]:
    """The one-per-case and one-per-column rules #276 could not key-enforce
    (a summary can be typed before its debtor record exists), owned here as
    that PR's assumptions promised."""
    problems: list[PacketProblem] = []

    if not data.petitions:
        problems.append(
            PacketProblem(
                source="petitions",
                item_id=None,
                field="",
                message="The petition's case-level answers (B101 Parts 2-6)"
                " have not been entered yet.",
            )
        )
    for extra in data.petitions[1:]:
        problems.append(
            PacketProblem(
                source="petitions",
                item_id=extra.id,
                field="",
                message="A case has exactly one petition record — delete the"
                " duplicates before assembling.",
            )
        )

    seen_households: dict[str, str] = {}
    for household in data.households:
        which = household.body.which_household
        if which is None:
            problems.append(
                PacketProblem(
                    source="households",
                    item_id=household.id,
                    field="which_household",
                    message="Say which schedule this household belongs to"
                    " (main, or Debtor 2's separate household) — without it"
                    " the row cannot print on 106J or 106J-2.",
                )
            )
        elif which in seen_households:
            problems.append(
                PacketProblem(
                    source="households",
                    item_id=household.id,
                    field="which_household",
                    message="Two household records claim the same schedule —"
                    " a case has at most one main household and one separate"
                    " household for Debtor 2.",
                )
            )
        else:
            seen_households[which] = household.id

    summaries_by_debtor: dict[str, str] = {}
    for summary in data.income_summaries:
        ref = summary.body.debtor_id
        if ref is None:
            continue
        if ref in summaries_by_debtor:
            problems.append(
                PacketProblem(
                    source="income_summaries",
                    item_id=summary.id,
                    field="debtor_id",
                    message="Two income summaries claim the same debtor column"
                    " — 106I prints one column per debtor.",
                )
            )
        else:
            summaries_by_debtor[ref] = summary.id
    return problems


def completeness_problems(data: CaseData) -> tuple[PacketProblem, ...]:
    """Every structural reason the case cannot assemble, before a single
    projection runs. Deterministic order: case, debtors, cardinality,
    references — so the same case always reports the same list."""
    problems: list[PacketProblem] = []
    if data.case.chapter != 7:
        problems.append(
            PacketProblem(
                source="case",
                item_id=None,
                field="chapter",
                message=f"This is a Chapter {data.case.chapter} case — only"
                " the Chapter 7 packet can be assembled today.",
            )
        )
    if data.case.status == "filed":
        problems.append(
            PacketProblem(
                source="case",
                item_id=None,
                field="status",
                message="This case is filed. A filed case's packet is pinned"
                " to the data that produced it and is never re-assembled.",
            )
        )
    if not any(debtor.filing_role == "debtor_1" for debtor in data.debtors):
        problems.append(
            PacketProblem(
                source="debtors",
                item_id=None,
                field="",
                message="The case has no Debtor 1 record — every form in the"
                " packet prints the debtor's name.",
            )
        )
    problems.extend(_cardinality_problems(data))
    problems.extend(_reference_problems(data))
    return tuple(problems)


def packet_form_series(data: CaseData) -> tuple[str, ...]:
    """Which of the set's forms THIS case files.

    B106J-2 prints only when Debtor 2 keeps a separate household — its own
    projection module says "packet assembly decides whether a schedule with
    nothing to say is filed at all", and an all-blank J-2 in front of a clerk
    is a question, not a filing. Everything else is unconditional for an
    individual Chapter 7.
    """
    has_separate = any(
        e.body.which_household == "debtor_2_separate" for e in data.households
    )
    return tuple(
        series
        for series in PACKET_FORM_SERIES
        if series != "form/b106j2" or has_separate
    )


@dataclass(frozen=True)
class AssembledPacket:
    """A clean assembly: the parts in filing order, and the facts the record
    and the pins store. `parts` maps zip entry name -> exact bytes."""

    parts: tuple[tuple[str, bytes], ...]
    form_revisions: Mapping[str, str]
    creditor_count: int


def assemble(
    data: CaseData, *, as_of: date
) -> AssembledPacket | tuple[PacketProblem, ...]:
    """The whole gate-then-render, pure over its inputs.

    Returns the assembled parts, or EVERY problem found — never both and
    never a partial packet, the creditor matrix's contract writ large. The
    gate's structural checks, the matrix's list, each projection's refusals
    and each fill's are all collected before anything is given up on, so one
    run reports the whole fix list.
    """
    problems = list(completeness_problems(data))

    matrix = generate_creditor_matrix(data.creditors)
    problems.extend(
        PacketProblem(
            source="creditors",
            item_id=problem.creditor_id,
            field=problem.field,
            message=problem.message,
        )
        for problem in matrix.problems
    )

    case_file = to_case_file(data)
    series_ids = packet_form_series(data)
    # Resolve every release first: the pins the case records are the packet's
    # identity, and resolution failures gate like any other problem.
    releases: dict[str, FormRelease] = {}
    for series_id in series_ids:
        try:
            releases[series_id] = resolve_form(series_id, as_of)
        except LookupError as error:
            problems.append(
                PacketProblem(
                    source=series_id, item_id=None, field="", message=str(error)
                )
            )

    projected: dict[str, FieldValues] = {}
    for series_id, release in releases.items():
        try:
            projected[series_id] = project(release, case_file)
        except FormProjectionError as error:
            problems.extend(
                PacketProblem(source=series_id, item_id=None, field="", message=message)
                for message in error.problems
            )

    if problems:
        return tuple(problems)

    parts: list[tuple[str, bytes]] = []
    for position, series_id in enumerate(series_ids, start=1):
        release = releases[series_id]
        try:
            rendered = fill_form(release, projected[series_id])
        except FormFillError as error:
            problems.extend(
                PacketProblem(source=series_id, item_id=None, field="", message=message)
                for message in error.problems
            )
            continue
        parts.append((f"{position:02d}-{release.form}.pdf", rendered))
    if problems:
        return tuple(problems)

    assert matrix.content is not None  # its problems gated above
    parts.append((MATRIX_FILE_NAME, matrix.content.encode("ascii")))

    return AssembledPacket(
        parts=tuple(parts),
        # The WHOLE set pins, including a J-2 this case does not file: the
        # pin map records which revisions were in force for this assembly,
        # and a household added before re-assembly must not find a hole.
        form_revisions={
            series_id: resolve_form(series_id, as_of).pin
            for series_id in PACKET_FORM_SERIES
        },
        creditor_count=matrix.creditor_count,
    )


def packet_zip(parts: tuple[tuple[str, bytes], ...]) -> bytes:
    """One zip, deterministic to the byte: fixed entry order (the caller's,
    which is filing order), fixed DOS-epoch timestamps, STORED rather than
    deflated so no compressor version can ever wiggle the bytes. PDFs are
    already compressed internally; the matrix is a few kilobytes."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, content in parts:
            info = zipfile.ZipInfo(filename=name, date_time=_ZIP_EPOCH)
            # Regular file, rw-r--r-- — set explicitly so the host's umask
            # can never reach the archive.
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    return buffer.getvalue()


@dataclass(frozen=True)
class PacketAssemblyDeps:
    """What the worker composes — the entrypoints build this from the AWS
    adapters, tests from the memory ones. A dataclass rather than positional
    arguments so a new dependency is a named field in one place."""

    case_store: CaseStore
    debtor_store: DebtorStore
    entity_store: CaseEntityStore
    packet_store: PacketStore
    blobs: DocumentBlobStore
    access_log: AccessLog


def run_packet_assembly(
    job: Job, deps: PacketAssemblyDeps, *, today: date | None = None
) -> dict[str, Any]:
    """The worker: Job in, JSON-shaped result out (core/jobs.py's contract).

    Two result shapes, both SUCCEEDED jobs:

        {"outcome": "blocked", "problems": [...]}    the gate refused; the
                                                     list is the deliverable
        {"outcome": "assembled", "packet": {...}}    the packet is stored and
                                                     the case is pinned

    JobError (-> job `failed`) is reserved for states a fix-and-retry can
    change: the case vanished, or changed under the assembly. Anything else
    raising is infrastructure and takes the run_job retry path.
    """
    case = deps.case_store.read_for_worker(job.case_id)
    if case is None:
        raise JobError(
            "The case this job was accepted against no longer exists.",
            category="case_not_found",
        )
    # The worker reads the whole file — that is a case-data read, and it is
    # recorded against the preparer whose accept caused it (the same subject
    # the accept endpoint logged, so the trail reads: accepted, then read).
    deps.access_log.record(
        record_access(
            case_id=case.id, principal=job.created_by, action="packet.assemble"
        )
    )

    data = read_case_data(
        case, debtor_store=deps.debtor_store, entity_store=deps.entity_store
    )
    as_of = today if today is not None else datetime.now(UTC).date()
    outcome = assemble(data, as_of=as_of)

    if not isinstance(outcome, AssembledPacket):
        logger.info(
            # GLBA: the count alone — a problem message names case facts.
            "packet assembly blocked",
            extra={"case_id": case.id, "job_id": job.id, "problems": len(outcome)},
        )
        return {
            "outcome": "blocked",
            "problems": [problem_json(problem) for problem in outcome],
        }

    content = packet_zip(outcome.parts)
    packet = new_packet(
        case_id=case.id,
        job_id=job.id,
        byte_size=len(content),
        sha256=hashlib.sha256(content).hexdigest(),
        form_revisions=outcome.form_revisions,
        creditor_count=outcome.creditor_count,
        created_by=job.created_by,
    )
    # Bytes first, record second: an object with no record is invisible and
    # harmless (nothing lists the bucket); a record with no object would be a
    # download that 404s. The failure window between the two leaves only the
    # former.
    deps.blobs.put_bytes(
        packet.storage_ref, content=content, content_type=PACKET_CONTENT_TYPE
    )

    pinned = pin_case(case, form_revisions=outcome.form_revisions)
    stored = deps.packet_store.create(
        packet, pinned_case=pinned, expected_updated_at=case.updated_at
    )
    if not stored:
        # The case moved (edited, filed, deleted) between our read and this
        # write. The packet no longer describes the case, so refusing is the
        # only honest answer; the stored object stays unreferenced, which is
        # the harmless side of the ordering above.
        raise JobError(
            "The case changed while its packet was being assembled — run"
            " assembly again.",
            category="case_changed",
        )
    logger.info(
        "packet assembled",
        extra={"case_id": case.id, "job_id": job.id, "packet_id": packet.id},
    )
    return {"outcome": "assembled", "packet": packet_json(packet)}


def packet_assembly_worker(deps: PacketAssemblyDeps) -> Callable[[Job], dict[str, Any]]:
    """The registry entry: closes the dependencies over the plain
    Job-in/result-out callable the worker registries expect."""

    def worker(job: Job) -> dict[str, Any]:
        return run_packet_assembly(job, deps)

    return worker
