"""AI petition review (issue #97): the worker, the document the model is
shown, and the findings that come back — end to end against the memory
adapters and the scripted model, which is ADR 0018's local story with the
one genuinely unrunnable hop (the generation itself) faked at its port.

The reference case is the fixture here as it is for packet assembly: the one
case proven to assemble cleanly, so a review of it exercises the whole pipe.
"""

from __future__ import annotations

import re
from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.review_model import ScriptedReviewModel
from insolvia_api.core.jobs import JobError, new_job
from insolvia_api.core.packet_assembly import (
    AssembledPacket,
    assemble,
    run_packet_assembly,
)
from insolvia_api.core.petition_review import (
    PETITION_REVIEW_KIND,
    PetitionReviewDeps,
    ReviewFinding,
    finding_json,
    parse_findings,
    review_document,
    run_petition_review,
    scrub,
)
from insolvia_core.creditors import CREDITOR

from tests.test_packet_assembly import (
    CASE_ID,
    TODAY,
    _entity,
    build_deps,
    reference_case_data,
)

A_FINDING = {
    "severity": "high",
    "category": "consistency",
    "form": "form/b106i",
    "line": "4_combined_monthly_income",
    "message": "Schedule I income disagrees with the SOFA income answers.",
}


def review_deps(assembly_deps, model):
    return PetitionReviewDeps(
        case_store=assembly_deps.case_store,
        debtor_store=assembly_deps.debtor_store,
        entity_store=assembly_deps.entity_store,
        packet_store=assembly_deps.packet_store,
        access_log=assembly_deps.access_log,
        model=model,
    )


def accept_review(case_id=CASE_ID):
    return new_job(PETITION_REVIEW_KIND, case_id=case_id, created_by="subject-1")


def accept_assembly(case_id=CASE_ID):
    return new_job("packet_assembly", case_id=case_id, created_by="subject-1")


# ── The worker ──────────────────────────────────────────────────


def test_an_unconfigured_environment_fails_the_job_honestly():
    deps = review_deps(build_deps(reference_case_data()), model=None)
    with pytest.raises(JobError) as caught:
        run_petition_review(accept_review(), deps, today=TODAY)
    assert caught.value.category == "not_configured"


def test_a_vanished_case_is_a_deterministic_failure():
    deps = review_deps(build_deps(reference_case_data()), ScriptedReviewModel())
    job = accept_review(case_id="00000000-0000-4000-8000-000000000404")
    with pytest.raises(JobError) as caught:
        run_petition_review(job, deps, today=TODAY)
    assert caught.value.category == "case_not_found"


def test_a_case_the_gate_refuses_reports_the_same_problems_as_assembly():
    data = reference_case_data()
    data = replace(data, petitions=())  # the gate's simplest refusal
    deps = review_deps(build_deps(data), ScriptedReviewModel())

    result = run_petition_review(accept_review(), deps, today=TODAY)

    assert result["outcome"] == "blocked"
    assert any(p["source"] == "petitions" for p in result["problems"])


def test_review_before_any_assembly_is_blocked_not_run():
    model = ScriptedReviewModel()
    deps = review_deps(build_deps(reference_case_data()), model)

    result = run_petition_review(accept_review(), deps, today=TODAY)

    assert result["outcome"] == "blocked"
    assert "assemble" in result["problems"][0]["message"]
    assert model.documents == []  # nothing left for the model API


def test_a_case_edited_after_assembly_is_blocked_until_reassembled():
    data = reference_case_data()
    assembly_deps = build_deps(data)
    run_packet_assembly(accept_assembly(), assembly_deps, today=TODAY)
    # A new creditor lands after the packet: the deterministic re-render no
    # longer hashes to the stored packet, so the review must refuse rather
    # than describe a packet nobody holds.
    late_creditor = _entity(
        CREDITOR,
        replace(data.creditors[0].body, name="Wholly New Creditor LLC"),
        "creditor-late",
        9_500,
    )
    assembly_deps.entity_store.create(late_creditor)
    model = ScriptedReviewModel()

    result = run_petition_review(
        accept_review(), review_deps(assembly_deps, model), today=TODAY
    )

    assert result["outcome"] == "blocked"
    assert "changed" in result["problems"][0]["message"]
    assert model.documents == []


