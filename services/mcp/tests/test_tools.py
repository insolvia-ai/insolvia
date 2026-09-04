"""The tool layer over the in-memory ports — the pyramid's base for the MCP
surface. What the protocol seam (test_protocol.py) pins as wire behaviour is
verified here as domain behaviour: gates, anti-oracle answers, pagination,
and the candidate flow, all without JSON-RPC in the way."""

from __future__ import annotations

import pytest
from insolvia_core.case_entities import create_entity, parse_entity
from insolvia_core.cases import create_case, parse_case_creation
from insolvia_core.creditors import CREDITOR
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    ForbiddenError,
    NotFoundError,
    ValidationError,
)
from insolvia_core.firms import ADD_EDIT, VIEW_ONLY
from insolvia_mcp.core.tools import ENTITY_TYPES, whoami

from .conftest import COLLEAGUE, FIRM_ID, OTHER_FIRM_ID, SUBJECT, make_accessor

ADMIN = make_accessor(is_admin=True)
VIEWER = make_accessor(
    is_admin=False,
    access_all_cases=True,
    permissions={"cases": VIEW_ONLY, "intake": VIEW_ONLY, "documents": VIEW_ONLY},
)
STRIPPED = make_accessor(is_admin=False, access_all_cases=True, permissions={})
OTHER_FIRM = make_accessor(is_admin=True, firm_id=OTHER_FIRM_ID)


def _case(tools, accessor=ADMIN):
    case, assignment = create_case(
        parse_case_creation({"chapter": 7, "district": "Middle District of Florida"}),
        firm_id=accessor.firm_id,
        created_by=accessor.subject,
    )
    tools.case_store.create(case, assignment)
    return case


def _creditor(tools, case_id: str, name: str = "Example Bank"):
    draft = parse_entity(
        CREDITOR, {"name": name, "provenance": {"name": {"source": "staff_typed"}}}
    )
    entity = create_entity(CREDITOR, draft, case_id=case_id)
    tools.case_entity_store.create(entity)
    return entity


def _proposal(name: str = "First Example Bank") -> dict[str, object]:
    return {"entityType": "creditors", "payload": {"name": name}}


# ── whoami ──────────────────────────────────────────────────────────


def test_whoami_reports_the_firm_and_permissions() -> None:
    result = whoami(VIEWER)
    assert result["firm"] == {"id": FIRM_ID, "name": "Example Firm"}
    assert result["displayName"] == "Dev User"
    assert result["permissions"]["cases"] == "view_only"
    assert result["permissions"]["extraction_review"] == "hidden"


def test_whoami_reports_the_absence_of_a_firm() -> None:
    result = whoami(None)
    assert result["firm"] is None
    assert result["permissions"] == {}


# ── listing and reading cases ───────────────────────────────────────


def test_list_cases_answers_the_callers_cases_newest_first(tools) -> None:
    first = _case(tools)
    second = _case(tools)
    result = tools.list_cases(ADMIN)
    assert [case["id"] for case in result["cases"]] == [second.id, first.id]
    assert "nextCursor" not in result


def test_list_cases_is_firm_scoped(tools) -> None:
    _case(tools)
    assert tools.list_cases(OTHER_FIRM)["cases"] == []


def test_list_cases_filters_by_status_within_the_page(tools) -> None:
    _case(tools)
    result = tools.list_cases(ADMIN, status="filed")
    assert result["cases"] == []


@pytest.mark.parametrize(
    ("kwargs", "message_part"),
    [
        ({"status": "open"}, "status"),
        ({"limit": 0}, "limit"),
        ({"limit": 101}, "limit"),
        ({"limit": True}, "limit"),
        ({"cursor": 7}, "cursor"),
    ],
)
def test_list_cases_rejects_bad_arguments(tools, kwargs, message_part) -> None:
    _case(tools)
    with pytest.raises(ValidationError, match=message_part):
        tools.list_cases(ADMIN, **kwargs)


