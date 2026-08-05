"""The debtor parser: B101 Part 1, progressive, and provenance-enforced.

The bias of this file is that every field is optional and nothing is required,
because docs/reference/case-data-model.md says storage validates shape and type
only. So most tests here are about what is ACCEPTED — a half-empty record must
save — and the rest are about the two things that are not negotiable: the
provenance invariants, and tax_id.
"""

from __future__ import annotations

import pytest
from insolvia_api.core.debtors import (
    FILING_ROLES,
    Debtor,
    create_debtor,
    debtor_from_item,
    debtor_item,
    debtor_json,
    parse_debtor,
    parse_filing_role,
    replace_debtor,
    role_order,
    sort_key,
)
from insolvia_api.core.errors import FieldValidationError
from insolvia_api.core.provenance import ADDRESSABLE_ID_RE

TYPED = {"source": "staff_typed"}


def draft(**body: object):
    return parse_debtor(body)


class TestProgressiveIntake:
    def test_an_entirely_empty_body_is_valid(self) -> None:
        # The one thing the questionnaire must never fail at is saving a
        # half-finished intake — and "half" includes "not started".
        result = draft()
        assert result.name.given is None
        assert result.other_names_used == ()

    def test_one_field_with_its_provenance_is_valid(self) -> None:
        result = draft(name={"given": "Ada"}, provenance={"name.given": TYPED})
        assert result.name.given == "Ada"

    def test_whitespace_only_text_collapses_to_absent(self) -> None:
        # "Cleared the box" and "never filled it in" are the same state on a
        # form. Keeping them apart would mean provenance for a deletion.
        result = draft(name={"given": "   "})
        assert result.name.given is None

    def test_a_cleared_field_therefore_needs_no_provenance(self) -> None:
        draft(name={"given": ""}, phone="  ")


