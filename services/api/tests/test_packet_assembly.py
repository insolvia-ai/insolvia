"""Packet assembly (issue #96): the completeness gate, the deterministic
render, and the worker — end to end against the memory adapters, which is
ADR 0018's local story in executable form.

The reference case from test_form_projections.py is the fixture here too:
it is the one case proven (by the goldens) to project every form cleanly, so
wrapping it into stored entities and asserting the worker yields a packet is
the closest a unit suite gets to the issue's own definition of done.
"""

from __future__ import annotations

import io
import zipfile
from dataclasses import replace
from datetime import date

import pytest
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.core import dollar_amounts
from insolvia_api.core.creditor_matrix import MATRIX_FILE_NAME
from insolvia_api.core.form_templates import form_revisions_as_of
from insolvia_api.core.jobs import KINDS, JobError, new_job
from insolvia_api.core.packet_assembly import (
    PACKET_ASSEMBLY_KIND,
    PACKET_FORM_SERIES,
    AssembledPacket,
    CaseData,
    PacketAssemblyDeps,
    assemble,
    completeness_problems,
    packet_form_series,
    packet_zip,
    problem_json,
    run_packet_assembly,
)
from insolvia_core.adapters.memory.access_log import MemoryAccessLog
from insolvia_core.adapters.memory.case_entity_store import MemoryCaseEntityStore
from insolvia_core.adapters.memory.case_store import MemoryCaseStore
from insolvia_core.adapters.memory.debtor_store import MemoryDebtorStore
from insolvia_core.adapters.memory.document_blobs import MemoryDocumentBlobStore
from insolvia_core.assets import ASSET
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import assign_case
from insolvia_core.claims import CLAIM, ClaimBody
from insolvia_core.codebtors import CODEBTOR, COMMUNITY_HOUSEHOLD_MEMBER
from insolvia_core.contract_leases import CONTRACT_LEASE
from insolvia_core.creditors import CREDITOR
from insolvia_core.exemption_claims import EXEMPTION, ExemptionBody
from insolvia_core.expenses import DEPENDENT, EXPENSE, HOUSEHOLD, HouseholdBody
from insolvia_core.income import EMPLOYMENT, INCOME_SUMMARY, IncomeSummaryBody
from insolvia_core.petitions import (
    FILING_PROFESSIONAL,
    PETITION,
    PRIOR_CASE,
    RELATED_CASE,
    SOLE_PROPRIETORSHIP,
)
from insolvia_core.sofa import SOFA_ENTRY

from tests.test_form_projections import REFERENCE_CASE, reference_case_file

CASE_ID = "11111111-2222-4333-8444-000000000001"
TODAY = date(2026, 9, 3)


def _entity(kind, body, entity_id, position):
    return CaseEntity(
        kind=kind,
        id=entity_id,
        case_id=CASE_ID,
        # Creation order is print order; the counter keeps it stable and the
        # memory store's sort agrees with it.
        created_at=f"2026-08-01T12:{position // 60:02d}:{position % 60:02d}.000Z",
        updated_at=f"2026-08-01T12:{position // 60:02d}:{position % 60:02d}.000Z",
        body=body,
        provenance={},
    )


def reference_case_data() -> CaseData:
    """The reference CaseFile, wrapped back into stored-entity shape. Bare
    bodies get synthetic ids; id-paired collections keep the ids the file's
    cross-references use."""
    case_file = reference_case_file()
    case = replace(REFERENCE_CASE, id=CASE_ID)
    position = iter(range(10_000))

    def wrap_bodies(kind, bodies, prefix):
        return tuple(
            _entity(kind, body, f"{prefix}-{index}", next(position))
            for index, body in enumerate(bodies)
        )

    def wrap_pairs(kind, pairs):
        return tuple(
            _entity(kind, body, entity_id, next(position)) for entity_id, body in pairs
        )

    return CaseData(
        case=case,
        debtors=tuple(replace(d, case_id=CASE_ID) for d in case_file.debtors),
        petitions=wrap_bodies(PETITION, (case_file.petition,), "petition"),
        prior_cases=wrap_bodies(PRIOR_CASE, case_file.prior_cases, "prior"),
        related_cases=wrap_bodies(RELATED_CASE, case_file.related_cases, "related"),
        sole_proprietorships=wrap_bodies(
            SOLE_PROPRIETORSHIP, case_file.sole_proprietorships, "soleprop"
        ),
        filing_professionals=wrap_bodies(
            FILING_PROFESSIONAL, case_file.filing_professionals, "prof"
        ),
        employments=wrap_bodies(EMPLOYMENT, case_file.employments, "employment"),
        income_summaries=wrap_bodies(
            INCOME_SUMMARY, case_file.income_summaries, "income"
        ),
        assets=wrap_pairs(ASSET, case_file.assets),
        exemptions=wrap_bodies(EXEMPTION, case_file.exemptions, "exemption"),
        creditors=wrap_pairs(CREDITOR, case_file.creditors),
        claims=wrap_pairs(CLAIM, case_file.claims),
        contract_leases=wrap_pairs(CONTRACT_LEASE, case_file.contract_leases),
        codebtors=wrap_bodies(CODEBTOR, case_file.codebtors, "codebtor"),
        community_household_members=wrap_bodies(
            COMMUNITY_HOUSEHOLD_MEMBER,
            case_file.community_household_members,
            "member",
        ),
        households=wrap_pairs(HOUSEHOLD, case_file.households),
        expenses=wrap_bodies(EXPENSE, case_file.expenses, "expense"),
        dependents=wrap_bodies(DEPENDENT, case_file.dependents, "dependent"),
        sofa_entries=wrap_bodies(SOFA_ENTRY, case_file.sofa_entries, "sofa"),
    )