def test_a_reviewed_packet_reports_findings_against_the_stored_packet():
    assembly_deps = build_deps(reference_case_data())
    assembled = run_packet_assembly(accept_assembly(), assembly_deps, today=TODAY)
    model = ScriptedReviewModel(findings=(A_FINDING,), model="claude-test")
    deps = review_deps(assembly_deps, model)

    result = run_petition_review(accept_review(), deps, today=TODAY)

    assert result["outcome"] == "reviewed"
    report = result["report"]
    assert report["packetId"] == assembled["packet"]["id"]
    assert report["packetSha256"] == assembled["packet"]["sha256"]
    assert report["model"] == "claude-test"
    assert report["findings"] == [dict(A_FINDING)]
    # The whole-case read is access-logged against the accepting preparer.
    assert any(
        e.action == "petition.review" and e.principal == "subject-1"
        for e in deps.access_log.events
    )


def test_a_clean_review_carries_an_empty_findings_list():
    assembly_deps = build_deps(reference_case_data())
    run_packet_assembly(accept_assembly(), assembly_deps, today=TODAY)
    deps = review_deps(assembly_deps, ScriptedReviewModel())

    result = run_petition_review(accept_review(), deps, today=TODAY)

    assert result["outcome"] == "reviewed"
    assert result["report"]["findings"] == []


# ── What leaves for the model API ───────────────────────────────


def assembled_reference():
    outcome = assemble(reference_case_data(), as_of=TODAY)
    assert isinstance(outcome, AssembledPacket)
    return outcome


def test_the_document_shows_the_projected_forms_and_the_matrix():
    document = review_document(reference_case_data(), assembled_reference())
    # The projections' line keys are the citation vocabulary the prompt
    # demands; a form's series id and a 106I line key prove they made it in.
    assert '"form/b106i"' in document
    assert "4_combined_monthly_income" in document
    assert '"creditor_matrix"' in document
    assert '"confirmed_records"' in document


def test_the_document_is_deterministic():
    first = review_document(reference_case_data(), assembled_reference())
    second = review_document(reference_case_data(), assembled_reference())
    assert first == second


def test_the_document_never_carries_a_tax_id_shape():
    """Defence in depth over the stores' own refusal to hold one — nothing
    shaped like an SSN/ITIN survives the scrub, whatever field it hid in."""
    document = review_document(reference_case_data(), assembled_reference())
    assert re.search(r"\b\d{3}-\d{2}-\d{4}\b", document) is None


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        ({"tax_id": "123-45-6789", "name": "A"}, {"name": "A"}),
        ({"ssn": "x", "itin": "y"}, {}),
        (
            {"note": "SSN 123-45-6789 on file"},
            {"note": "SSN [tax id removed] on file"},
        ),
        (
            {"nested": [{"social_security_number": "z", "keep": 1}]},
            {"nested": [{"keep": 1}]},
        ),
        ({"amount": "1234.56"}, {"amount": "1234.56"}),
    ],
)
def test_scrub_removes_tax_identifiers_and_nothing_else(value, expected):
    assert scrub(value) == expected


# ── The findings boundary ───────────────────────────────────────


def test_parse_findings_validates_and_orders():
    findings = parse_findings({"findings": [A_FINDING]})
    assert findings == (
        ReviewFinding(
            severity="high",
            category="consistency",
            form="form/b106i",
            line="4_combined_monthly_income",
            message=A_FINDING["message"],
        ),
    )
    assert finding_json(findings[0]) == dict(A_FINDING)


@pytest.mark.parametrize(
    "raw",
    [
        {},
        {"findings": "not a list"},
        {"findings": ["not an object"]},
        {"findings": [{**A_FINDING, "severity": "catastrophic"}]},
        {"findings": [{**A_FINDING, "category": "vibes"}]},
        {"findings": [{**A_FINDING, "message": "  "}]},
    ],
)
def test_parse_findings_refuses_malformed_output(raw):
    with pytest.raises(ValueError, match="review model"):
        parse_findings(raw)


def test_parse_findings_caps_the_list_and_the_message():
    long_message = "x" * 2_000
    raw = {"findings": [{**A_FINDING, "message": long_message}] * 80}
    findings = parse_findings(raw)
    assert len(findings) == 50
    assert len(findings[0].message) == 500
