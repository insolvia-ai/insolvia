"""The two invariants from docs/reference/case-data-model.md, and the path
walker they stand on. These are enforcement tests: each one names a way the
rule could be evaded rather than just a way it could be used.
"""

from __future__ import annotations

import pytest
from insolvia_api.core.errors import FieldValidationError
from insolvia_api.core.provenance import (
    ProvenanceEntry,
    parse_provenance,
    populated_paths,
    provenance_json,
    require_provenance,
)

CONFIRMED = {
    "source": "ai_extracted",
    "confirmed_by": "staff-1",
    "confirmed_at": "2026-08-05T12:00:00.000000Z",
}


def typed(*paths: str) -> dict[str, ProvenanceEntry]:
    return {path: ProvenanceEntry(source="staff_typed") for path in paths}


class TestPopulatedPaths:
    def test_absent_values_are_not_populated(self) -> None:
        record = {"a": None, "b": "", "c": [], "d": {}}
        assert populated_paths(record) == []

    def test_false_and_zero_are_answers_not_absences(self) -> None:
        # The classic bug this guards: `if not value` would treat "no, I do not
        # rent my residence" as an unanswered question, and an extraction that
        # got it wrong would then need no confirmation.
        record = {"rents_residence": False, "dependents": 0}
        assert sorted(populated_paths(record)) == ["dependents", "rents_residence"]

    def test_nested_maps_produce_dotted_paths(self) -> None:
        record = {"name": {"given": "Ada", "surname": None}}
        assert populated_paths(record) == ["name.given"]

    def test_list_elements_are_addressed_by_id_not_position(self) -> None:
        record = {"other_names_used": [{"id": "n1", "surname": "Byron"}]}
        assert populated_paths(record) == ["other_names_used[n1].surname"]

    def test_reordering_a_list_does_not_move_a_path(self) -> None:
        # The whole reason embedded list elements carry an id.
        first = {"aliases": [{"id": "a", "surname": "X"}, {"id": "b", "surname": "Y"}]}
        second = {"aliases": [{"id": "b", "surname": "Y"}, {"id": "a", "surname": "X"}]}
        assert sorted(populated_paths(first)) == sorted(populated_paths(second))

    def test_a_list_without_element_ids_is_attributed_whole(self) -> None:
        # Positional paths would be a lie, so the list gets one path instead of
        # element paths that reordering would silently reattach.
        record = {"employer_ids": ["12-3456789"]}
        assert populated_paths(record) == ["employer_ids"]