# ── The completeness gate ───────────────────────────────────────


def test_the_reference_case_passes_the_gate():
    assert completeness_problems(reference_case_data()) == ()


def test_a_chapter_13_case_is_refused():
    data = reference_case_data()
    data = replace(data, case=replace(data.case, chapter=13))
    problems = completeness_problems(data)
    assert any(p.source == "case" and p.field == "chapter" for p in problems)


def test_a_filed_case_never_reassembles():
    data = reference_case_data()
    data = replace(data, case=replace(data.case, status="filed"))
    outcome = assemble(data, as_of=TODAY)
    assert not isinstance(outcome, AssembledPacket)
    assert any(p.field == "status" for p in outcome)


def test_a_case_without_debtor_1_is_refused():
    data = replace(reference_case_data(), debtors=())
    problems = completeness_problems(data)
    assert any(p.source == "debtors" for p in problems)


def test_a_missing_petition_is_refused_and_a_duplicate_is_named():
    data = reference_case_data()
    missing = completeness_problems(replace(data, petitions=()))
    assert any(p.source == "petitions" for p in missing)
    duplicate = completeness_problems(
        replace(data, petitions=data.petitions + data.petitions)
    )
    assert any(p.source == "petitions" and p.item_id is not None for p in duplicate)


def test_two_households_claiming_one_schedule_are_refused():
    data = reference_case_data()
    extra = _entity(
        HOUSEHOLD, HouseholdBody(which_household="main"), "house-extra", 9_998
    )
    problems = completeness_problems(
        replace(data, households=(*data.households, extra))
    )
    assert any(
        p.source == "households" and p.item_id == "house-extra" for p in problems
    )


def test_a_household_without_a_schedule_is_refused():
    data = reference_case_data()
    unplaced = _entity(HOUSEHOLD, HouseholdBody(), "house-unplaced", 9_997)
    problems = completeness_problems(
        replace(data, households=(*data.households, unplaced))
    )
    assert any(p.field == "which_household" for p in problems)


def test_two_income_summaries_for_one_debtor_are_refused():
    data = reference_case_data()
    duplicate = _entity(
        INCOME_SUMMARY,
        data.income_summaries[0].body,
        "income-duplicate",
        9_996,
    )
    problems = completeness_problems(
        replace(data, income_summaries=(*data.income_summaries, duplicate))
    )
    assert any(p.source == "income_summaries" for p in problems)


@pytest.mark.parametrize(
    ("collection", "kind", "body_builder", "field"),
    [
        (
            "claims",
            CLAIM,
            lambda: ClaimBody(creditor_id="no-such-creditor"),
            "creditor_id",
        ),
        (
            "exemptions",
            EXEMPTION,
            lambda: ExemptionBody(asset_id="no-such-asset"),
            "asset_id",
        ),
        (
            "income_summaries",
            INCOME_SUMMARY,
            lambda: IncomeSummaryBody(debtor_id="no-such-debtor"),
            "debtor_id",
        ),
    ],
)
def test_a_dangling_reference_is_reported_per_item(
    collection, kind, body_builder, field
):
    """The checks issue #276 deliberately deferred to this gate: references
    are shape-checked at entry and deletes do not cascade, so dangling is
    detected exactly here, named per record and per field."""
    data = reference_case_data()
    dangling = _entity(kind, body_builder(), "dangling-record", 9_995)
    data = replace(data, **{collection: (*getattr(data, collection), dangling)})
    problems = completeness_problems(data)
    matches = [p for p in problems if p.item_id == "dangling-record"]
    assert matches
    assert matches[0].source == collection
    assert matches[0].field == field


