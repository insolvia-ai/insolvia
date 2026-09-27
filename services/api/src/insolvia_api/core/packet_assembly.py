"""Packet assembly — the completeness gate, the deterministic render, and
the pipeline worker that runs both (issue #96; Chapter 13, issue #367).

The milestone's definition of done: intake data in, a complete, filed-ready
Chapter 7 packet out — and, since #367, the Chapter 13 packet with its plan
(`CHAPTER_13_FORM_SERIES`). This module is the end of that pipe, and it is a
PIPELINE WORKER, not an endpoint (ADR 0015/0018): rendering up to nineteen
official PDFs takes longer than a request should, so the API accepts a
`packet_assembly` job (api/routes/jobs.py) and this worker runs it.

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
   filed case never re-resolves — except as an AMENDMENT (issue #370,
   `OutputOptions.amended_only`), which prints only the changed schedules
   and writes no pins to the case.

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
from dataclasses import dataclass, field, replace
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any, Final

from insolvia_core import courts
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
    OTHER_INCOME_RECORD,
    PAY_PERIOD_RECORD,
    EmploymentBody,
    IncomeSummaryBody,
    OtherIncomeRecordBody,
    PayPeriodRecordBody,
)
from insolvia_core.means_test_inputs import MEANS_TEST_INPUT, MeansTestInputBody
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
from insolvia_core.plans import PLAN, PlanBody
from insolvia_core.sofa import SOFA_ENTRY, SofaEntryBody
from insolvia_core.tax_ids import read_tax_id

from insolvia_api.core import dollar_amounts
from insolvia_api.core.amendment_cover_sheet import render_amendment_cover_sheet
from insolvia_api.core.creditor_matrix import (
    MATRIX_FILE_NAME,
    format_for_court,
    generate_creditor_matrix,
)
from insolvia_api.core.form_fill import FormFillError, fill_form
from insolvia_api.core.form_overlay import (
    DEFAULT_OUTPUT_OPTIONS,
    OutputOptions,
    apply_amendment_options,
    apply_output_stamps,
    apply_signature_options,
    parse_output_options,
)
from insolvia_api.core.form_projections import (
    CaseFile,
    FieldValues,
    FormProjectionError,
    project,
)
from insolvia_api.core.form_projections.b122a2 import files_b122a2
from insolvia_api.core.form_projections.b122c2 import files_b122c2
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
        TaxIdCipher,
        TaxIdStore,
    )

    from insolvia_api.core.ports import PacketStore

logger = logging.getLogger(__name__)

# The job kind the accept endpoint validates and the worker registries key on.
PACKET_ASSEMBLY_KIND: Final = "packet_assembly"

# The individual Chapter 7 set, in filing order — the order the clerk's
# checklist reads and the order the zip lists. B121 follows the petition
# (it is submitted separately from the public file, but with it); B108 and
# the two Director's Forms follow the statement of affairs (issue #351:
# B108 files only when a secured claim or a flagged lease exists, B2030
# only when an attorney is on the case — packet_form_series decides). The
# B122A pair closes the set (issue #102): the CMI statement always files;
# the calculation only for an above-median debtor.
PACKET_FORM_SERIES: Final = (
    "form/b101",
    "form/b121",
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
    "form/b108",
    "form/b2010",
    "form/b2030",
    "form/b122a1",
    "form/b122a2",
)

# The individual Chapter 13 set, in filing order (issues #365, #367). It
# differs from the Chapter 7 set in three places:
#
# - the B122C pair replaces the B122A pair (§ 1325(b) instead of
#   § 707(b)): B122C-1 always files, B122C-2 only above the median
#   (B122C-1 line 17);
# - there is no B108. Bankruptcy Rule 1007(b)(2) asks for the Statement of
#   Intention from "an individual debtor in a chapter 7 case", and the form
#   says so in its title; on Chapter 13 what happens to each secured claim
#   and each lease is the PLAN's to say (B113 Parts 3 and 6);
# - Official Form 113, the plan, closes the set. Rule 3015(b) lets it file
#   with the petition or within 14 days after, so it follows every
#   statement it rests on — the schedules it treats, the means test whose
#   commitment period sets its term. A district that has opted out of the
#   national form under Rule 3015.1 is refused at the gate rather than
#   handed the wrong form (`_local_plan_form_problem`).
CHAPTER_13_FORM_SERIES: Final = (
    "form/b101",
    "form/b121",
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
    "form/b2010",
    "form/b2030",
    "form/b122c1",
    "form/b122c2",
    "form/b113",
)

# Every series a case may pin — the union of the chapter sets, in the
# Chapter 7 set's order with the Chapter 13 additions appended. `form_revisions`
# records the WHOLE registry's revisions at assembly, so a case whose
# chapter changes before re-assembly does not find a hole.
ALL_FORM_SERIES: Final = (
    *PACKET_FORM_SERIES,
    *(series for series in CHAPTER_13_FORM_SERIES if series not in PACKET_FORM_SERIES),
)


def chapter_form_series(chapter: int) -> tuple[str, ...]:
    """The unconditional form set a chapter files, before the per-case
    conditions `packet_form_series` applies."""
    return CHAPTER_13_FORM_SERIES if chapter == 13 else PACKET_FORM_SERIES


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
    projections get the bodies via `to_case_file`.

    `tax_ids` (filing role -> the full nine digits) is EMPTY as read — the
    stores never hand over a full tax identifier — and is filled only by
    `disclose_tax_ids`, the logged full-value read, on the one path that is
    about to print B121. `to_case_file` carries it across verbatim."""

    case: Case
    debtors: tuple[Debtor, ...] = ()
    tax_ids: Mapping[str, str] = field(default_factory=dict)
    petitions: tuple[CaseEntity[PetitionBody], ...] = ()
    prior_cases: tuple[CaseEntity[PriorCaseBody], ...] = ()
    related_cases: tuple[CaseEntity[RelatedCaseBody], ...] = ()
    sole_proprietorships: tuple[CaseEntity[SoleProprietorshipBody], ...] = ()
    filing_professionals: tuple[CaseEntity[FilingProfessionalBody], ...] = ()
    employments: tuple[CaseEntity[EmploymentBody], ...] = ()
    income_summaries: tuple[CaseEntity[IncomeSummaryBody], ...] = ()
    pay_period_records: tuple[CaseEntity[PayPeriodRecordBody], ...] = ()
    other_income_records: tuple[CaseEntity[OtherIncomeRecordBody], ...] = ()
    means_test_inputs: tuple[CaseEntity[MeansTestInputBody], ...] = ()
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
    plans: tuple[CaseEntity[PlanBody], ...] = ()


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
        pay_period_records=entity_store.list_for_case(case.id, PAY_PERIOD_RECORD),
        other_income_records=entity_store.list_for_case(case.id, OTHER_INCOME_RECORD),
        means_test_inputs=entity_store.list_for_case(case.id, MEANS_TEST_INPUT),
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
        plans=entity_store.list_for_case(case.id, PLAN),
    )


