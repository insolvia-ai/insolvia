"""The questionnaire's section catalogue and a firm's config of it (ADR 0023
PR 3 / #362): what a save accepts, what the stored item looks like, and
which sections a client sees versus staff.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.questionnaire import (
    CATALOGUE,
    MAX_INSTRUCTIONS,
    SECTION_IDS,
    build_config,
    parse_questionnaire_update,
    portal_questionnaire_json,
    questionnaire_from_item,
    questionnaire_item,
    questionnaire_json,
    resolve,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def save(**overrides: dict[str, object]) -> dict[str, object]:
    """A whole-record save with every section on and no instructions, with
    per-section overrides by id."""
    return {
        "sections": [
            {"id": section_id, "enabled": True, **overrides.get(section_id, {})}
            for section_id in SECTION_IDS
        ]
    }


def config_from(body: dict[str, object]):
    return build_config(
        parse_questionnaire_update(body), firm_id=FIRM_ID, updated_by=ALICE
    )


# ── The catalogue ───────────────────────────────────────────────────


def test_the_catalogue_ids_are_the_contract_the_answers_will_hang_off():
    # #363 keys every question by these. Changing this tuple is changing a
    # stored contract — add a section, never rename one.
    assert SECTION_IDS == (
        "personal_information",
        "property",
        "debts",
        "income",
        "expenses",
        "other",
    )


def test_only_personal_information_is_always_on():
    assert [s.id for s in CATALOGUE if not s.switchable] == ["personal_information"]


# ── Defaults ────────────────────────────────────────────────────────


def test_a_firm_with_no_config_shows_its_clients_every_section_with_defaults():
    body = portal_questionnaire_json(None)

    assert [s["id"] for s in body["sections"]] == list(SECTION_IDS)
    assert body["sections"][1]["instructions"] == CATALOGUE[1].default_instructions


def test_a_firm_with_no_config_reads_as_default_with_null_provenance():
    body = questionnaire_json(None)

    assert (body["isDefault"], body["updatedAt"], body["updatedBy"]) == (
        True,
        None,
        None,
    )
    assert all(s["enabled"] and s["instructions"] is None for s in body["sections"])


# ── Switching off ───────────────────────────────────────────────────


def test_a_section_switched_off_is_gone_from_the_client_and_kept_for_staff():
    config = config_from(save(expenses={"enabled": False}))

    client_ids = [s["id"] for s in portal_questionnaire_json(config)["sections"]]
    staff = {s["id"]: s for s in questionnaire_json(config)["sections"]}

    assert "expenses" not in client_ids
    assert staff["expenses"]["enabled"] is False
    assert list(staff) == list(SECTION_IDS)


def test_the_client_view_carries_nothing_staff_facing():
    config = config_from(save(other={"enabled": False}))

    body = portal_questionnaire_json(config)

    assert set(body) == {"sections"}
    assert all(set(s) == {"id", "title", "instructions"} for s in body["sections"])
    assert "other" not in [s["id"] for s in body["sections"]]


def test_personal_information_cannot_be_switched_off():
    with pytest.raises(FieldValidationError) as raised:
        parse_questionnaire_update(save(personal_information={"enabled": False}))

    assert raised.value.fields == {
        "sections.personal_information.enabled": "Personal information is always on."
    }


def test_personal_information_is_on_even_if_a_stored_row_says_otherwise():
    item = questionnaire_item(config_from(save()))
    item["sections"]["personal_information"] = {"enabled": False}  # type: ignore[index]

    resolved = resolve(questionnaire_from_item(item))

    assert resolved[0].enabled is True


# ── Instructions ────────────────────────────────────────────────────


def test_the_firms_instructions_replace_the_default_for_the_client():
    config = config_from(save(debts={"instructions": "  Bring your last statement.  "}))

    debts = portal_questionnaire_json(config)["sections"][2]

    assert debts == {
        "id": "debts",
        "title": "Debts",
        "instructions": "Bring your last statement.",
    }


@pytest.mark.parametrize(
    "instructions",
    ["", "   ", None, CATALOGUE[3].default_instructions],
    ids=["empty", "blank", "null", "same-as-default"],
)
def test_instructions_that_say_nothing_new_are_stored_as_the_default(instructions):
    config = config_from(save(income={"instructions": instructions}))

    assert config.sections["income"].instructions is None
    assert "instructions" not in questionnaire_item(config)["sections"]["income"]  # type: ignore[index]


def test_overlong_instructions_are_refused_beside_their_section():
    with pytest.raises(FieldValidationError) as raised:
        parse_questionnaire_update(
            save(property={"instructions": "x" * (MAX_INSTRUCTIONS + 1)})
        )

    assert raised.value.fields == {
        "sections.property.instructions": (
            f"Must be {MAX_INSTRUCTIONS} characters or fewer."
        )
    }


# ── A save is the whole record ──────────────────────────────────────


@pytest.mark.parametrize(
    ("body", "errors"),
    [
        ({}, {"sections": "Must be a list of sections."}),
        (
            {"sections": [{"id": s, "enabled": True} for s in SECTION_IDS[:-1]]},
            {"sections.other": "Missing from the save."},
        ),
        (
            {
                "sections": [{"id": s, "enabled": True} for s in SECTION_IDS]
                + [{"id": "debts", "enabled": False}]
            },
            {"sections.debts": "Named more than once."},
        ),
        (
            {
                "sections": [{"id": s, "enabled": True} for s in SECTION_IDS]
                + [{"id": "hobbies", "enabled": True}]
            },
            {"sections.6.id": "Must be one of " + ", ".join(SECTION_IDS)},
        ),
        (
            save(debts={"enabled": "no"}),
            {"sections.debts.enabled": "Must be true or false."},
        ),
        (
            {
                "sections": [
                    {"id": s, "enabled": True} if s != "income" else {"id": s}
                    for s in SECTION_IDS
                ]
            },
            {"sections.income.enabled": "Must be true or false."},
        ),
    ],
    ids=["no-list", "missing", "duplicate", "unknown", "not-bool", "absent-flag"],
)
def test_a_save_that_is_not_one_whole_record_is_refused(body, errors):
    with pytest.raises(FieldValidationError) as raised:
        parse_questionnaire_update(body)

    assert raised.value.fields == errors


# ── The stored item ─────────────────────────────────────────────────


def test_the_item_lives_in_the_firm_partition_under_its_own_sort_key():
    item = questionnaire_item(config_from(save(other={"enabled": False})))

    assert (item["PK"], item["SK"], item["firmId"], item["updatedBy"]) == (
        f"FIRM#{FIRM_ID}",
        "QUESTIONNAIRE",
        FIRM_ID,
        ALICE,
    )
    assert "GSI1PK" not in item


def test_an_item_round_trips():
    config = config_from(
        save(other={"enabled": False}, debts={"instructions": "Statements, please."})
    )

    assert questionnaire_from_item(questionnaire_item(config)) == config


def test_a_section_added_after_the_firm_saved_takes_its_default():
    item = questionnaire_item(config_from(save(other={"enabled": False})))
    del item["sections"]["income"]  # type: ignore[attr-defined]

    resolved = {r.section.id: r for r in resolve(questionnaire_from_item(item))}

    assert resolved["income"].enabled is True
    assert resolved["other"].enabled is False


def test_a_malformed_item_raises_rather_than_reading_half():
    item = questionnaire_item(config_from(save()))
    item["sections"]["debts"] = {"instructions": "no flag"}  # type: ignore[index]

    with pytest.raises(ValidationError):
        questionnaire_from_item(item)