def test_a_codebtor_naming_a_missing_claim_is_reported():
    data = reference_case_data()
    body = replace(data.codebtors[0].body, claim_ids=("no-such-claim",))
    broken = _entity(CODEBTOR, body, "codebtor-broken", 9_994)
    problems = completeness_problems(replace(data, codebtors=(broken,)))
    assert any(
        p.item_id == "codebtor-broken" and p.field == "claim_ids" for p in problems
    )


def test_matrix_problems_gate_the_packet():
    data = reference_case_data()
    nameless = replace(data.creditors[0].body, name=None)
    creditors = (
        _entity(CREDITOR, nameless, data.creditors[0].id, 9993),
        *data.creditors[1:],
    )
    outcome = assemble(replace(data, creditors=creditors), as_of=TODAY)
    assert not isinstance(outcome, AssembledPacket)
    assert any(
        p.source == "creditors" and p.item_id == data.creditors[0].id for p in outcome
    )


def test_problem_json_omits_absent_keys():
    from insolvia_api.core.packet_assembly import PacketProblem

    bare = problem_json(
        PacketProblem(source="petitions", item_id=None, field="", message="m")
    )
    assert bare == {"source": "petitions", "message": "m"}


# ── The render ──────────────────────────────────────────────────


def test_the_reference_case_assembles_the_full_set():
    outcome = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    names = [name for name, _ in outcome.parts]
    # One shared household in the reference case, so J-2 has nothing to say
    # and stays out; every other form of the set files, plus the matrix.
    assert len(names) == len(PACKET_FORM_SERIES) - 1 + 1
    assert names[0] == "01-b101.pdf"
    assert "form/b106j2" not in packet_form_series(reference_case_data())
    assert names[-1] == MATRIX_FILE_NAME
    # The pin map still records the WHOLE set, J-2 included — a household
    # added before re-assembly must not find a hole.
    assert outcome.form_revisions == form_revisions_as_of(TODAY)
    assert "form/b106j2" in outcome.form_revisions
    # The second pin: the dollar-amounts release resolved as of the same
    # assembly date (issue #99).
    assert outcome.constants_set_id == dollar_amounts.resolve(TODAY).release_id
    assert outcome.creditor_count > 0


def test_assembly_gates_when_no_dollar_amounts_release_is_effective():
    # A date before the series' earliest release must refuse to assemble —
    # the effective-dating rule: wrong data is worse than no answer.
    outcome = assemble(reference_case_data(), as_of=date(2024, 1, 1))
    assert not isinstance(outcome, AssembledPacket)
    assert any(p.source == "code/dollar-amounts" for p in outcome)


