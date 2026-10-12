"""The client questionnaire's questions and the answers they turn into
candidates (ADR 0023 PR 4 / #363): what an answer may carry, what it is
stored as, who may change it and when.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.candidates import (
    ACCEPTED,
    PENDING,
    CandidateOrigin,
    ProposalDraft,
    create_candidate,
)
from insolvia_core.errors import ConflictError, FieldValidationError, NotFoundError
from insolvia_core.questionnaire import (
    PROPERTY,
    SECTION_IDS,
    QuestionnaireConfig,
    SectionSetting,
    resolve,
)
from insolvia_core.questions import (
    CATALOGUE,
    MAX_PENDING_ANSWERS,
    QUESTION_IDS,
    answer_json,
    answer_ref,
    create_answer,
    own_answer_or_404,
    parse_answer,
    portal_sections_json,
    question,
    question_json,
    questions_for,
    review_question_json,
    revise_answer,
)

CASE_ID = "00000000-0000-4000-8000-0000000ca5e1"
FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
CLIENT_SUB = "00000000-0000-4000-8000-0000000c11e1"
OTHER_SUB = "00000000-0000-4000-8000-0000000c11e2"
PORTAL_APP = "portal-app-client-id"

ORIGIN = CandidateOrigin(channel="client", client_id=PORTAL_APP, subject=CLIENT_SUB)
ALL_ON = resolve(None)


def property_off() -> tuple:
    return resolve(
        QuestionnaireConfig(
            firm_id=FIRM_ID,
            sections={PROPERTY: SectionSetting(enabled=False)},
            updated_at="2099-01-01T00:00:00.000000Z",
            updated_by="00000000-0000-4000-8000-00000000a11c",
        )
    )


def answer(body: dict[str, object], *, roles=("debtor_1",), sections=ALL_ON):
    return parse_answer(body, sections=sections, roles=roles)


def stored(body: dict[str, object], *, origin=ORIGIN, existing=()):
    return create_answer(
        answer(body), case_id=CASE_ID, origin=origin, existing=tuple(existing)
    )


LEGAL_NAME = {
    "questionId": "personal_information.legal_name",
    "value": {"given": "Testy", "surname": "Example"},
}
VEHICLE = {
    "questionId": "property.vehicle",
    "value": {"year": 2099, "make": "Examplecar", "value_entire": "1200"},
}


# ── The catalogue ───────────────────────────────────────────────────


def test_every_question_hangs_off_a_section_and_is_prefixed_by_it():
    for entry in CATALOGUE:
        assert entry.section in SECTION_IDS
        assert entry.id.startswith(f"{entry.section}.")


def test_question_ids_are_unique_because_a_stored_answer_names_one():
    assert len(QUESTION_IDS) == len(set(QUESTION_IDS))


def test_every_section_has_at_least_one_question():
    for section_id in SECTION_IDS:
        assert questions_for(section_id), section_id


def test_the_tax_id_is_never_asked_through_a_candidate():
    # The digits are a sealed item (tax_ids.py); a candidate payload is not
    # a place for them.
    for entry in CATALOGUE:
        assert "tax_id" not in entry.input_keys
        assert entry.debtor_field != "tax_id"


def test_question_json_carries_what_the_portal_renders_and_no_target():
    rendered = question_json(question("property.vehicle"))
    assert rendered["id"] == "property.vehicle"
    assert rendered["repeats"] is True
    assert [i["key"] for i in rendered["inputs"]] == [
        "year",
        "make",
        "model",
        "mileage",
        "value_entire",
    ]
    # Where an answer lands is the server's business.
    assert "target" not in rendered
    assert "fixed" not in rendered


def test_the_portal_questionnaire_carries_each_enabled_sections_questions():
    config = QuestionnaireConfig(
        firm_id=FIRM_ID,
        sections={PROPERTY: SectionSetting(enabled=False)},
        updated_at="2099-01-01T00:00:00.000000Z",
        updated_by="00000000-0000-4000-8000-00000000a11c",
    )
    body = portal_sections_json(config)
    ids = [section["id"] for section in body["sections"]]
    assert PROPERTY not in ids
    every_question = [q["id"] for s in body["sections"] for q in s["questions"]]
    assert "personal_information.legal_name" in every_question
    assert not [q for q in every_question if q.startswith("property.")]


# ── Parsing an answer ───────────────────────────────────────────────


def test_a_nested_answer_becomes_the_debtor_field_it_maps_to():
    parsed = answer(LEGAL_NAME)
    assert parsed.question.target == "debtors"
    assert parsed.filing_role == "debtor_1"
    assert parsed.payload == {"name": {"given": "Testy", "surname": "Example"}}


def test_a_record_answer_carries_its_fixed_category_and_canonical_values():
    parsed = answer(VEHICLE)
    assert parsed.payload == {
        "year": 2099,
        "make": "Examplecar",
        "value_entire": "1200.00",
        "category": "vehicle",
    }


def test_a_key_the_question_does_not_ask_is_dropped_not_stored():
    parsed = answer(
        {
            "questionId": "property.vehicle",
            "value": {"make": "Examplecar", "category": "real_property", "detail": "x"},
        }
    )
    assert parsed.payload == {"make": "Examplecar", "category": "vehicle"}


def test_an_address_answer_never_carries_the_raw_fallback():
    parsed = answer(
        {
            "questionId": "personal_information.residence_address",
            "value": {
                "residence_address": {
                    "line1": "1 Example Way",
                    "city": "Testville",
                    "raw": "unparsed",
                }
            },
        }
    )
    assert parsed.payload == {
        "residence_address": {"line1": "1 Example Way", "city": "Testville"}
    }


def test_an_appended_other_name_gets_an_id_provenance_can_address():
    parsed = answer(
        {
            "questionId": "personal_information.other_names",
            "value": {"surname": "Formerly"},
        }
    )
    [element] = parsed.payload["other_names_used"]
    assert element["surname"] == "Formerly"
    assert isinstance(element["id"], str)
    assert element["id"]


@pytest.mark.parametrize(
    ("body", "key"),
    [
        (
            {"questionId": "property.vehicle", "value": {"make": "X", "year": "new"}},
            "value.year",
        ),
        (
            {"questionId": "expenses.housing", "value": {"amount": "-5"}},
            "value.amount",
        ),
        (
            {"questionId": "personal_information.legal_name", "value": {"given": "A"}},
            "value.surname",
        ),
        (
            {"questionId": "personal_information.legal_name", "value": {"given": 7}},
            "value.given",
        ),
        ({"questionId": "expenses.food", "value": {}}, "value.amount"),
        ({"questionId": "personal_information.mailing_address", "value": {}}, "value"),
        ({"questionId": "expenses.food", "value": "12"}, "value"),
        ({"questionId": "no.such_question", "value": {}}, "questionId"),
    ],
)
def test_a_malformed_answer_names_the_box_to_fix(body, key):
    with pytest.raises(FieldValidationError) as caught:
        answer(body)
    assert key in caught.value.fields


def test_a_question_in_a_switched_off_section_is_not_answerable():
    with pytest.raises(FieldValidationError) as caught:
        answer(VEHICLE, sections=property_off())
    assert "questionId" in caught.value.fields


def test_a_one_role_binding_answers_for_its_own_role_by_default():
    assert answer(LEGAL_NAME, roles=("debtor_2",)).filing_role == "debtor_2"


def test_a_two_role_binding_must_say_which_debtor():
    with pytest.raises(FieldValidationError) as caught:
        answer(LEGAL_NAME, roles=("debtor_1", "debtor_2"))
    assert "filingRole" in caught.value.fields


@pytest.mark.parametrize("role", ["debtor_2", "non_filing_spouse", "both"])
def test_a_client_cannot_answer_for_a_role_their_binding_lacks(role):
    with pytest.raises(FieldValidationError) as caught:
        answer({**LEGAL_NAME, "filingRole": role}, roles=("debtor_1",))
    assert "filingRole" in caught.value.fields


# ── Storing it as a candidate ───────────────────────────────────────


def test_an_answer_is_a_pending_candidate_with_a_question_locator():
    candidate = stored(LEGAL_NAME)
    assert candidate.status == PENDING
    assert candidate.entity_type == "debtors"
    assert candidate.origin == ORIGIN
    assert candidate.locator == {
        "kind": "question",
        "section_id": "personal_information",
        "question_id": "personal_information.legal_name",
        "filing_role": "debtor_1",
    }
    assert candidate.document_id is None


def test_a_second_live_answer_to_a_one_answer_question_is_refused():
    first = stored(LEGAL_NAME)
    with pytest.raises(ConflictError):
        stored(LEGAL_NAME, existing=[first])


def test_once_reviewed_a_one_answer_question_takes_a_new_answer():
    first = replace(stored(LEGAL_NAME), status=ACCEPTED)
    assert stored(LEGAL_NAME, existing=[first]).status == PENDING


def test_a_repeating_question_takes_many_answers():
    first = stored(VEHICLE)
    assert stored(VEHICLE, existing=[first]).id != first.id


def test_another_persons_pending_answer_does_not_block_mine():
    # A joint case: each spouse states their own facts.
    theirs = stored(
        LEGAL_NAME,
        origin=CandidateOrigin(
            channel="client", client_id=PORTAL_APP, subject=OTHER_SUB
        ),
    )
    assert stored(LEGAL_NAME, existing=[theirs]).status == PENDING


def test_the_pending_answers_a_client_may_hold_are_bounded():
    pending = [stored(VEHICLE) for _ in range(MAX_PENDING_ANSWERS)]
    with pytest.raises(ConflictError):
        stored(VEHICLE, existing=pending)


# ── Reading it back, editing and withdrawing ────────────────────────


def test_the_client_reads_back_their_own_value_and_status_only():
    candidate = replace(
        stored(VEHICLE),
        status=ACCEPTED,
        confirmed_by="00000000-0000-4000-8000-00000000a11c",
        corrected_payload={"make": "Changed"},
    )
    body = answer_json(candidate, answer_ref(candidate))
    assert body["value"] == {
        "year": 2099,
        "make": "Examplecar",
        "value_entire": "1200.00",
    }
    assert body["status"] == ACCEPTED
    assert body["questionId"] == "property.vehicle"
    for staff_only in ("confirmedBy", "correctedPayload", "resultingRecordId"):
        assert staff_only not in body


def test_a_pending_answer_is_editable_and_keeps_its_question_and_element():
    candidate = stored(
        {"questionId": "personal_information.other_names", "value": {"surname": "A"}}
    )
    ref = answer_ref(candidate)
    revised = revise_answer(candidate, ref, {"surname": "B"})
    assert revised.locator == candidate.locator
    assert (
        revised.payload["other_names_used"][0]["id"]
        == candidate.payload["other_names_used"][0]["id"]
    )
    assert revised.payload["other_names_used"][0]["surname"] == "B"


def test_a_reviewed_answer_cannot_be_edited():
    candidate = replace(stored(VEHICLE), status=ACCEPTED)
    with pytest.raises(ConflictError):
        revise_answer(candidate, answer_ref(candidate), {"make": "Other"})


def test_someone_elses_candidate_is_not_found_rather_than_forbidden():
    mcp = create_candidate(
        ProposalDraft(entity_type="assets", payload={}, external_ref=None, note=None),
        case_id=CASE_ID,
        origin=CandidateOrigin(channel="mcp", client_id="x", subject=CLIENT_SUB),
    )
    theirs = stored(
        VEHICLE,
        origin=CandidateOrigin(
            channel="client", client_id=PORTAL_APP, subject=OTHER_SUB
        ),
    )
    for candidate in (mcp, theirs, None):
        with pytest.raises(NotFoundError):
            own_answer_or_404(candidate, subject=CLIENT_SUB, sections=ALL_ON)


def test_an_answer_in_a_section_since_switched_off_is_gone_for_the_client():
    with pytest.raises(NotFoundError):
        own_answer_or_404(stored(VEHICLE), subject=CLIENT_SUB, sections=property_off())


def test_the_review_queue_labels_an_answer_with_its_section_and_question():
    assert review_question_json(answer_ref(stored(LEGAL_NAME))) == {
        "id": "personal_information.legal_name",
        "sectionId": "personal_information",
        "sectionTitle": "Personal information",
        "text": "What is your full legal name?",
        "filingRole": "debtor_1",
    }


def test_a_non_client_candidate_has_no_question():
    mcp = create_candidate(
        ProposalDraft(entity_type="assets", payload={}, external_ref=None, note=None),
        case_id=CASE_ID,
        origin=CandidateOrigin(channel="mcp", client_id="x", subject=CLIENT_SUB),
        locator={"kind": "question", "question_id": "property.vehicle"},
    )
    assert answer_ref(mcp) is None