def disclose_tax_ids(
    data: CaseData,
    *,
    principal: str,
    purpose: str,
    tax_id_store: TaxIdStore,
    tax_id_cipher: TaxIdCipher,
    access_log: AccessLog,
) -> CaseData:
    """The logged full-value read, for every debtor of the case that carries
    a tax id — ONE `taxid.read` row per debtor, naming `principal` and
    `purpose` (insolvia_core.tax_ids.read_tax_id) — returning the data with
    `tax_ids` filled so B121 can print.

    Called by exactly three things, each about to print B121 or reproduce
    a packet that did: `run_packet_assembly` (unless the requested subset
    leaves B121 out), the single-form preview for `b121`, and the review
    worker's byte-exact re-assembly (core/petition_review.py — which then
    drops B121 from what the model sees). Never by a route that answers a
    client with case data, and never by the forms-hub listing, which prints
    nothing.
    """
    disclosed: dict[str, str] = {}
    for debtor in data.debtors:
        if debtor.tax_id is None:
            continue
        digits = read_tax_id(
            debtor.tax_id,
            firm_id=data.case.firm_id,
            case_id=data.case.id,
            filing_role=debtor.filing_role,
            principal=principal,
            purpose=purpose,
            cipher=tax_id_cipher,
            store=tax_id_store,
            access_log=access_log,
        )
        if digits is not None:
            disclosed[debtor.filing_role] = digits
    return replace(data, tax_ids=disclosed)