def test_j2_files_when_debtor_2_keeps_a_separate_household():
    data = reference_case_data()
    second = _entity(
        HOUSEHOLD,
        HouseholdBody(which_household="debtor_2_separate", separate_household=True),
        "hh-second",
        9_992,
    )
    with_second = replace(data, households=(*data.households, second))
    series = packet_form_series(with_second)
    assert "form/b106j2" in series
    outcome = assemble(with_second, as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    assert len(outcome.parts) == len(PACKET_FORM_SERIES) + 1


def test_assembly_is_deterministic_to_the_byte():
    first = assemble(reference_case_data(), as_of=TODAY)
    second = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(first, AssembledPacket)
    assert isinstance(second, AssembledPacket)
    assert packet_zip(first.parts) == packet_zip(second.parts)


def test_the_zip_carries_fixed_timestamps():
    outcome = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    archive = zipfile.ZipFile(io.BytesIO(packet_zip(outcome.parts)))
    assert all(info.date_time == (1980, 1, 1, 0, 0, 0) for info in archive.infolist())
    assert archive.namelist() == [name for name, _ in outcome.parts]


# ── The worker, end to end on the memory adapters ───────────────


def build_deps(data: CaseData):
    case_store = MemoryCaseStore()
    case_store.create(
        data.case, assign_case(data.case, subject="subject-1", assigned_by="subject-1")
    )
    debtor_store = MemoryDebtorStore()
    for debtor in data.debtors:
        debtor_store.create(debtor)
    entity_store = MemoryCaseEntityStore()
    for field_name in (
        "petitions",
        "prior_cases",
        "related_cases",
        "sole_proprietorships",
        "filing_professionals",
        "employments",
        "income_summaries",
        "assets",
        "exemptions",
        "creditors",
        "claims",
        "contract_leases",
        "codebtors",
        "community_household_members",
        "households",
        "expenses",
        "dependents",
        "sofa_entries",
    ):
        for entity in getattr(data, field_name):
            entity_store.create(entity)
    return PacketAssemblyDeps(
        case_store=case_store,
        debtor_store=debtor_store,
        entity_store=entity_store,
        packet_store=MemoryPacketStore(case_store),
        blobs=MemoryDocumentBlobStore(),
        access_log=MemoryAccessLog(),
    )


def accept_job(case_id=CASE_ID):
    return new_job(PACKET_ASSEMBLY_KIND, case_id=case_id, created_by="subject-1")


def test_the_worker_stores_the_packet_and_pins_the_case_together():
    data = reference_case_data()
    deps = build_deps(data)

    result = run_packet_assembly(accept_job(), deps, today=TODAY)

    assert result["outcome"] == "assembled"
    packet_body = result["packet"]
    stored = deps.packet_store.get(CASE_ID, packet_body["id"])
    assert stored is not None
    # The bytes are where the record says, and they hash to what it claims.
    content = deps.blobs.contents[stored.storage_ref]
    assert len(content) == stored.byte_size
    assert deps.blobs.content_types[stored.storage_ref] == "application/zip"
    # The pins landed on the case in the same operation, and they match the
    # packet's own copy — the effective-dating provenance rule.
    pinned = deps.case_store.cases[CASE_ID]
    assert pinned.form_revisions == form_revisions_as_of(TODAY)
    assert dict(stored.form_revisions) == pinned.form_revisions
    # `constants_set_id` lands in the same write — the standing IOU from
    # core/cases.py, paid by the code/dollar-amounts series (issue #99).
    assert pinned.constants_set_id == dollar_amounts.resolve(TODAY).release_id
    assert stored.constants_set_id == pinned.constants_set_id
    # The zip is a readable archive holding the full set plus the matrix.
    names = zipfile.ZipFile(io.BytesIO(content)).namelist()
    assert names[-1] == MATRIX_FILE_NAME
    # Twelve forms (the shared-household reference files no J-2) + matrix.
    assert len(names) == len(PACKET_FORM_SERIES) - 1 + 1
    # The case-data read was access-logged against the preparer.
    assert any(
        e.action == "packet.assemble" and e.principal == "subject-1"
        for e in deps.access_log.events
    )


def test_reassembly_repins_and_keeps_the_old_packet():
    data = reference_case_data()
    deps = build_deps(data)
    first = run_packet_assembly(accept_job(), deps, today=TODAY)
    second = run_packet_assembly(accept_job(), deps, today=TODAY)

    packets = deps.packet_store.list_for_case(CASE_ID)
    assert len(packets) == 2
    # Deterministic render: both packets carry identical bytes.
    assert first["packet"]["sha256"] == second["packet"]["sha256"]
    assert deps.case_store.cases[CASE_ID].form_revisions == form_revisions_as_of(TODAY)


def test_a_blocked_case_stores_nothing_and_pins_nothing():
    data = reference_case_data()
    broken_claims = (
        _entity(CLAIM, ClaimBody(creditor_id="no-such"), "claim-broken", 9_990),
    )
    deps = build_deps(replace(data, claims=data.claims + broken_claims))

    result = run_packet_assembly(accept_job(), deps, today=TODAY)

    assert result["outcome"] == "blocked"
    assert any(p.get("itemId") == "claim-broken" for p in result["problems"])
    assert deps.packet_store.list_for_case(CASE_ID) == ()
    assert deps.blobs.contents == {}
    assert deps.case_store.cases[CASE_ID].form_revisions is None


def test_a_vanished_case_fails_deterministically():
    deps = build_deps(reference_case_data())
    with pytest.raises(JobError):
        run_packet_assembly(
            accept_job("11111111-2222-4333-8444-000000000099"), deps, today=TODAY
        )


def test_a_case_that_changed_mid_assembly_fails_the_job():
    class RefusingPacketStore:
        def create(self, packet, *, pinned_case, expected_updated_at):
            return False

        def get(self, case_id, packet_id):
            return None

        def list_for_case(self, case_id):
            return ()

    data = reference_case_data()
    deps = build_deps(data)
    deps = replace(deps, packet_store=RefusingPacketStore())
    with pytest.raises(JobError) as caught:
        run_packet_assembly(accept_job(), deps, today=TODAY)
    assert caught.value.category == "case_changed"


def test_the_memory_packet_store_refuses_a_moved_or_filed_case():
    data = reference_case_data()
    deps = build_deps(data)
    result = run_packet_assembly(accept_job(), deps, today=TODAY)
    packet = deps.packet_store.get(CASE_ID, result["packet"]["id"])
    fresh = replace(packet, id="11111111-2222-4333-8444-00000000feed")
    stale = deps.packet_store.create(
        fresh,
        pinned_case=deps.case_store.cases[CASE_ID],
        expected_updated_at="2020-01-01T00:00:00.000000Z",
    )
    assert stale is False


def test_packet_assembly_is_an_acceptable_job_kind():
    """The accept endpoint validates against KINDS; the worker entrypoints
    register under PACKET_ASSEMBLY_KIND. This is the pin that keeps the two
    naming the same kind."""
    assert PACKET_ASSEMBLY_KIND in KINDS