def test_list_cases_requires_the_cases_feature(tools) -> None:
    with pytest.raises(ForbiddenError):
        tools.list_cases(STRIPPED)


def test_get_case_answers_the_case_and_its_record_counts(tools) -> None:
    case = _case(tools)
    _creditor(tools, case.id)
    result = tools.get_case(ADMIN, case_id=case.id)
    assert result["case"]["id"] == case.id
    assert result["recordCounts"]["creditors"] == 1
    assert set(result["recordCounts"]) == set(ENTITY_TYPES)


def test_another_firms_case_is_not_found_not_forbidden(tools) -> None:
    # The anti-oracle rule: indistinguishable from a case that does not exist.
    case = _case(tools)
    with pytest.raises(NotFoundError):
        tools.get_case(OTHER_FIRM, case_id=case.id)


def test_case_reads_are_recorded_either_way(tools, stores) -> None:
    case = _case(tools)
    tools.get_case(ADMIN, case_id=case.id)
    with pytest.raises(NotFoundError):
        tools.get_case(OTHER_FIRM, case_id=case.id)
    outcomes = [(event.action, event.outcome) for event in stores["access_log"].events]
    assert ("case.read", "allowed") in outcomes
    assert ("case.read", "denied") in outcomes


# ── the generic record tools ────────────────────────────────────────


def test_list_case_records_serializes_the_apis_wire_shape(tools) -> None:
    case = _case(tools)
    entity = _creditor(tools, case.id)
    result = tools.list_case_records(ADMIN, case_id=case.id, entity_type="creditors")
    (record,) = result["records"]
    assert record["id"] == entity.id
    assert record["name"] == "Example Bank"
    assert "provenance" in record


def test_list_case_records_paginates_with_bound_cursors(tools) -> None:
    case = _case(tools)
    for index in range(3):
        _creditor(tools, case.id, name=f"Creditor {index}")
    first = tools.list_case_records(
        ADMIN, case_id=case.id, entity_type="creditors", limit=2
    )
    assert len(first["records"]) == 2
    second = tools.list_case_records(
        ADMIN,
        case_id=case.id,
        entity_type="creditors",
        limit=2,
        cursor=first["nextCursor"],
    )
    assert len(second["records"]) == 1
    assert "nextCursor" not in second


def test_a_cursor_replayed_against_another_listing_is_rejected(tools) -> None:
    case = _case(tools)
    other = _case(tools)
    for index in range(3):
        _creditor(tools, case.id, name=f"Creditor {index}")
    page = tools.list_case_records(
        ADMIN, case_id=case.id, entity_type="creditors", limit=2
    )
    with pytest.raises(ValidationError):
        tools.list_case_records(
            ADMIN, case_id=other.id, entity_type="creditors", cursor=page["nextCursor"]
        )


def test_an_unknown_entity_type_is_a_validation_failure(tools) -> None:
    case = _case(tools)
    with pytest.raises(ValidationError, match="entityType"):
        tools.list_case_records(ADMIN, case_id=case.id, entity_type="wallets")


@pytest.mark.parametrize("entity_type", ["creditors", "debtors"])
def test_record_reads_gate_on_intake(tools, entity_type) -> None:
    case = _case(tools)
    with pytest.raises(ForbiddenError):
        tools.list_case_records(STRIPPED, case_id=case.id, entity_type=entity_type)


def test_document_reads_gate_on_documents(tools) -> None:
    case = _case(tools)
    intake_only = make_accessor(
        is_admin=False,
        access_all_cases=True,
        permissions={"cases": VIEW_ONLY, "intake": ADD_EDIT},
    )
    with pytest.raises(ForbiddenError):
        tools.list_case_records(intake_only, case_id=case.id, entity_type="documents")


def test_get_case_record_answers_one_record(tools) -> None:
    case = _case(tools)
    entity = _creditor(tools, case.id)
    result = tools.get_case_record(
        ADMIN, case_id=case.id, entity_type="creditors", record_id=entity.id
    )
    assert result["record"]["id"] == entity.id