def to_case_file(data: CaseData) -> CaseFile:
    """The projections' input: bodies in listing order, id-paired where other
    records reference them (form_projections/shared.CaseFile's contract).

    The petition collapses to the FIRST record — the gate has already refused
    a case with more than one, so by the time a projection reads this the
    first is the only."""
    return CaseFile(
        case=data.case,
        debtors=data.debtors,
        tax_ids=data.tax_ids,
        petition=data.petitions[0].body if data.petitions else None,
        prior_cases=tuple(e.body for e in data.prior_cases),
        related_cases=tuple(e.body for e in data.related_cases),
        sole_proprietorships=tuple(e.body for e in data.sole_proprietorships),
        filing_professionals=tuple(e.body for e in data.filing_professionals),
        employments=tuple((e.id, e.body) for e in data.employments),
        income_summaries=tuple(e.body for e in data.income_summaries),
        pay_period_records=tuple(e.body for e in data.pay_period_records),
        other_income_records=tuple(e.body for e in data.other_income_records),
        means_test_inputs=tuple(e.body for e in data.means_test_inputs),
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
        plans=tuple(e.body for e in data.plans),
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
        ref = claim.body.asset_id
        if ref is not None and ref not in asset_ids:
            dangle("claims", claim.id, "asset_id", "property (asset)")
    for exemption in data.exemptions:
        ref = exemption.body.asset_id
        if ref is not None and ref not in asset_ids:
            dangle("exemptions", exemption.id, "asset_id", "property (asset)")
    for employment in data.employments:
        ref = employment.body.debtor_id
        if ref is not None and ref not in debtor_ids:
            dangle("employments", employment.id, "debtor_id", "debtor")
    employment_ids = {e.id for e in data.employments}
    for record in data.pay_period_records:
        ref = record.body.employment_id
        if ref is not None and ref not in employment_ids:
            dangle("pay_period_records", record.id, "employment_id", "employment")
    for receipt in data.other_income_records:
        ref = receipt.body.debtor_id
        if ref is not None and ref not in debtor_ids:
            dangle("other_income_records", receipt.id, "debtor_id", "debtor")
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

    if data.case.chapter == 13:
        # One plan per case, not key-enforced (insolvia_core/plans.py leaves
        # the cardinality here, as means_test_input does): B113 prints ONE
        # proposal, and a second record would be a second answer to it.
        if not data.plans:
            problems.append(
                PacketProblem(
                    source="plans",
                    item_id=None,
                    field="",
                    message="The Chapter 13 plan has not been entered yet —"
                    " Official Form 113 prints from it.",
                )
            )
        for extra_plan in data.plans[1:]:
            problems.append(
                PacketProblem(
                    source="plans",
                    item_id=extra_plan.id,
                    field="",
                    message="A case has exactly one plan record — delete the"
                    " duplicates before assembling.",
                )
            )

    for extra_input in data.means_test_inputs[1:]:
        problems.append(
            PacketProblem(
                source="means_test_inputs",
                item_id=extra_input.id,
                field="",
                message="A case has exactly one means-test input record —"
                " delete the duplicates before assembling.",
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


def _statement_problems(data: CaseData) -> list[PacketProblem]:
    """What B108 and B2030 need answered before they can print (issue #351)
    — and, on a Chapter 13 case, what B113 Part 6 needs in B108's place
    (issue #367).

    A B108 row without its intention box, or a B2030 without its amounts,
    is a signed statement with its one question blank — so each is a gate,
    named per record like every other problem. B121 and B2010 gate nothing:
    the notice has no field, and B121's "you do not have a Social Security
    number / an ITIN" boxes are not modelled, so a debtor without a stored
    tax id cannot be told apart from one who has none to give — a blank
    line 2 is that debtor's honest statement, not a defect this gate can
    name (issue 13.12 / #382 stored the number; modelling the absence is
    still open).
    """
    problems: list[PacketProblem] = []
    if data.case.chapter == 13:
        # No B108 on Chapter 13 (Rule 1007(b)(2) is Chapter 7's): each secured
        # claim's fate is the plan's treatment instead, which the B113
        # projection checks. A lease still needs its answer, for a harder
        # reason — B113 Part 6.1 lists the ASSUMED contracts and leases and
        # rejects every other one, so an unanswered lease would be rejected
        # by silence.
        for lease in data.contract_leases:
            if lease.body.intention is None:
                problems.append(
                    PacketProblem(
                        source="contract_leases",
                        item_id=lease.id,
                        field="intention",
                        message="Say whether this contract or lease will be"
                        " assumed or rejected — the Chapter 13 plan (B113"
                        " Part 6) lists the assumed ones and rejects every"
                        " other.",
                    )
                )
        problems.extend(_attorney_disclosure_problems(data))
        return problems
    for claim in data.claims:
        if claim.body.claim_class == "secured" and claim.body.intention is None:
            problems.append(
                PacketProblem(
                    source="claims",
                    item_id=claim.id,
                    field="intention",
                    message="Say what the debtor intends to do with this collateral"
                    " (surrender, redeem, reaffirm, or other) — the Statement of"
                    " Intention (B108) prints one answer per secured claim.",
                )
            )
    for lease in data.contract_leases:
        if lease.body.list_on_statement_of_intention and lease.body.intention is None:
            problems.append(
                PacketProblem(
                    source="contract_leases",
                    item_id=lease.id,
                    field="intention",
                    message="Say whether this lease will be assumed or rejected —"
                    " it is flagged for the Statement of Intention (B108), which"
                    " prints one answer per listed lease.",
                )
            )
    problems.extend(_attorney_disclosure_problems(data))
    return problems


def _attorney_disclosure_problems(data: CaseData) -> list[PacketProblem]:
    """B2030's answers — the attorney's Rule 2016(b) disclosure files on
    either chapter."""
    problems: list[PacketProblem] = []
    for professional in data.filing_professionals:
        if professional.body.role != "attorney":
            continue
        for field_name, label in (
            ("compensation_agreed", "the fee agreed for legal services"),
            ("compensation_received", "the amount received before filing"),
            ("compensation_source_paid", "who paid the compensation"),
            ("compensation_source_to_be_paid", "who will pay the balance"),
            ("compensation_shared", "whether the fee is shared outside the firm"),
        ):
            if getattr(professional.body, field_name) is None:
                problems.append(
                    PacketProblem(
                        source="filing_professionals",
                        item_id=professional.id,
                        field=field_name,
                        message=f"The attorney's compensation disclosure (B2030)"
                        f" needs {label}.",
                    )
                )
    return problems


def _local_plan_form_problem(case: Case) -> PacketProblem | None:
    """A Chapter 13 case in a district that has opted out of Official Form
    113 (Rule 3015.1) — refused, naming the district's form, rather than
    handed a national plan the court's rules say not to file.

    The court registry owns the fact (`CourtDistrict.chapter_13_plan`,
    issue #367). Only a VERIFIED `local` answer refuses: an unverified one
    ("we have not read the page") and a case written before the registry
    (`case.court` None) both take the national form, which is what Rule
    3015(c) makes the default. The district's own plan forms are data to
    add, one district at a time; until one is modelled, its cases stop
    here."""
    if case.court is None:
        return None
    district = courts.district(case.court)
    if district is None:
        return None
    fact = district.chapter_13_plan
    if not fact.verified or fact.value is None or fact.value.form != "local":
        return None
    title = fact.value.title or "its own local plan form"
    return PacketProblem(
        source="case",
        item_id=None,
        field="court",
        message=f"The {district.name} requires {title} in place of Official"
        " Form 113 (Bankruptcy Rule 3015.1), and that form is not prepared"
        " here yet — the Chapter 13 plan for this court has to be prepared"
        " outside Insolvia.",
    )


def completeness_problems(
    data: CaseData, *, options: OutputOptions = DEFAULT_OUTPUT_OPTIONS
) -> tuple[PacketProblem, ...]:
    """Every structural reason the case cannot assemble, before a single
    projection runs. Deterministic order: case, debtors, cardinality,
    references, the statements' own answers — so the same case always
    reports the same list.

    `options` (issue #370) is read for exactly one thing: `amended_only`
    lifts the filed-case refusal below. Every OTHER caller — `forms_hub.py`'s
    listing among them — passes nothing and gets exactly today's behaviour:
    a filed case still blocks every ordinary re-assembly.
    """
    problems: list[PacketProblem] = []
    if data.case.chapter not in (7, 13):
        problems.append(
            PacketProblem(
                source="case",
                item_id=None,
                field="chapter",
                message=f"This is a Chapter {data.case.chapter} case — only"
                " the individual Chapter 7 and Chapter 13 packets can be"
                " assembled today.",
            )
        )
    if data.case.chapter == 13:
        local_plan = _local_plan_form_problem(data.case)
        if local_plan is not None:
            problems.append(local_plan)
    if data.case.status == "filed" and not options.amended_only:
        problems.append(
            PacketProblem(
                source="case",
                item_id=None,
                field="status",
                message="This case is filed. A filed case's packet is pinned"
                " to the data that produced it and is never re-assembled."
                " To change a filed schedule, mark the changed items amended"
                " and assemble with amendedOnly instead.",
            )
        )
    if options.amended_only and data.case.status != "filed":
        problems.append(
            PacketProblem(
                source="case",
                item_id=None,
                field="status",
                message="amendedOnly renders an amendment to a FILED case —"
                " this case has not been filed yet.",
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
    problems.extend(_statement_problems(data))
    return tuple(problems)


def packet_form_series(data: CaseData) -> tuple[str, ...]:
    """Which of the set's forms THIS case files.

    B106J-2 prints only when Debtor 2 keeps a separate household — its own
    projection module says "packet assembly decides whether a schedule with
    nothing to say is filed at all", and an all-blank J-2 in front of a clerk
    is a question, not a filing. B122A-2 files only when the debtor is not
    determinately below the median (B122A-1 line 14; `files_b122a2` argues
    the indeterminate case), and B122C-2 likewise on a Chapter 13 case
    (B122C-1 line 17; `files_b122c2`). B108 files only when it has a row —
    a secured claim, or a lease flagged for it — because § 521(a)(2) asks
    for it only then (and never on Chapter 13, whose set has no B108);
    B2030 only when an attorney signs, because it is the attorney's own
    disclosure. Everything else — the Chapter 13 plan, B113, among it — is
    unconditional for an individual filing of the case's chapter
    (`chapter_form_series`).
    """
    has_separate = any(
        e.body.which_household == "debtor_2_separate" for e in data.households
    )
    base = chapter_form_series(data.case.chapter)
    case_file = to_case_file(data)
    skipped = set()
    if not has_separate:
        skipped.add("form/b106j2")
    if "form/b122a2" in base and not files_b122a2(case_file):
        skipped.add("form/b122a2")
    if "form/b122c2" in base and not files_b122c2(case_file):
        skipped.add("form/b122c2")
    has_statement_row = any(
        e.body.claim_class == "secured" for e in data.claims
    ) or any(e.body.list_on_statement_of_intention for e in data.contract_leases)
    if not has_statement_row:
        skipped.add("form/b108")
    if not any(e.body.role == "attorney" for e in data.filing_professionals):
        skipped.add("form/b2030")
    return tuple(series for series in base if series not in skipped)


# ── Amendments (issue #370) ──────────────────────────────────────────────────
#
# `amended` (core/case_entities.py's generic entity attribute) marks a
# SCHEDULE ITEM as changed since the case was filed. `OutputOptions.amended_
# only` renders a packet of exactly the schedules that carry one, each
# printing only ITS amended items, plus B106Sum/B106Dec (always, whenever
# that set is non-empty) and a generated cover sheet
# (core/amendment_cover_sheet.py) — never B101, B121, B108, B2010, B2030,
# B122A-1/2 or B122C-1/2, none of which prints a per-item list an "amended"
# flag could narrow. Nor B113 on a Chapter 13 case: a plan changed after
# filing is modified under § 1329 (before confirmation, § 1323), a motion of
# its own, not a Rule 1009 amendment — `plans.py` accepts and ignores the
# flag for the same reason. So a Chapter 13 amendment prints exactly what a
# Chapter 7 one does: the changed schedules, B106Sum/B106Dec, the cover.
#
# Every schedule below is fed by exactly ONE amendable collection — the
# thing that keeps `filtered_for_render` simple: narrowing that series'
# own row list can never strand a CROSS-reference another series in the SAME
# render needs, because each series gets its own per-series data view (see
# `assemble`), and only that one series' own collection is ever narrowed in
# it. A claim's creditor, an exemption's asset, a codebtor's claim — none of
# those referenced collections is touched by another series' filter.
AMENDABLE_SERIES: Final = (
    "form/b106ab",
    "form/b106c",
    "form/b106d",
    "form/b106ef",
    "form/b106g",
    "form/b106h",
    "form/b106i",
    "form/b106j",
    "form/b106j2",
    "form/b107",
)

# Re-rendered (their own "amended filing" caption ticked) whenever
# `AMENDABLE_SERIES` produces a non-empty set — B106Sum copies every
# schedule forward and B106Dec is the declaration about them, so an
# amendment to any schedule makes both of these stale, filed or not.
_ALWAYS_WITH_AMENDMENT: Final = ("form/b106sum", "form/b106dec")


def _household_ids(data: CaseData, which: str) -> frozenset[str]:
    return frozenset(e.id for e in data.households if e.body.which_household == which)


def _series_has_amended_items(data: CaseData, series_id: str) -> bool:
    """Whether ANY item feeding `series_id` is flagged amended — the test
    `amended_series_ids` filters `AMENDABLE_SERIES` by. Unknown or
    non-amendable series answer False rather than raising: a caller that
    asks about, say, "form/b101" is asking a question with an honest "no",
    not a programming error."""
    if series_id == "form/b106ab":
        return any(e.amended for e in data.assets)
    if series_id == "form/b106c":
        return any(e.amended for e in data.exemptions)
    if series_id == "form/b106d":
        return any(e.amended for e in data.claims if e.body.claim_class == "secured")
    if series_id == "form/b106ef":
        return any(
            e.amended
            for e in data.claims
            if e.body.claim_class in ("priority_unsecured", "nonpriority_unsecured")
        )
    if series_id == "form/b106g":
        return any(e.amended for e in data.contract_leases)
    if series_id == "form/b106h":
        return any(e.amended for e in data.codebtors)
    if series_id == "form/b106i":
        return any(e.amended for e in data.income_summaries)
    if series_id in ("form/b106j", "form/b106j2"):
        which = "main" if series_id == "form/b106j" else "debtor_2_separate"
        household_ids = _household_ids(data, which)
        return any(
            e.amended for e in data.expenses if e.body.household_id in household_ids
        )
    if series_id == "form/b107":
        return any(e.amended for e in data.sofa_entries)
    return False


def amended_series_ids(data: CaseData, series_ids: tuple[str, ...]) -> tuple[str, ...]:
    """The subset of `series_ids` that both `AMENDABLE_SERIES` names and
    carries at least one amended item — filing order preserved."""
    return tuple(
        series
        for series in series_ids
        if series in AMENDABLE_SERIES and _series_has_amended_items(data, series)
    )


def amendment_render_series(
    data: CaseData, series_ids: tuple[str, ...]
) -> tuple[str, ...]:
    """Every series an amendedOnly render prints, in filing order: the
    schedules carrying an amended item plus B106Sum/B106Dec — or nothing at
    all when no schedule does, because a Summary and Declaration about an
    unchanged set amend nothing. The ONE answer both packet assembly and the
    forms hub's single-form preview read, so the two cannot disagree about
    whether a form belongs to the amendment."""
    amended = amended_series_ids(data, series_ids)
    if not amended:
        return ()
    return tuple(
        series
        for series in series_ids
        if series in amended or series in _ALWAYS_WITH_AMENDMENT
    )


def filtered_for_render(data: CaseData, series_id: str) -> CaseData:
    """The render-only view of `data` for ONE series of an amendedOnly
    packet: only THIS series' own printed-row collection narrows to its
    amended items. Every other collection — including one this series
    merely RESOLVES A REFERENCE into (a claim's creditor, an exemption's or
    a secured claim's asset, a codebtor's claim or lease) — stays whole, so
    a kept row's cross-reference always resolves regardless of whether the
    referenced record is itself flagged amended. A series this module does
    not narrow (B106Sum, B106Dec, and everything outside
    `AMENDABLE_SERIES`) gets `data` back unchanged — it still summarises or
    declares over the WHOLE case, exactly as it always has; only its
    caption changes (`form_overlay.apply_amendment_options`).
    """
    if series_id == "form/b106ab":
        return replace(data, assets=tuple(e for e in data.assets if e.amended))
    if series_id == "form/b106c":
        return replace(data, exemptions=tuple(e for e in data.exemptions if e.amended))
    if series_id == "form/b106d":
        return replace(
            data,
            claims=tuple(
                e for e in data.claims if e.amended and e.body.claim_class == "secured"
            ),
        )
    if series_id == "form/b106ef":
        return replace(
            data,
            claims=tuple(
                e
                for e in data.claims
                if e.amended
                and e.body.claim_class
                in ("priority_unsecured", "nonpriority_unsecured")
            ),
        )
    if series_id == "form/b106g":
        return replace(
            data,
            contract_leases=tuple(e for e in data.contract_leases if e.amended),
        )
    if series_id == "form/b106h":
        return replace(data, codebtors=tuple(e for e in data.codebtors if e.amended))
    # form/b106i is amendable but never narrowed: Schedule I is one income
    # statement, not a list of items, so an amended income summary re-prints
    # the whole schedule — it falls through to the whole-data return below.
    if series_id in ("form/b106j", "form/b106j2"):
        which = "main" if series_id == "form/b106j" else "debtor_2_separate"
        household_ids = _household_ids(data, which)
        return replace(
            data,
            expenses=tuple(
                e
                for e in data.expenses
                if e.amended and e.body.household_id in household_ids
            ),
        )
    if series_id == "form/b107":
        return replace(
            data, sofa_entries=tuple(e for e in data.sofa_entries if e.amended)
        )
    return data


@dataclass(frozen=True)
class AssembledPacket:
    """A clean assembly: the parts in filing order, and the facts the record
    and the pins store. `parts` maps zip entry name -> exact bytes.

    `projections` keeps the per-form field values the fills were rendered
    from, keyed by series id in filing order. The packet worker ignores it —
    the PDFs are the deliverable — but the AI review worker (issue #97,
    core/petition_review.py) reads the SAME projected content the forms
    printed, which is what lets its findings cite the form's own line keys
    without parsing a PDF back apart.

    `constants_set_id` is the `code/dollar-amounts` release resolved as of
    the same assembly date as the forms — the second pin effective-dating.md
    names, recorded on the case (and this packet) in the same write."""

    parts: tuple[tuple[str, bytes], ...]
    form_revisions: Mapping[str, str]
    constants_set_id: str
    creditor_count: int
    projections: Mapping[str, FieldValues]


def assemble(
    data: CaseData,
    *,
    as_of: date,
    options: OutputOptions = DEFAULT_OUTPUT_OPTIONS,
    printed_at: datetime | None = None,
) -> AssembledPacket | tuple[PacketProblem, ...]:
    """The whole gate-then-render, pure over its inputs.

    Returns the assembled parts, or EVERY problem found — never both and
    never a partial packet, the creditor matrix's contract writ large. The
    gate's structural checks, the matrix's list, each projection's refusals
    and each fill's are all collected before anything is given up on, so one
    run reports the whole fix list.

    `options` (issue 13.11) is a plain `OutputOptions()` for the ordinary
    filing set — every step below is then exactly what it always was, and
    the output is byte-identical to a caller that never passed `options` at
    all. It touches ONLY the render loop at the bottom: the completeness
    gate, the creditor matrix, and every form's resolution/projection still
    run over the WHOLE case regardless of `options.forms` — a subset
    selection changes what gets PRINTED, not what "complete" means, so a
    packet is exactly as provably complete when printing one form as when
    printing all of them.

    `options.amended_only` (issue #370) is the one exception to "runs over
    the whole case regardless": each series still resolves and projects, but
    against a PER-SERIES view of the data (`filtered_for_render`) that
    narrows only that series' own printed row list to its amended items —
    see that function's own docstring for why this is safe for
    cross-references. It also lifts `completeness_problems`'s filed-case
    refusal (passed straight through here) and adds one gate of its own:
    nothing to amend is a problem, not a silent empty packet.
    """
    problems = list(completeness_problems(data, options=options))

    matrix = generate_creditor_matrix(data.creditors, format_for_court(data.case.court))
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
    amendment = (
        amendment_render_series(data, series_ids) if options.amended_only else ()
    )
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

    # The dollar-amounts pin resolves alongside the forms: the same as_of,
    # the same gate on failure — a packet must never record form pins while
    # leaving "which constant set applied" unanswerable.
    constants_release: dollar_amounts.Release | None = None
    try:
        constants_release = dollar_amounts.resolve(as_of)
    except LookupError as error:
        problems.append(
            PacketProblem(
                source=dollar_amounts.DOLLAR_AMOUNTS_SERIES,
                item_id=None,
                field="",
                message=str(error),
            )
        )

    projected: dict[str, FieldValues] = {}
    for series_id, release in releases.items():
        # Every OTHER series still projects the WHOLE case, unfiltered —
        # `filtered_for_render` is a no-op for anything outside
        # `AMENDABLE_SERIES` (B106Sum/B106Dec still summarise/declare over
        # everything, exactly as they always have).
        series_case_file = (
            to_case_file(filtered_for_render(data, series_id))
            if options.amended_only
            else case_file
        )
        try:
            projected[series_id] = project(release, series_case_file)
        except FormProjectionError as error:
            problems.extend(
                PacketProblem(source=series_id, item_id=None, field="", message=message)
                for message in error.problems
            )

    if problems:
        return tuple(problems)

    # A requested subset (options.forms, short keys) narrows WHICH resolved
    # forms get rendered — everything above still ran for the whole case. A
    # key naming a form this case does not currently file (typo, or a stale
    # selection from before a household/median fact changed) is a problem
    # like any other, not a silent drop: the preparer picked it on purpose.
    # `amendedOnly` narrows the same way, by a different rule — the two are
    # refused together at the request-validation layer
    # (`form_overlay.parse_output_options`), so only one of these branches
    # ever applies.
    render_series_ids = series_ids
    if options.forms is not None:
        requested = {f"form/{key}" for key in options.forms}
        problems.extend(
            PacketProblem(
                source=series_id,
                item_id=None,
                field="",
                message="This case does not currently file this form, so it "
                "cannot be included in a selected print.",
            )
            for series_id in sorted(requested - set(series_ids))
        )
        if problems:
            return tuple(problems)
        render_series_ids = tuple(s for s in series_ids if s in requested)
    elif options.amended_only:
        if not amendment:
            problems.append(
                PacketProblem(
                    source="case",
                    item_id=None,
                    field="",
                    message="No schedule items are marked amended — mark at"
                    " least one item amended before assembling an"
                    " amendedOnly packet.",
                )
            )
            return tuple(problems)
        render_series_ids = amendment

    printed_at = printed_at if printed_at is not None else datetime.now(UTC)
    parts: list[tuple[str, bytes]] = []
    if options.amended_only:
        parts.append(
            (
                "00-amendment-cover.pdf",
                render_amendment_cover_sheet(
                    case_file,
                    amended_releases=[releases[s] for s in amendment],
                    generated_at=printed_at,
                ),
            )
        )
    for position, series_id in enumerate(series_ids, start=1):
        if series_id not in render_series_ids:
            continue
        release = releases[series_id]
        values = apply_signature_options(
            release,
            projected[series_id],
            case_file=case_file,
            options=options,
            today=as_of,
        )
        values = apply_amendment_options(
            release,
            values,
            amended=options.amended_only and series_id in render_series_ids,
        )
        try:
            rendered = fill_form(release, values)
        except FormFillError as error:
            problems.extend(
                PacketProblem(source=series_id, item_id=None, field="", message=message)
                for message in error.problems
            )
            continue
        stamped = apply_output_stamps(
            rendered, release, options=options, printed_at=printed_at
        )
        if stamped is None:
            # Nothing of this form survives the requested signature-page
            # mode (e.g. "only" on a form with no signature line) — it
            # simply contributes nothing to this print, not a problem.
            continue
        parts.append((f"{position:02d}-{release.form}.pdf", stamped))
    if problems:
        return tuple(problems)

    assert matrix.content is not None  # its problems gated above
    assert constants_release is not None  # its resolution failure gated above
    parts.append((MATRIX_FILE_NAME, matrix.content.encode("ascii")))

    return AssembledPacket(
        parts=tuple(parts),
        constants_set_id=constants_release.release_id,
        # The WHOLE set pins, including a J-2 this case does not file and
        # the other chapter's means-test pair: the pin map records which
        # revisions were in force for this assembly, and a household added
        # (or a chapter changed) before re-assembly must not find a hole.
        form_revisions={
            series_id: resolve_form(series_id, as_of).pin
            for series_id in ALL_FORM_SERIES
        },
        creditor_count=matrix.creditor_count,
        projections={series_id: projected[series_id] for series_id in series_ids},
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
    # B121's number (issue 13.12 / #382): the sealed items and the cipher
    # that opens them, under the worker role's Decrypt-only grant.
    tax_id_store: TaxIdStore
    tax_id_cipher: TaxIdCipher


def prints_b121(options: OutputOptions) -> bool:
    """Whether this render will put B121 on paper — the only reason to
    perform the full-value read. A subset that leaves it out projects B121
    blank and never opens an envelope. An amendment never prints B121
    (it is not an amendable schedule), so it never opens one either."""
    if options.amended_only:
        return False
    return options.forms is None or "b121" in options.forms


def run_packet_assembly(
    job: Job,
    deps: PacketAssemblyDeps,
    *,
    today: date | None = None,
    printed_at: datetime | None = None,
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

    `job.options` (issue 13.11) was already validated and canonicalised by
    the accept route (`api.routes.jobs`) before this job was ever queued —
    re-parsing it here with `parse_output_options` is a re-validation of
    known-good data, the same trust level `parse_job_message` gives the rest
    of the envelope, not a new decision.
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
    options = (
        parse_output_options(job.options, allow_forms=True)
        if job.options is not None
        else DEFAULT_OUTPUT_OPTIONS
    )
    if prints_b121(options):
        # The full tax identifier, for B121 alone — its own audit row per
        # debtor, against the same preparer, beside the packet.assemble row.
        data = disclose_tax_ids(
            data,
            principal=job.created_by,
            purpose="b121",
            tax_id_store=deps.tax_id_store,
            tax_id_cipher=deps.tax_id_cipher,
            access_log=deps.access_log,
        )
    outcome = assemble(data, as_of=as_of, options=options, printed_at=printed_at)

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
        constants_set_id=outcome.constants_set_id,
        creditor_count=outcome.creditor_count,
        created_by=job.created_by,
        options=options,
    )
    # Bytes first, record second: an object with no record is invisible and
    # harmless (nothing lists the bucket); a record with no object would be a
    # download that 404s. The failure window between the two leaves only the
    # former.
    deps.blobs.put_bytes(
        packet.storage_ref, content=content, content_type=PACKET_CONTENT_TYPE
    )

    # An amendedOnly run never re-pins the CASE (issue #370): the case's
    # `form_revisions`/`constants_set_id` describe the ORIGINAL filing, and
    # this amendment's own resolution — possibly years later, against
    # today's current releases — belongs on the PACKET record alone
    # (`outcome.form_revisions` above), never overwriting what the case says
    # the filing used. `pinned_case=case` (unchanged) makes the transactional
    # case write below a no-op overwrite, and `allow_filed=True` is the one
    # thing that lets it proceed at all against a filed case.
    pinned = (
        case
        if options.amended_only
        else pin_case(
            case,
            form_revisions=outcome.form_revisions,
            constants_set_id=outcome.constants_set_id,
        )
    )
    stored = deps.packet_store.create(
        packet,
        pinned_case=pinned,
        expected_updated_at=case.updated_at,
        allow_filed=options.amended_only,
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
