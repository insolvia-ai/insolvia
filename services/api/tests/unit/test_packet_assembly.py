"""Packet assembly (issue #96): the completeness gate, the deterministic
render, and the worker — end to end against the memory adapters, which is
ADR 0018's local story in executable form.

The reference case from test_form_projections.py is the fixture here too:
it is the one case proven (by the goldens) to project every form cleanly, so
wrapping it into stored entities and asserting the worker yields a packet is
the closest a unit suite gets to the issue's own definition of done.
"""

from __future__ import annotations

import hashlib
import io
import zipfile
from dataclasses import replace
from datetime import UTC, date, datetime

import pytest
from insolvia_api.adapters.memory.packet_store import MemoryPacketStore
from insolvia_api.core import dollar_amounts
from insolvia_api.core.creditor_matrix import MATRIX_FILE_NAME
from insolvia_api.core.form_fill import Text
from insolvia_api.core.form_overlay import OutputOptions
from insolvia_api.core.form_projections import CaseFile
from insolvia_api.core.form_templates import form_revisions_as_of, latest_form
from insolvia_api.core.jobs import KINDS, JobError, new_job
from insolvia_api.core.packet_assembly import (
    ALL_FORM_SERIES,
    CHAPTER_13_FORM_SERIES,
    PACKET_ASSEMBLY_KIND,
    PACKET_FORM_SERIES,
    AssembledPacket,
    CaseData,
    PacketAssemblyDeps,
    assemble,
    chapter_form_series,
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
from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.adapters.memory.tax_id_store import MemoryTaxIdStore
from insolvia_core.assets import ASSET
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import assign_case
from insolvia_core.claims import CLAIM, ClaimBody
from insolvia_core.codebtors import CODEBTOR, COMMUNITY_HOUSEHOLD_MEMBER
from insolvia_core.contract_leases import CONTRACT_LEASE
from insolvia_core.creditors import CREDITOR
from insolvia_core.exemption_claims import EXEMPTION, ExemptionBody
from insolvia_core.expenses import DEPENDENT, EXPENSE, HOUSEHOLD, HouseholdBody
from insolvia_core.income import (
    EMPLOYMENT,
    INCOME_SUMMARY,
    OTHER_INCOME_RECORD,
    PAY_PERIOD_RECORD,
    IncomeSummaryBody,
)
from insolvia_core.means_test_inputs import MEANS_TEST_INPUT
from insolvia_core.petitions import (
    FILING_PROFESSIONAL,
    PETITION,
    PRIOR_CASE,
    RELATED_CASE,
    SOLE_PROPRIETORSHIP,
)
from insolvia_core.plans import PLAN
from insolvia_core.sofa import SOFA_ENTRY
from insolvia_core.tax_ids import TaxIdInput, store_tax_id
from pypdf import PdfReader

from tests.unit.test_form_projections import (
    reference_case_file,
    reference_case_file_chapter_13,
)

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


def reference_case_data(case_file: CaseFile | None = None) -> CaseData:
    """The reference CaseFile, wrapped back into stored-entity shape. Bare
    bodies get synthetic ids; id-paired collections keep the ids the file's
    cross-references use."""
    case_file = case_file if case_file is not None else reference_case_file()
    case = replace(case_file.case, id=CASE_ID)
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
        # The disclosed state, like the file: `assemble()` called directly on
        # this prints B121's numbers; `build_deps` below seals the same
        # digits so the WORKER path reaches them through the logged read.
        tax_ids=case_file.tax_ids,
        petitions=wrap_bodies(PETITION, (case_file.petition,), "petition"),
        prior_cases=wrap_bodies(PRIOR_CASE, case_file.prior_cases, "prior"),
        related_cases=wrap_bodies(RELATED_CASE, case_file.related_cases, "related"),
        sole_proprietorships=wrap_bodies(
            SOLE_PROPRIETORSHIP, case_file.sole_proprietorships, "soleprop"
        ),
        filing_professionals=wrap_bodies(
            FILING_PROFESSIONAL, case_file.filing_professionals, "prof"
        ),
        employments=wrap_pairs(EMPLOYMENT, case_file.employments),
        income_summaries=wrap_bodies(
            INCOME_SUMMARY, case_file.income_summaries, "income"
        ),
        pay_period_records=wrap_bodies(
            PAY_PERIOD_RECORD, case_file.pay_period_records, "payperiod"
        ),
        other_income_records=wrap_bodies(
            OTHER_INCOME_RECORD, case_file.other_income_records, "otherincome"
        ),
        means_test_inputs=wrap_bodies(
            MEANS_TEST_INPUT, case_file.means_test_inputs, "meanstest"
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
        plans=wrap_bodies(PLAN, case_file.plans, "plan"),
    )


def reference_chapter_13_case_data() -> CaseData:
    """The same family under Chapter 13, with its plan (issue #367)."""
    return reference_case_data(reference_case_file_chapter_13())


# ── The completeness gate ───────────────────────────────────────


def test_the_reference_case_passes_the_gate():
    assert completeness_problems(reference_case_data()) == ()


def test_the_chapter_13_reference_case_passes_the_gate():
    assert completeness_problems(reference_chapter_13_case_data()) == ()


def test_the_chapter_13_reference_case_assembles_with_its_plan():
    """Issue #367's done-when, on the reference case: the Chapter 13 set,
    B122C in place of B122A, no B108, and the plan closing the set."""
    outcome = assemble(reference_chapter_13_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    forms = [name.split("-", 1)[1] for name, _ in outcome.parts[:-1]]
    assert forms[-3:] == ["b122c1.pdf", "b122c2.pdf", "b113.pdf"]
    assert "b108.pdf" not in forms
    assert not {"b122a1.pdf", "b122a2.pdf"} & set(forms)
    assert outcome.projections["form/b113"]["line_2_5_total"] == Text("30,000.00")
    assert outcome.form_revisions["form/b113"] == "2017-12-01"


def test_a_chapter_13_case_without_a_plan_is_refused():
    data = replace(reference_chapter_13_case_data(), plans=())
    problems = completeness_problems(data)
    assert [p.source for p in problems] == ["plans"]
    assert "Official Form 113" in problems[0].message


def test_a_second_plan_record_is_refused_by_id():
    data = reference_chapter_13_case_data()
    extra = replace(data.plans[0], id="plan-extra")
    problems = completeness_problems(replace(data, plans=(*data.plans, extra)))
    assert [(p.source, p.item_id) for p in problems] == [("plans", "plan-extra")]


def test_a_chapter_13_lease_without_an_answer_is_refused():
    # B113 Part 6 assumes the listed leases and rejects every other one, so
    # an unanswered lease would be rejected by silence.
    data = reference_chapter_13_case_data()
    leases = tuple(
        replace(e, body=replace(e.body, intention=None)) if e.id == "cl-storage" else e
        for e in data.contract_leases
    )
    problems = completeness_problems(replace(data, contract_leases=leases))
    assert [(p.source, p.item_id, p.field) for p in problems] == [
        ("contract_leases", "cl-storage", "intention")
    ]


def test_a_chapter_13_secured_claim_needs_no_statement_of_intention():
    # B108 is Chapter 7's (Rule 1007(b)(2)); on Chapter 13 the plan treats
    # the claim instead.
    data = reference_chapter_13_case_data()
    claims = tuple(
        replace(e, body=replace(e.body, intention=None)) for e in data.claims
    )
    assert completeness_problems(replace(data, claims=claims)) == ()


def test_an_infeasible_plan_is_refused_by_the_plan_form():
    data = reference_chapter_13_case_data()
    starved = replace(
        data.plans[0], body=replace(data.plans[0].body, monthly_payment="50.00")
    )
    outcome = assemble(replace(data, plans=(starved,)), as_of=TODAY)
    assert not isinstance(outcome, AssembledPacket)
    assert {p.source for p in outcome} == {"form/b113"}
    assert any("not feasible" in p.message for p in outcome)


@pytest.mark.parametrize(
    ("court", "refused"),
    [
        # FLSB's local form is verified in the registry (@2026-09-26+2).
        ("flsb", True),
        # FLMB's answer is unverified: the national form is the default.
        ("flmb", False),
        # A case written before the registry names no court.
        (None, False),
    ],
)
def test_a_district_with_a_local_plan_form_is_refused(court, refused):
    data = reference_chapter_13_case_data()
    data = replace(data, case=replace(data.case, court=court))
    problems = completeness_problems(data)
    local = [p for p in problems if p.source == "case" and p.field == "court"]
    assert bool(local) is refused
    if refused:
        assert "Rule 3015.1" in local[0].message
        assert "Official Form 113" in local[0].message


def test_a_chapter_7_case_is_not_asked_for_a_plan():
    data = reference_case_data()
    assert "form/b113" not in packet_form_series(data)
    assert completeness_problems(data) == ()


def test_a_chapter_11_case_is_refused_too():
    data = reference_case_data()
    data = replace(data, case=replace(data.case, chapter=11))
    problems = completeness_problems(data)
    assert any(
        p.source == "case" and p.field == "chapter" and "Chapter 11" in p.message
        for p in problems
    )


def test_a_chapter_13_case_files_the_b122c_pair_in_place_of_b122a():
    # The set the forms hub lists for a Chapter 13 case (issues #365, #367):
    # the Chapter 7 set with the Chapter 13 means-test pair — C-2 only above
    # the median, exactly as A-2 only files above it on Chapter 7 — without
    # B108, and with the plan last.
    data = reference_case_data()
    thirteen = replace(data, case=replace(data.case, chapter=13))
    series = packet_form_series(thirteen)
    assert series[-3:] == ("form/b122c1", "form/b122c2", "form/b113")
    assert not {"form/b122a1", "form/b122a2", "form/b108"} & set(series)
    assert series[:-3] == tuple(
        s for s in packet_form_series(data)[:-2] if s != "form/b108"
    )
    shrunk = replace(
        thirteen,
        pay_period_records=tuple(
            replace(entity, body=replace(entity.body, gross="4500.00"))
            for entity in data.pay_period_records
        ),
    )
    below = packet_form_series(shrunk)
    assert "form/b122c1" in below
    assert "form/b122c2" not in below


def test_the_pin_map_covers_both_chapters_form_sets():
    assert set(ALL_FORM_SERIES) == set(form_revisions_as_of(TODAY))
    assert set(CHAPTER_13_FORM_SERIES) | set(PACKET_FORM_SERIES) == set(ALL_FORM_SERIES)
    assert chapter_form_series(13) == CHAPTER_13_FORM_SERIES
    assert chapter_form_series(7) == PACKET_FORM_SERIES


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
            "claims",
            CLAIM,
            lambda: ClaimBody(claim_class="secured", asset_id="no-such-asset"),
            "asset_id",
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
    # Nineteen series (issue #351 added B108, B121, B2010 and B2030):
    # eighteen forms for this case, then the matrix.
    assert len(PACKET_FORM_SERIES) == 19
    assert len(names) == 19
    assert names[0] == "01-b101.pdf"
    assert names[1] == "02-b121.pdf"
    # The zip entries are the filed series, numbered in filing order.
    filed = packet_form_series(reference_case_data())
    assert [name.split("-", 1)[1] for name in names[:-1]] == [
        f"{series.removeprefix('form/')}.pdf" for series in filed
    ]
    assert {"form/b108", "form/b121", "form/b2010", "form/b2030"} <= set(filed)
    assert "form/b106j2" not in filed
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


# ── Output options (issue 13.11) ─────────────────────────────────


# Pinned exactly as core/form_fill.py's own goldens pin a sha256 (this
# module's docstring, "byte-exact"): computed from assemble()'s output BEFORE
# core/form_overlay.py existed, and re-verified against the base commit this
# feature branched from — most recently #383 (B108/B121/B2010/B2030 joining
# the Chapter 7 packet), which changed the plain set's own form list and
# order, so the golden moved with it. A change to that hash means the plain,
# unwatermarked render moved — which issue 13.11 promises never happens.
# Re-pinned once by issue 13.12 / #382: the reference case gained its two
# tax identifiers, so B121 (part 02) prints lines 2-3 and B101 (part 01) its
# line 3 boxes — the same deliberate move as a goldens regeneration.
# Re-pinned by issue #367: B2030 (part 16) draws each overlay value in its
# own saved graphics state (black fill, reset text state) — the same values
# in the same places, different content-stream bytes.
PLAIN_PACKET_SHA256 = "f52503cefe9589055842dbf1c195031055d61e86a48f8d2101f19d18cb71d5b7"


def test_output_options_default_to_the_plain_filing_set():
    """assemble() with NO options at all, and assemble() with an explicit
    OutputOptions() (every option off), must produce the exact same bytes —
    the default IS the plain filing set, not an approximation of it."""
    without_options = assemble(reference_case_data(), as_of=TODAY)
    with_default_options = assemble(
        reference_case_data(), as_of=TODAY, options=OutputOptions()
    )
    assert isinstance(without_options, AssembledPacket)
    assert isinstance(with_default_options, AssembledPacket)
    assert packet_zip(without_options.parts) == packet_zip(with_default_options.parts)


def test_the_plain_packet_bytes_are_unchanged_by_output_options():
    """The done-when of issue 13.11: the unwatermarked packet's bytes must be
    unchanged. Renders the SAME reference case and compares the plain bytes
    to the pinned golden above — the same case, with no options requested,
    still produces exactly what it always did."""
    outcome = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    assert hashlib.sha256(packet_zip(outcome.parts)).hexdigest() == PLAIN_PACKET_SHA256


def test_a_draft_watermark_changes_the_bytes_but_not_the_form_set():
    plain = assemble(reference_case_data(), as_of=TODAY)
    draft = assemble(
        reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(draft_watermark=True, print_date=True),
        printed_at=datetime(2026, 9, 3, 14, 30, tzinfo=UTC),
    )
    assert isinstance(plain, AssembledPacket)
    assert isinstance(draft, AssembledPacket)
    assert packet_zip(plain.parts) != packet_zip(draft.parts)
    # Same forms, same order, same pins — only the pages themselves differ.
    assert [name for name, _ in plain.parts] == [name for name, _ in draft.parts]
    assert plain.form_revisions == draft.form_revisions
    b101 = next(content for name, content in draft.parts if name == "01-b101.pdf")
    text = PdfReader(io.BytesIO(b101)).pages[0].extract_text()
    assert "DRAFT" in text
    assert "Printed 2026-09-03 14:30 UTC" in text


def test_signature_pages_only_narrows_every_form_to_its_signature_block():
    outcome = assemble(
        reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(signature_pages="only"),
    )
    assert isinstance(outcome, AssembledPacket)
    names = [name for name, _ in outcome.parts]
    # A form with no signature line of its own (every schedule) drops out
    # entirely; B101 keeps only its three signature pages (7, 8, 9).
    assert "01-b101.pdf" in names
    assert not any("b106ab" in name for name in names)
    assert not any("b106j" in name for name in names)
    assert names[-1] == MATRIX_FILE_NAME  # plain text, untouched by page selection
    b101 = next(content for name, content in outcome.parts if name == "01-b101.pdf")
    assert len(PdfReader(io.BytesIO(b101)).pages) == 3


def test_sign_electronically_fills_the_debtor_signature_line():
    outcome = assemble(
        reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(sign_electronically=True, print_date=True),
        printed_at=datetime(2026, 9, 3, 14, 30, tzinfo=UTC),
    )
    assert isinstance(outcome, AssembledPacket)
    b101 = next(content for name, content in outcome.parts if name == "01-b101.pdf")
    fields = PdfReader(io.BytesIO(b101)).get_fields()
    assert fields is not None
    # Ada Quinn Lovelace — the reference case's Debtor 1 (test_form_projections).
    assert fields["Debtor1.signature"].value == "/s/ Ada Quinn Lovelace"
    assert fields["Executed on"].value == "09/03/2026"
    # The attorney's own signature line is left wet — this feature signs on
    # a DEBTOR's behalf, never an attorney's.
    assert fields["Attorney.Sig"].value in (None, "")


def test_a_subset_of_forms_renders_only_those_forms():
    outcome = assemble(
        reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(forms=("b101", "b106i")),
    )
    assert isinstance(outcome, AssembledPacket)
    names = [name for name, _ in outcome.parts]
    assert names == ["01-b101.pdf", "10-b106i.pdf", MATRIX_FILE_NAME]
    # The pin map is unaffected by a print selection — it always pins the
    # whole set the case actually files.
    assert outcome.form_revisions == form_revisions_as_of(TODAY)


def test_an_unfiled_form_named_in_the_subset_is_a_problem():
    # The reference case keeps no separate household for Debtor 2, so J-2
    # is not part of this case's own set — naming it is a stale selection,
    # not a silent no-op.
    outcome = assemble(
        reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(forms=("b106j2",)),
    )
    assert not isinstance(outcome, AssembledPacket)
    assert any(p.source == "form/b106j2" for p in outcome)


# ── Amendments (issue #370) ──────────────────────────────────────


def _filed_reference_case_data() -> CaseData:
    data = reference_case_data()
    return replace(data, case=replace(data.case, status="filed"))


def _with_amended(data: CaseData, field: str, entity_id: str) -> CaseData:
    """A copy of `data` with exactly ONE entity of `field` flagged
    amended — the test's stand-in for a PUT with `amended: true` against a
    filed case."""
    updated = tuple(
        replace(entity, amended=True) if entity.id == entity_id else entity
        for entity in getattr(data, field)
    )
    return replace(data, **{field: updated})


def _part_bytes(outcome: AssembledPacket, suffix: str) -> bytes:
    ((_, content),) = (
        (name, content) for name, content in outcome.parts if name.endswith(suffix)
    )
    return content


def _pdf_field_value(content: bytes, series_id: str, field_id: str):
    """The AcroForm value of one logical field, resolved through its
    release's own `pdf_names` — field VALUES (unlike stamped text) are not
    reliably present in `extract_text()` without generated appearances, so
    every check here reads `get_fields()` instead."""
    pdf_name = latest_form(series_id).field(field_id).pdf_names[0]
    fields = PdfReader(io.BytesIO(content)).get_fields() or {}
    return fields.get(pdf_name)


def test_amended_only_refuses_an_unfiled_case():
    # `amended` cannot even be SET before filing (core/case_entities.py's
    # own rule, enforced at the route layer) — this is the packet-assembly
    # side of the same rule, in case a test or a stale job body sets it
    # anyway.
    data = _with_amended(reference_case_data(), "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert not isinstance(outcome, AssembledPacket)
    assert any(
        p.source == "case" and "has not been filed" in p.message for p in outcome
    )


def test_amended_only_with_nothing_marked_amended_is_a_problem():
    outcome = assemble(
        _filed_reference_case_data(),
        as_of=TODAY,
        options=OutputOptions(amended_only=True),
    )
    assert not isinstance(outcome, AssembledPacket)
    assert any("No schedule items are marked amended" in p.message for p in outcome)


def test_a_filed_case_still_refuses_an_ordinary_reassembly():
    # amendedOnly is the ONE exception — a plain re-assembly of a filed case
    # (no options.amended_only) is unaffected.
    outcome = assemble(_filed_reference_case_data(), as_of=TODAY)
    assert not isinstance(outcome, AssembledPacket)
    assert any(
        p.source == "case" and "never re-assembled" in p.message for p in outcome
    )


def test_amended_only_renders_exactly_the_amended_schedule_plus_sum_and_dec():
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    names = [name for name, _ in outcome.parts]
    assert names[0] == "00-amendment-cover.pdf"
    assert any(name.endswith("-b106d.pdf") for name in names)
    assert any(name.endswith("-b106sum.pdf") for name in names)
    assert any(name.endswith("-b106dec.pdf") for name in names)
    # Nothing else in the schedule set carries an amended item.
    assert not any(name.endswith("-b106ab.pdf") for name in names)
    assert not any(name.endswith("-b106ef.pdf") for name in names)
    assert not any(name.endswith("-b106c.pdf") for name in names)
    assert names[-1] == MATRIX_FILE_NAME


def test_amended_only_prints_only_the_amended_item_on_its_schedule():
    # Two secured claims exist (claim-mortgage, claim-auto); only the first
    # is flagged amended, so only ITS creditor's name should print on 106D.
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    content = _part_bytes(outcome, "-b106d.pdf")
    values = " ".join(
        str(v) for v in (PdfReader(io.BytesIO(content)).get_fields() or {}).values()
    )
    assert "Gulf Coast Home Loans" in values
    assert "Drive Away Financial" not in values


def test_amended_only_ticks_the_amended_caption_on_the_amended_schedule():
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    field = _pdf_field_value(
        _part_bytes(outcome, "-b106d.pdf"), "form/b106d", "caption.amended_filing"
    )
    assert field is not None
    assert field.get("/V") not in (None, "/Off")


def test_a_plain_assembly_never_ticks_the_amended_caption():
    # The plain filing set (no amendedOnly) must leave the caption exactly
    # as blank as it has always been — the projections themselves still
    # never fill case-wide `is_amended` (form_projections/__init__.py).
    outcome = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    content = _part_bytes(outcome, "-b106ab.pdf")
    field = _pdf_field_value(content, "form/b106ab", "caption.amended_filing")
    assert field is None or field.get("/V") in (None, "/Off")


def test_amended_only_cover_sheet_lists_the_amended_forms():
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    text = (
        PdfReader(io.BytesIO(_part_bytes(outcome, "amendment-cover.pdf")))
        .pages[0]
        .extract_text()
    )
    assert "not an official" in text.lower()
    # Every form rendered after it, Summary and Declaration included.
    assert text.index("106Sum") < text.index("106D ") < text.index("106Dec")


def test_amending_schedule_ef_prints_only_the_amended_unsecured_claim():
    # Issue #370's done-when: a filed case amends Schedule E/F and produces
    # an amended-only set — the cover sheet, 106Sum, 106E/F and 106Dec.
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-hospital")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    forms = [name.split("-", 1)[1] for name, _ in outcome.parts[:-1]]
    assert forms == ["amendment-cover.pdf", "b106sum.pdf", "b106ef.pdf", "b106dec.pdf"]
    ef = _part_bytes(outcome, "-b106ef.pdf")
    values = " ".join(
        str(v) for v in (PdfReader(io.BytesIO(ef)).get_fields() or {}).values()
    )
    assert "Bayside General Hospital" in values
    assert "Meridian Bank Card Services" not in values
    caption = _pdf_field_value(ef, "form/b106ef", "caption.amended_filing")
    assert caption is not None
    assert caption.get("/V") not in (None, "/Off")


def test_an_amendment_never_opens_a_tax_id_envelope():
    # B121 is not amendable, so an amendment never prints it and never
    # performs its audited full-value read.
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    deps = build_deps(data)
    job = new_job(
        PACKET_ASSEMBLY_KIND,
        case_id=CASE_ID,
        created_by="subject-1",
        options={"amendedOnly": True},
    )
    result = run_packet_assembly(job, deps, today=TODAY)
    assert result["outcome"] == "assembled"
    assert not any(e.action == "taxid.read" for e in deps.access_log.events)


def test_amended_only_does_not_repin_the_case():
    # The CASE's own form_revisions/constants_set_id describe the ORIGINAL
    # filing; an amendment years later must not rewrite that history — only
    # the PACKET's own record does (outcome.form_revisions).
    data = _with_amended(_filed_reference_case_data(), "claims", "claim-mortgage")
    deps = build_deps(data)
    job = new_job(
        PACKET_ASSEMBLY_KIND,
        case_id=CASE_ID,
        created_by="subject-1",
        options={"amendedOnly": True},
    )
    result = run_packet_assembly(job, deps, today=TODAY)
    assert result["outcome"] == "assembled"
    stored_case = deps.case_store.cases[CASE_ID]
    assert stored_case.status == "filed"
    assert stored_case.updated_at == data.case.updated_at


def test_a_chapter_13_amendment_prints_the_schedules_not_the_plan():
    """amendedOnly stays coherent on Chapter 13 (issue #367): the changed
    schedule, B106Sum/B106Dec and the cover — never B113, whose post-filing
    change is a § 1323/§ 1329 modification, not a Rule 1009 amendment."""
    data = reference_chapter_13_case_data()
    data = replace(data, case=replace(data.case, status="filed"))
    data = _with_amended(data, "claims", "claim-mortgage")
    outcome = assemble(data, as_of=TODAY, options=OutputOptions(amended_only=True))
    assert isinstance(outcome, AssembledPacket)
    names = [name for name, _ in outcome.parts]
    assert names[0] == "00-amendment-cover.pdf"
    assert [n.split("-", 1)[1] for n in names[1:-1]] == [
        "b106sum.pdf",
        "b106d.pdf",
        "b106dec.pdf",
    ]


def test_amended_only_combined_with_forms_is_refused_at_parse_time():
    from insolvia_api.core.form_overlay import parse_output_options
    from insolvia_core.errors import FieldValidationError

    with pytest.raises(FieldValidationError):
        parse_output_options(
            {"amendedOnly": True, "forms": ["b106d"]}, allow_forms=True
        )


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
        "pay_period_records",
        "other_income_records",
        "means_test_inputs",
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
        "plans",
    ):
        for entity in getattr(data, field_name):
            entity_store.create(entity)
    # The reference tax ids, sealed the way the debtor route seals them —
    # under the case's firm and each debtor's ref — so the worker's logged
    # read (not the fixture's `tax_ids`) is what puts the number on B121.
    tax_id_store = MemoryTaxIdStore()
    tax_id_cipher = LocalTaxIdCipher()
    for debtor in data.debtors:
        digits = data.tax_ids.get(debtor.filing_role)
        if debtor.tax_id is None or digits is None:
            continue
        store_tax_id(
            TaxIdInput(kind=debtor.tax_id.kind, value=digits),
            existing=debtor.tax_id,
            firm_id=data.case.firm_id,
            case_id=data.case.id,
            cipher=tax_id_cipher,
            store=tax_id_store,
        )
    return PacketAssemblyDeps(
        case_store=case_store,
        debtor_store=debtor_store,
        entity_store=entity_store,
        packet_store=MemoryPacketStore(case_store),
        blobs=MemoryDocumentBlobStore(),
        access_log=MemoryAccessLog(),
        tax_id_store=tax_id_store,
        tax_id_cipher=tax_id_cipher,
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


def test_the_worker_performs_the_logged_read_for_b121_only():
    """B121's number reaches the packet through the audited full-value read
    (issue 13.12 / #382): one `taxid.read` row per debtor, naming the
    preparer, the debtor and the form — and the printed B121 carries the
    disclosed digits, never the fixture's."""
    deps = build_deps(reference_case_data())
    run_packet_assembly(accept_job(), deps, today=TODAY)

    reads = [e for e in deps.access_log.events if e.action == "taxid.read"]
    assert [(e.principal, e.filing_role, e.purpose) for e in reads] == [
        ("subject-1", "debtor_1", "b121"),
        ("subject-1", "debtor_2", "b121"),
    ]
    stored = deps.packet_store.list_for_case(CASE_ID)[0]
    content = deps.blobs.contents[stored.storage_ref]
    b121 = zipfile.ZipFile(io.BytesIO(content)).read("02-b121.pdf")
    fields = PdfReader(io.BytesIO(b121)).get_fields()
    assert fields is not None
    assert fields["Debtor1a.SSNum"].value == "987-65-4321"
    assert fields["Debtor2a ITINNum"].value == "87-65-4322"


def test_a_subset_without_b121_never_opens_an_envelope():
    deps = build_deps(reference_case_data())
    job = new_job(
        PACKET_ASSEMBLY_KIND,
        case_id=CASE_ID,
        created_by="subject-1",
        options={"forms": ["b101"]},
    )
    result = run_packet_assembly(job, deps, today=TODAY)
    assert result["outcome"] == "assembled"
    assert not any(e.action == "taxid.read" for e in deps.access_log.events)


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
        def create(
            self, packet, *, pinned_case, expected_updated_at, allow_filed=False
        ):
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


def test_b122a2_stays_out_of_the_packet_below_the_median():
    # B122A-1 line 14a: below the median the calculation form is not filed
    # — but its series still PINS, the J-2 rule for the same reason.
    data = reference_case_data()
    shrunk = replace(
        data,
        pay_period_records=tuple(
            replace(entity, body=replace(entity.body, gross="4500.00"))
            for entity in data.pay_period_records
        ),
    )
    series = packet_form_series(shrunk)
    assert "form/b122a1" in series
    assert "form/b122a2" not in series
    outcome = assemble(shrunk, as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    assert "form/b122a2" in outcome.form_revisions
    names = [name for name, _ in outcome.parts]
    assert any(name.endswith("b122a1.pdf") for name in names)
    assert not any(name.endswith("b122a2.pdf") for name in names)


def test_the_reference_case_files_the_means_test_pair():
    data = reference_case_data()
    series = packet_form_series(data)
    assert series[-2:] == ("form/b122a1", "form/b122a2")


def test_b108_stays_out_without_a_secured_claim_or_a_flagged_lease():
    """§ 521(a)(2) asks for the statement only when it has a row."""
    data = reference_case_data()
    unsecured_only = tuple(c for c in data.claims if c.body.claim_class != "secured")
    unflagged = tuple(
        replace(e, body=replace(e.body, list_on_statement_of_intention=None))
        for e in data.contract_leases
    )
    without = replace(
        data,
        claims=unsecured_only,
        contract_leases=unflagged,
        # The codebtor on the car loan would dangle without its claim.
        codebtors=tuple(
            c for c in data.codebtors if "claim-auto" not in c.body.claim_ids
        ),
    )
    assert "form/b108" not in packet_form_series(without)
    assert "form/b108" in packet_form_series(data)
    # A flagged lease alone is enough.
    assert "form/b108" in packet_form_series(
        replace(without, contract_leases=data.contract_leases)
    )


def test_b2030_files_only_when_an_attorney_is_on_the_case():
    data = reference_case_data()
    assert "form/b2030" in packet_form_series(data)
    pro_se = replace(data, filing_professionals=())
    assert "form/b2030" not in packet_form_series(pro_se)
    assert completeness_problems(pro_se) == ()


def test_a_secured_claim_without_an_intention_is_refused():
    data = reference_case_data()
    claims = tuple(
        replace(c, body=replace(c.body, intention=None)) if c.id == "claim-auto" else c
        for c in data.claims
    )
    problems = completeness_problems(replace(data, claims=claims))
    assert [(p.source, p.item_id, p.field) for p in problems] == [
        ("claims", "claim-auto", "intention")
    ]


def test_a_flagged_lease_without_an_answer_is_refused():
    data = reference_case_data()
    leases = tuple(
        replace(e, body=replace(e.body, intention=None)) if e.id == "cl-rav4" else e
        for e in data.contract_leases
    )
    problems = completeness_problems(replace(data, contract_leases=leases))
    assert [(p.source, p.item_id, p.field) for p in problems] == [
        ("contract_leases", "cl-rav4", "intention")
    ]


def test_an_attorney_without_the_compensation_answers_is_refused():
    data = reference_case_data()
    attorney = data.filing_professionals[0]
    blank = replace(
        attorney,
        body=replace(
            attorney.body,
            compensation_agreed=None,
            compensation_source_to_be_paid=None,
            compensation_shared=None,
        ),
    )
    problems = completeness_problems(replace(data, filing_professionals=(blank,)))
    assert [(p.source, p.field) for p in problems] == [
        ("filing_professionals", "compensation_agreed"),
        ("filing_professionals", "compensation_source_to_be_paid"),
        ("filing_professionals", "compensation_shared"),
    ]
    assert all(p.item_id == attorney.id for p in problems)


def test_a_duplicate_means_test_input_is_refused():
    data = reference_case_data()
    extra = data.means_test_inputs[0]
    doubled = replace(
        data,
        means_test_inputs=(
            data.means_test_inputs[0],
            replace(extra, id="meanstest-extra"),
        ),
    )
    problems = completeness_problems(doubled)
    assert any(p.source == "means_test_inputs" for p in problems)


def test_a_dangling_pay_period_employment_is_reported():
    data = reference_case_data()
    broken = replace(
        data,
        pay_period_records=(
            *data.pay_period_records[1:],
            replace(
                data.pay_period_records[0],
                body=replace(
                    data.pay_period_records[0].body, employment_id="employment-gone"
                ),
            ),
        ),
    )
    problems = completeness_problems(broken)
    assert any(
        p.source == "pay_period_records" and p.field == "employment_id"
        for p in problems
    )