class TestShapeValidation:
    def test_rejects_a_non_string_where_text_belongs(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(name={"given": 7})
        assert "name.given" in caught.value.fields

    def test_rejects_a_multi_line_value(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(name={"surname": "Love\nlace"})
        assert "name.surname" in caught.value.fields

    def test_rejects_an_over_long_value(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(email="a" * 201)
        assert "email" in caught.value.fields

    def test_rejects_an_unknown_venue_basis(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(venue={"basis": "felt_like_it"})
        assert "venue.basis" in caught.value.fields

    def test_accepts_the_four_credit_counseling_statuses(self) -> None:
        for status in (
            "completed_with_certificate",
            "completed_certificate_pending",
            "exigent_circumstances_waiver_requested",
            "not_required",
        ):
            result = draft(
                credit_counseling={"status": status},
                provenance={"credit_counseling.status": TYPED},
            )
            assert result.credit_counseling.status == status

    def test_reports_every_bad_field_at_once(self) -> None:
        # One round trip per mistake would be a miserable form to fill in.
        with pytest.raises(FieldValidationError) as caught:
            draft(name={"given": 1, "surname": 2}, email=3)
        assert set(caught.value.fields) >= {"name.given", "name.surname", "email"}


class TestOtherNames:
    def test_keeps_the_caller_supplied_id(self) -> None:
        # The client writes the alias and its provenance in one request, so it
        # has to be able to name the row it is describing.
        result = draft(
            other_names_used=[{"id": "n1", "surname": "Byron"}],
            provenance={"other_names_used[n1].surname": TYPED},
        )
        assert result.other_names_used[0].id == "n1"

    def test_an_alias_without_an_id_is_refused(self) -> None:
        # Minting one server-side is the trap, not the kindness it looks like:
        # the alias's fields then need provenance at
        # `other_names_used[<fresh-uuid>].…`, a path the caller cannot have
        # sent. It 400s — and because a new uuid is minted on every attempt,
        # echoing back the path the error just named 400s again with a
        # different uuid. That loop made the 8-year alias list unusable.
        with pytest.raises(FieldValidationError) as caught:
            draft(other_names_used=[{"surname": "Smith"}])
        assert "other_names_used[0].id" in caught.value.fields

    def test_the_refusal_is_stable_across_attempts(self) -> None:
        # The point of the fix: the same request twice gets the SAME error, so
        # a client can act on it. The old behaviour named a different uuid each
        # time, which is what made the loop unescapable.
        def fields() -> dict[str, str]:
            with pytest.raises(FieldValidationError) as caught:
                draft(other_names_used=[{"surname": "Smith"}])
            return caught.value.fields

        assert fields() == fields()

    def test_rejects_duplicate_ids(self) -> None:
        # Two rows with one id would make a provenance path ambiguous.
        with pytest.raises(FieldValidationError) as caught:
            draft(other_names_used=[{"id": "n1"}, {"id": "n1"}])
        assert "other_names_used[1].id" in caught.value.fields

    def test_rejects_a_string_where_a_list_belongs(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(other_names_used="Byron")
        assert "other_names_used" in caught.value.fields


class TestTaxId:
    def test_is_refused_rather_than_ignored(self) -> None:
        # Silently dropping it is the dangerous option: the client would
        # believe a tax id had been stored when the field simply is not there.
        with pytest.raises(FieldValidationError) as caught:
            draft(tax_id={"kind": "ssn", "value": "000-00-0000"})
        assert "tax_id" in caught.value.fields
        assert "encryption" in caught.value.fields["tax_id"]

    def test_an_explicit_null_is_not_an_error(self) -> None:
        draft(tax_id=None)


class TestProvenanceEnforcement:
    def test_a_value_without_provenance_is_rejected(self) -> None:
        with pytest.raises(FieldValidationError) as caught:
            draft(name={"given": "Ada"})
        assert "provenance.name.given" in caught.value.fields

    def test_an_unconfirmed_extraction_cannot_be_saved(self) -> None:
        with pytest.raises(FieldValidationError):
            draft(
                name={"given": "Ada"},
                provenance={"name.given": {"source": "ai_extracted"}},
            )

    def test_a_confirmed_extraction_can(self) -> None:
        result = draft(
            name={"given": "Ada"},
            provenance={
                "name.given": {
                    "source": "ai_extracted",
                    "confirmed_by": "staff-1",
                    "confirmed_at": "2026-08-05T12:00:00.000000Z",
                }
            },
        )
        assert result.provenance["name.given"].confirmed_by == "staff-1"

    def test_provenance_is_checked_against_the_stored_shape_not_the_raw_body(
        self,
    ) -> None:
        # `   ` collapses to absent, so demanding provenance for it would be
        # demanding provenance for a field that is about to vanish.
        draft(name={"given": "   "}, provenance={})

    def test_a_bad_field_is_reported_before_bad_provenance(self) -> None:
        # The fixable thing first: a caller who sent both wants to see the
        # field error, not a provenance error about a field they must re-send.
        with pytest.raises(FieldValidationError) as caught:
            draft(name={"given": 7}, provenance={"!!": TYPED})
        assert "name.given" in caught.value.fields
        assert not any(key.startswith("provenance") for key in caught.value.fields)


class TestFilingRole:
    def test_accepts_the_three_roles(self) -> None:
        for role in ("debtor_1", "debtor_2", "non_filing_spouse"):
            assert parse_filing_role(role) == role

    def test_rejects_anything_else(self) -> None:
        with pytest.raises(FieldValidationError):
            parse_filing_role("debtor_3")

    def test_non_filing_spouse_is_a_role_not_a_flag(self) -> None:
        # 106I's second column may belong to a spouse who is not filing at all.
        assert parse_filing_role("non_filing_spouse") == "non_filing_spouse"


class TestIdentityAndReplacement:
    def test_create_stamps_server_owned_identity(self) -> None:
        debtor = create_debtor(draft(), case_id="c1", filing_role="debtor_1")
        assert debtor.case_id == "c1"
        assert debtor.filing_role == "debtor_1"
        assert debtor.id
        assert debtor.created_at == debtor.updated_at

    def test_replacing_keeps_the_id_and_created_at(self) -> None:
        # Provenance paths on other records may already name this id.
        first = create_debtor(draft(), case_id="c1", filing_role="debtor_1")
        second = replace_debtor(
            first, draft(name={"given": "Ada"}, provenance={"name.given": TYPED})
        )
        assert second.id == first.id
        assert second.created_at == first.created_at
        assert second.name.given == "Ada"

    def test_replacing_drops_a_field_the_new_body_omits(self) -> None:
        # PUT semantics: the body IS the record. A field left out is cleared,
        # not preserved — which is what makes "delete this alias" work.
        first = replace_debtor(
            create_debtor(draft(), case_id="c1", filing_role="debtor_1"),
            draft(name={"given": "Ada"}, provenance={"name.given": TYPED}),
        )
        second = replace_debtor(first, draft())
        assert second.name.given is None


class TestSerialisation:
    def test_json_omits_absent_values(self) -> None:
        debtor = create_debtor(draft(), case_id="c1", filing_role="debtor_1")
        body = debtor_json(debtor)
        assert "phone" not in body
        assert body["filing_role"] == "debtor_1"

    def test_json_keeps_the_values_that_are_there(self) -> None:
        debtor = create_debtor(
            draft(name={"given": "Ada"}, provenance={"name.given": TYPED}),
            case_id="c1",
            filing_role="debtor_1",
        )
        assert debtor_json(debtor)["name"] == {"given": "Ada"}
        assert debtor_json(debtor)["provenance"] == {
            "name.given": {"source": "staff_typed"}
        }

    def test_the_item_keys_put_a_debtor_in_its_case_partition(self) -> None:
        debtor = create_debtor(draft(), case_id="c1", filing_role="debtor_2")
        item = debtor_item(debtor)
        assert item["PK"] == "CASE#c1"
        assert item["SK"] == sort_key("debtor_2") == "DEBTOR#debtor_2"

    def test_an_item_round_trips(self) -> None:
        debtor = create_debtor(
            draft(
                name={"given": "Ada", "surname": "Lovelace"},
                other_names_used=[{"id": "n1", "surname": "Byron"}],
                residence_address={"city": "London"},
                venue={"basis": "lived_longest_180_days"},
                provenance={
                    "name.given": TYPED,
                    "name.surname": TYPED,
                    "other_names_used[n1].surname": TYPED,
                    "residence_address.city": TYPED,
                    "venue.basis": TYPED,
                },
            ),
            case_id="c1",
            filing_role="debtor_1",
        )
        restored = debtor_from_item(debtor_item(debtor))
        assert restored == debtor

    def test_a_stored_item_is_re_parsed_rather_than_trusted(self) -> None:
        # An item written by an older revision is exactly where a field has
        # since changed shape; failing here beats a None three layers up.
        debtor = create_debtor(draft(), case_id="c1", filing_role="debtor_1")
        item = dict(debtor_item(debtor))
        item["body"] = {"name": {"given": 7}}
        with pytest.raises(FieldValidationError):
            debtor_from_item(item)


class TestDebtorDefaults:
    def test_a_bare_debtor_needs_only_identity(self) -> None:
        # The dataclass defaults are what let `create_debtor` accept a draft
        # with nothing in it; if they regressed to required, progressive
        # intake would break at the type level.
        debtor = Debtor(
            id="d1",
            case_id="c1",
            filing_role="debtor_1",
            created_at="t",
            updated_at="t",
        )
        assert debtor.other_names_used == ()
        assert debtor.name.given is None


class TestAliasIdsStayAddressable:
    """Found by an adversarial review of the provenance layer: a client-supplied
    id containing '.' or ']' minted a provenance path the parser then refused,
    leaving no payload that could satisfy both halves of the rule."""

    def test_an_unaddressable_id_is_refused_with_a_reason(self) -> None:
        for bad in ("n.1", "n]1", "n 1", "urn:x:1"):
            with pytest.raises(FieldValidationError) as caught:
                draft(other_names_used=[{"id": bad, "surname": "Byron"}])
            assert "other_names_used[0].id" in caught.value.fields

    def test_a_uuid_is_fine(self) -> None:
        result = draft(
            other_names_used=[{"id": "3f9c1a2b-0000-4000-8000-000000000000"}],
        )
        assert result.other_names_used[0].id.startswith("3f9c")

    def test_an_accepted_id_is_always_addressable(self) -> None:
        # The property that matters: every id that survives can appear in a
        # provenance path this service will accept.
        result = draft(other_names_used=[{"id": "n1"}, {"id": "3f9c-1a2b"}])
        assert all(
            ADDRESSABLE_ID_RE.match(alias.id) for alias in result.other_names_used
        )


class TestRoleOrdering:
    """One definition of "the order the forms print them", because both stores
    have to agree and DynamoDB's own sort-key order agrees only by coincidence."""

    def test_roles_order_as_the_forms_print_them(self) -> None:
        roles = ["non_filing_spouse", "debtor_2", "debtor_1"]
        assert sorted(roles, key=role_order) == [
            "debtor_1",
            "debtor_2",
            "non_filing_spouse",
        ]

    def test_an_unknown_role_sorts_last_rather_than_raising(self) -> None:
        # Listing a case is a read path: one record written by a later revision
        # must not make the rest of the case unreadable.
        assert role_order("debtor_9") > role_order("non_filing_spouse")

    def test_the_order_does_not_depend_on_the_names_sorting_alphabetically(
        self,
    ) -> None:
        # The trap this exists to close: today's three names happen to be in
        # alphabetical order, so a bare sorted() would pass every test while
        # being wrong for the first role added that is not.
        assert role_order("debtor_1") == 0
        assert [role_order(role) for role in FILING_ROLES] == [0, 1, 2]
