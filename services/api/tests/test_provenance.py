"""The two invariants from docs/reference/case-data-model.md, and the path
walker they stand on. These are enforcement tests: each one names a way the
rule could be evaded rather than just a way it could be used.
"""

from __future__ import annotations

import pytest
from insolvia_api.core.provenance import (
    ProvenanceEntry,
    parse_provenance,
    populated_paths,
    provenance_json,
    require_provenance,
)
from insolvia_core.errors import FieldValidationError

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


class TestEvasionsFoundInReview:
    """Each of these passed before the fix beside it. They are grouped so the
    next reader can see what an adversarial pass actually found, rather than
    reading six unrelated regression tests."""

    def test_a_blank_confirmer_is_nobody(self) -> None:
        # `confirmed_by: ""` satisfied "is not None" and so satisfied
        # invariant 2 — a confirmation with nobody attached, which is the exact
        # thing the rule exists to prevent.
        for blank in ("", "   "):
            with pytest.raises(FieldValidationError):
                parse_provenance({"a": {**CONFIRMED, "confirmed_by": blank}})

    def test_a_timestamp_must_be_a_real_instant(self) -> None:
        # The old check was a regex of the right SHAPE, which "0000-00-00" and
        # "9999-99-99" both match.
        for fake in ("0000-00-00T........Z", "9999-99-99T99:99:99Z", "2026-08-05T.Z"):
            with pytest.raises(FieldValidationError):
                parse_provenance({"a": {**CONFIRMED, "confirmed_at": fake}})

    def test_case_data_cannot_hide_under_a_server_owned_name(self) -> None:
        # The exemption matched the FIRST segment, so an object parked under
        # `created_at` was exempt in its entirety.
        for record in (
            {"created_at": {"ssn": "smuggled"}},
            {"id": {"nested": {"ssn": "smuggled"}}},
        ):
            with pytest.raises(FieldValidationError):
                require_provenance(record, {})

    def test_the_server_owned_scalars_are_still_exempt(self) -> None:
        require_provenance({"id": "c1", "case_id": "c1", "created_at": "t"}, {})

    def test_a_key_that_is_not_a_field_name_fails_loudly(self) -> None:
        # Previously these produced a REQUIRED path that parse_provenance then
        # refused as a key — no payload could satisfy both, and the caller was
        # stuck with a 400 they could not fix.
        for key in ("SSN", "legalName", "1099_income", "case-number", ""):
            with pytest.raises(FieldValidationError):
                require_provenance({key: "value"}, {})

    def test_a_value_under_the_empty_key_is_not_invisible(self) -> None:
        # It produced no path at all, so no provenance was ever required.
        with pytest.raises(FieldValidationError):
            require_provenance({"": "smuggled"}, {})

    def test_a_literal_dotted_key_cannot_collide_with_a_nested_path(self) -> None:
        # `{"name.given": x}` and `{"name": {"given": y}}` both produced
        # `name.given`, so one entry covered two different values.
        with pytest.raises(FieldValidationError):
            require_provenance({"name.given": "forged"}, typed("name.given"))

    def test_a_mixed_list_does_not_discard_the_paths_before_the_bad_element(
        self,
    ) -> None:
        # Returning mid-loop threw away earlier elements' paths, so one entry
        # for the list covered them all — and which behaviour you got depended
        # on the ORDER of the elements.
        good_first = {"aliases": [{"id": "n1", "surname": "Byron"}, {"surname": "X"}]}
        bad_first = {"aliases": [{"surname": "X"}, {"id": "n1", "surname": "Byron"}]}
        assert populated_paths(good_first) == populated_paths(bad_first) == ["aliases"]
        # And the whole list still needs an entry — it is not exempt.
        with pytest.raises(FieldValidationError):
            require_provenance(good_first, {})

    def test_an_id_that_cannot_be_addressed_does_not_mint_an_illegal_path(self) -> None:
        # A client-supplied id containing '.' or ']' used to produce a path
        # parse_provenance refused. Now such a list is attributed whole, and
        # every path this emits is one parse_provenance accepts.
        record = {"aliases": [{"id": "n.1", "surname": "Byron"}]}
        assert populated_paths(record) == ["aliases"]
        parse_provenance(dict.fromkeys(populated_paths(record), TYPED_SOURCE))

    def test_every_emitted_path_is_one_parse_provenance_accepts(self) -> None:
        # The property the two halves have to share, stated directly.
        record = {
            "name": {"given": "Ada"},
            "aliases": [{"id": "n1", "surname": "Byron"}],
            "rents_residence": False,
        }
        paths = populated_paths(record)
        entries = parse_provenance(dict.fromkeys(paths, TYPED_SOURCE))
        assert entries.keys() == set(paths)


TYPED_SOURCE = {"source": "staff_typed"}


class TestLocator:
    """Accepted as any mapping at all until a review pointed out where those
    values go: straight into DynamoDB, and back out in a response body."""

    def test_a_well_formed_locator_is_accepted(self) -> None:
        entries = parse_provenance(
            {
                "a": {
                    **CONFIRMED,
                    "locator": {
                        "document_id": "d1",
                        "page": 3,
                        "region": {"x": 0.1, "y": 0.2, "width": 0.5, "height": 0.05},
                    },
                }
            }
        )
        assert entries["a"].locator is not None

    def test_a_non_finite_number_is_refused(self) -> None:
        # stdlib JSON accepts `Infinity` and `NaN`, so these arrive as real
        # floats. DynamoDB refuses {"N": "inf"}, and a response echoing
        # `Infinity` is not JSON any strict parser will read.
        for bad in (float("inf"), float("nan")):
            with pytest.raises(FieldValidationError):
                parse_provenance(
                    {"a": {**CONFIRMED, "locator": {"region": {"x": bad}}}}
                )

    def test_a_region_outside_the_page_is_refused(self) -> None:
        # Fractions of the page box, origin top-left — not points.
        with pytest.raises(FieldValidationError):
            parse_provenance({"a": {**CONFIRMED, "locator": {"region": {"x": 1.5}}}})

    def test_pages_are_one_based(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_provenance({"a": {**CONFIRMED, "locator": {"page": 0}}})

    def test_a_non_finite_confidence_is_refused(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_provenance({"a": {**CONFIRMED, "confidence": float("nan")}})