class TestParseProvenance:
    def test_absent_provenance_is_an_empty_map_not_an_error(self) -> None:
        assert parse_provenance(None) == {}

    def test_rejects_an_unknown_source(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse_provenance({"name.given": {"source": "vibes"}})
        assert "provenance.name.given.source" in caught.value.fields

    def test_rejects_a_key_that_is_not_a_field_path(self) -> None:
        # A typo'd path would otherwise be stored as provenance for a field
        # nobody reads: present in the map, absent where it matters.
        with pytest.raises(FieldValidationError) as caught:
            parse_provenance({"Name..given": {"source": "staff_typed"}})
        assert "provenance.Name..given" in caught.value.fields

    def test_accepts_a_bracketed_element_path(self) -> None:
        entries = parse_provenance(
            {"other_names_used[n1].surname": {"source": "staff_typed"}}
        )
        assert "other_names_used[n1].surname" in entries

    def test_confidence_must_be_a_number_in_range(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_provenance({"a": {**CONFIRMED, "confidence": 1.5}})
        entries = parse_provenance({"a": {**CONFIRMED, "confidence": 0.9}})
        assert entries["a"].confidence == 0.9


class TestConfirmBeforeEntry:
    """INVARIANT 2 — machine-supplied values need a human before they can be
    stored. Each test is a way of trying to get around it."""

    @pytest.mark.parametrize("source", ["ai_extracted", "imported"])
    def test_unconfirmed_machine_value_is_rejected(self, source: str) -> None:
        with pytest.raises(FieldValidationError) as caught:
            parse_provenance({"name.given": {"source": source}})
        assert "provenance.name.given" in caught.value.fields

    def test_imported_is_not_a_way_around_it(self) -> None:
        # The data model is explicit: the source system does not change who is
        # signing the form, so `imported` carries the same requirement.
        with pytest.raises(FieldValidationError):
            parse_provenance({"a": {"source": "imported", "confidence": 1.0}})

    def test_a_timestamp_with_nobody_attached_is_not_a_confirmation(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_provenance(
                {
                    "a": {
                        "source": "ai_extracted",
                        "confirmed_at": "2026-08-05T12:00:00.000000Z",
                    }
                }
            )

    def test_a_person_with_no_moment_is_not_a_confirmation_either(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_provenance(
                {"a": {"source": "ai_extracted", "confirmed_by": "staff-1"}}
            )

    def test_a_confirmed_machine_value_is_accepted(self) -> None:
        entries = parse_provenance({"name.given": CONFIRMED})
        assert entries["name.given"].confirmed_by == "staff-1"

    def test_staff_typed_needs_no_confirmation(self) -> None:
        # A person typing it IS the confirmation; demanding a second one would
        # make every ordinary intake keystroke fail.
        assert (
            parse_provenance({"a": {"source": "staff_typed"}})["a"].confirmed_at is None
        )


class TestRequireProvenance:
    """INVARIANT 1 — and specifically that it closes the hole invariant 2 would
    otherwise have."""

    def test_a_value_without_provenance_is_rejected(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            require_provenance({"name": {"given": "Ada"}}, {})
        assert "provenance.name.given" in caught.value.fields

    def test_omitting_the_key_is_not_a_way_to_smuggle_a_value_in(self) -> None:
        # Without invariant 1, anything unconfirmed could be stored simply by
        # sending no provenance for it. This is that loophole, closed.
        with pytest.raises(FieldValidationError):
            require_provenance({"name": {"given": "extracted, unconfirmed"}}, {})

    def test_an_empty_record_needs_no_provenance(self) -> None:
        # Progressive intake: a half-finished questionnaire must persist.
        require_provenance({"name": {"given": None}, "phone": ""}, {})

    def test_server_owned_identity_is_exempt(self) -> None:
        require_provenance(
            {"id": "c1", "case_id": "c1", "name": {"given": "Ada"}}, typed("name.given")
        )

    def test_provenance_for_a_field_that_is_not_there_does_not_satisfy_it(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            require_provenance({"name": {"given": "Ada"}}, typed("name.surname"))
        assert "provenance.name.given" in caught.value.fields

    def test_a_fully_covered_record_passes(self) -> None:
        record = {
            "name": {"given": "Ada", "surname": "Lovelace"},
            "rents_residence": False,
        }
        require_provenance(
            record, typed("name.given", "name.surname", "rents_residence")
        )


class TestProvenanceJson:
    def test_drops_null_members_rather_than_storing_them(self) -> None:
        # This map rides on every record; a staff_typed entry is mostly empty.
        assert provenance_json(typed("a")) == {"a": {"source": "staff_typed"}}

    def test_keeps_every_member_that_has_a_value(self) -> None:
        entries = parse_provenance(
            {"a": {**CONFIRMED, "confidence": 0.5, "document_id": "d1"}}
        )
        assert provenance_json(entries)["a"] == {
            "source": "ai_extracted",
            "confirmed_by": "staff-1",
            "confirmed_at": "2026-08-05T12:00:00.000000Z",
            "document_id": "d1",
            "confidence": 0.5,
        }

    def test_round_trips_through_parse(self) -> None:
        entries = parse_provenance({"a": CONFIRMED})
        assert parse_provenance(provenance_json(entries)) == entries