def test_a_record_id_from_another_case_is_not_found(tools) -> None:
    case = _case(tools)
    other = _case(tools)
    entity = _creditor(tools, case.id)
    with pytest.raises(NotFoundError):
        tools.get_case_record(
            ADMIN, case_id=other.id, entity_type="creditors", record_id=entity.id
        )


# ── the candidate flow ──────────────────────────────────────────────


def test_propose_stores_pending_candidates_with_the_token_origin(tools) -> None:
    case = _case(tools)
    result = tools.propose_case_records(
        ADMIN,
        case_id=case.id,
        proposals=[_proposal(), _proposal("Second Bank")],
        client_id="mcp-client-1",
    )
    assert [row["status"] for row in result["candidates"]] == ["pending", "pending"]
    stored = tools.candidate_store.list_for_case(case.id)
    assert {candidate.origin.client_id for candidate in stored} == {"mcp-client-1"}
    assert {candidate.origin.subject for candidate in stored} == {SUBJECT}


def test_propose_requires_intake_add_edit(tools) -> None:
    case = _case(tools)
    with pytest.raises(ForbiddenError):
        tools.propose_case_records(
            VIEWER, case_id=case.id, proposals=[_proposal()], client_id="c"
        )


def test_a_malformed_batch_stores_nothing(tools) -> None:
    case = _case(tools)
    with pytest.raises(FieldValidationError):
        tools.propose_case_records(
            ADMIN,
            case_id=case.id,
            proposals=[_proposal(), {"entityType": "nope"}],
            client_id="c",
        )
    assert tools.candidate_store.list_for_case(case.id) == ()


def test_propose_against_another_firms_case_is_not_found(tools) -> None:
    case = _case(tools)
    with pytest.raises(NotFoundError):
        tools.propose_case_records(
            OTHER_FIRM, case_id=case.id, proposals=[_proposal()], client_id="c"
        )


def test_check_proposals_reports_status_and_filters(tools) -> None:
    case = _case(tools)
    proposed = tools.propose_case_records(
        ADMIN, case_id=case.id, proposals=[_proposal()], client_id="c"
    )
    candidate_id = proposed["candidates"][0]["candidateId"]
    result = tools.check_proposals(ADMIN, case_id=case.id)
    assert result["candidates"][0]["candidateId"] == candidate_id
    assert (
        tools.check_proposals(ADMIN, case_id=case.id, status="accepted")["candidates"]
        == []
    )
    assert (
        tools.check_proposals(ADMIN, case_id=case.id, candidate_ids=[candidate_id])[
            "candidates"
        ][0]["candidateId"]
        == candidate_id
    )


def test_withdraw_is_the_proposers_own(tools) -> None:
    case = _case(tools)
    proposed = tools.propose_case_records(
        ADMIN, case_id=case.id, proposals=[_proposal()], client_id="c"
    )
    candidate_id = proposed["candidates"][0]["candidateId"]

    colleague = make_accessor(is_admin=True, subject=COLLEAGUE)
    with pytest.raises(ForbiddenError):
        tools.withdraw_proposal(colleague, case_id=case.id, candidate_id=candidate_id)

    result = tools.withdraw_proposal(ADMIN, case_id=case.id, candidate_id=candidate_id)
    assert result["status"] == "withdrawn"
    # Withdrawn is terminal: a second withdrawal conflicts rather than
    # pretending to succeed.
    with pytest.raises(ConflictError):
        tools.withdraw_proposal(ADMIN, case_id=case.id, candidate_id=candidate_id)


def test_withdrawing_an_unknown_candidate_is_not_found(tools) -> None:
    case = _case(tools)
    with pytest.raises(NotFoundError):
        tools.withdraw_proposal(ADMIN, case_id=case.id, candidate_id="missing")
