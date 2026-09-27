"""A firm's client (ADR 0022 / #353): parsing, the whole-record replace, the
status write, the directory order, the stored item and the wire shape — and
the two things this revision must hold down beyond the library-creditor
precedent: the tax id is a pointer the wire can never set, and the sort key
cannot collide with the portal binding's.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace
from datetime import date, timedelta

import pytest
from insolvia_core.access_log import access_item, record_access
from insolvia_core.clients import ClientBinding, binding_item, client_sort_key
from insolvia_core.debtors import parse_debtor
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.fields import Address, PersonName
from insolvia_core.firm_clients import (
    ACTIVE,
    ARCHIVED,
    SK_PREFIX,
    FirmClient,
    create_firm_client,
    firm_client_from_item,
    firm_client_item,
    firm_client_json,
    parse_client_status,
    parse_firm_client,
    replace_firm_client,
    set_firm_client_status,
    sort_key,
    sorted_firm_clients,
)
from insolvia_core.firms import (
    CLIENTS,
    FEATURES,
    HIDDEN,
    VIEW_ONLY,
    FirmUser,
    default_permissions,
    permission_for,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
ALICE = "00000000-0000-4000-8000-00000000a11c"

FULL = {
    "name": {"given": "Jordan", "middle": "Q", "surname": "Example", "suffix": "Jr."},
    "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
    "date_of_birth": "1980-02-29",
    "residence_address": {
        "line1": "1 Example St",
        "city": "Springfield",
        "state": "IL",
        "postal_code": "62701",
        "county": "Sangamon",
    },
    "mailing_address": {"line1": "PO Box 1"},
    "phone": "555-0100",
    "mobile": "555-0101",
    "email": "jordan@example.test",
    "lead_source": "Referral",
    "referred_by": "A former client",
    "first_retained_at": "2026-09-01",
}


def make(payload=None, **overrides) -> FirmClient:
    draft = parse_firm_client({**(payload or FULL), **overrides})
    return create_firm_client(draft, firm_id=FIRM_ID, created_by=ALICE)


# ── Parsing ─────────────────────────────────────────────────────────


def test_a_name_is_required():
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client({})
    assert "name" in caught.value.fields


@pytest.mark.parametrize("half", ["surname", "given"])
def test_either_half_of_the_name_is_enough(half):
    """A mononym is a given name with no surname — ADR 0022's "name.surname
    or name.given the one required field"."""
    draft = parse_firm_client({"name": {half: "Example"}})
    assert getattr(draft.name, half) == "Example"


def test_a_middle_name_or_suffix_alone_is_not_a_name():
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client({"name": {"middle": "Q", "suffix": "Jr."}})
    assert "name" in caught.value.fields


def test_everything_but_the_name_is_optional():
    draft = parse_firm_client({"name": {"surname": "Example"}})
    assert draft.residence_address == Address()
    assert draft.other_names_used == ()
    assert draft.date_of_birth is None
    assert draft.lead_source is None


def test_a_full_record_parses():
    draft = parse_firm_client(FULL)
    assert draft.name == PersonName(
        given="Jordan", middle="Q", surname="Example", suffix="Jr."
    )
    assert draft.residence_address.county == "Sangamon"
    assert draft.other_names_used[0].id == "alias-1"
    assert draft.first_retained_at == "2026-09-01"


def test_dates_are_form_dates():
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client(
            {**FULL, "date_of_birth": "1980-02-30", "first_retained_at": "Sept 1"}
        )
    assert set(caught.value.fields) == {"date_of_birth", "first_retained_at"}


def test_a_date_of_birth_in_the_future_is_refused():
    tomorrow = (date.today() + timedelta(days=1)).isoformat()
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client({**FULL, "date_of_birth": tomorrow})
    assert "date_of_birth" in caught.value.fields


def test_an_alias_needs_its_own_id_exactly_as_a_debtors_does():
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client({**FULL, "other_names_used": [{"surname": "Sample"}]})
    assert "other_names_used[0].id" in caught.value.fields


@pytest.mark.parametrize(
    ("key", "value"),
    [
        ("tax_id", {"kind": "ssn", "value": "987-65-4320"}),
        ("tax_id_ref", "00000000-0000-4000-8000-0000000000ef"),
        ("tax_id_last_four", "4320"),
    ],
)
def test_the_tax_id_cannot_be_set_through_the_client_body(key, value):
    """Refused, not ignored: a caller that sent digits would otherwise
    believe a number was saved, and a caller that sent a ref would be
    pointing this client at somebody's sealed identifier."""
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_client({**FULL, key: value})
    assert key in caught.value.fields


def test_server_owned_fields_in_the_body_are_ignored():
    """A client echoing a GET body back into a PUT sends `id`, `status`,
    `created_by`… — none of which it controls, and none of which is an
    error to send."""
    draft = parse_firm_client(
        {**FULL, "id": "x", "status": ARCHIVED, "created_by": "someone"}
    )
    assert not hasattr(draft, "status")


def test_the_identity_fields_are_ones_a_debtor_accepts():
    """ADR 0022 copies a client's identity onto a debtor; a value the client
    record accepts must be one the debtor's own parser accepts."""
    copied = {
        key: FULL[key]
        for key in (
            "name",
            "other_names_used",
            "residence_address",
            "mailing_address",
            "phone",
            "mobile",
            "email",
        )
    }
    parse_debtor(copied, enforce_provenance=False)


# ── Transitions ─────────────────────────────────────────────────────


def test_a_new_client_is_active_and_stamped():
    client = make()
    assert client.status == ACTIVE
    assert client.firm_id == FIRM_ID
    assert client.created_by == ALICE
    assert client.created_at == client.updated_at
    assert client.tax_id_ref is None


def test_a_replace_keeps_what_the_caller_does_not_own():
    client = replace(
        make(),
        status=ARCHIVED,
        tax_id_ref="00000000-0000-4000-8000-0000000000ef",
        tax_id_last_four="4320",
    )
    replaced = replace_firm_client(
        client, parse_firm_client({"name": {"surname": "Renamed"}})
    )
    for kept in ("id", "firm_id", "created_at", "created_by"):
        assert getattr(replaced, kept) == getattr(client, kept)
    assert replaced.status == ARCHIVED
    assert replaced.tax_id_ref == client.tax_id_ref
    assert replaced.tax_id_last_four == "4320"
    # Whole record: what the draft left out is cleared.
    assert replaced.name == PersonName(surname="Renamed")
    assert replaced.residence_address == Address()
    assert replaced.lead_source is None


def test_a_status_write_changes_the_status_and_nothing_else():
    client = make()
    archived = set_firm_client_status(client, ARCHIVED)
    assert archived.status == ARCHIVED
    assert archived.archived
    assert replace(archived, status=ACTIVE, updated_at=client.updated_at) == client


@pytest.mark.parametrize("body", [{}, {"status": "deleted"}, {"status": None}])
def test_the_status_must_be_one_of_the_two(body):
    with pytest.raises(FieldValidationError) as caught:
        parse_client_status(body)
    assert "status" in caught.value.fields


def test_the_directory_is_ordered_by_surname_then_given_ignoring_case():
    names = [
        {"surname": "zeta", "given": "A"},
        {"surname": "Adams", "given": "Bea"},
        {"surname": "adams", "given": "al"},
        {"given": "Mononym"},
    ]
    clients = [make({"name": name}) for name in names]
    ordered = sorted_firm_clients(clients)
    assert [(c.name.surname, c.name.given) for c in ordered] == [
        (None, "Mononym"),
        ("adams", "al"),
        ("Adams", "Bea"),
        ("zeta", "A"),
    ]


# ── The stored item ─────────────────────────────────────────────────


def test_the_item_lives_in_the_firm_partition_under_its_own_prefix():
    client = make()
    item = firm_client_item(client)
    assert item["PK"] == f"FIRM#{FIRM_ID}"
    assert item["SK"] == f"FIRMCLIENT#{client.id}" == sort_key(client.id)
    assert "GSI1PK" not in item


def test_the_sort_key_cannot_collide_with_the_portal_binding():
    """The binding's authoritative row is `SK CLIENT#<subject>` in this same
    partition, and the binding store lists it with `begins_with(SK,
    "CLIENT#")`. Neither prefix may begin the other, or one listing returns
    the other's rows."""
    ours = sort_key("anything")
    theirs = client_sort_key("anything")
    binding_prefix = theirs.split("#", 1)[0] + "#"
    assert not ours.startswith(binding_prefix)
    assert not theirs.startswith(f"{SK_PREFIX}#")


def test_the_binding_row_does_not_parse_as_a_client():
    """Belt to the braces above: were a binding row ever handed to this
    parser, it must fail loudly, not become a nameless client."""
    binding = ClientBinding(
        firm_id=FIRM_ID,
        case_id="00000000-0000-4000-8000-00000000ca5e",
        subject=ALICE,
        email="a@example.test",
        display_name="A",
        roles=("debtor_1",),
        status="invited",
        invited_by=ALICE,
        created_at="2026-01-01T00:00:00Z",
        updated_at="2026-01-01T00:00:00Z",
    )
    with pytest.raises(ValidationError):
        firm_client_from_item(binding_item(binding))


def test_the_item_round_trips():
    client = replace(
        make(),
        tax_id_ref="00000000-0000-4000-8000-0000000000ef",
        tax_id_last_four="4320",
    )
    assert firm_client_from_item(firm_client_item(client)) == client


def test_a_client_without_a_tax_id_stores_no_tax_id_attribute():
    assert "taxId" not in firm_client_item(make())


def test_the_ref_is_beside_the_body_never_in_it():
    item = firm_client_item(
        replace(make(), tax_id_ref="ref-1", tax_id_last_four="4320")
    )
    assert item["taxId"] == {"ref": "ref-1", "lastFour": "4320"}
    body = item["body"]
    assert isinstance(body, dict)
    assert not any(key.startswith("tax") for key in body)


def test_a_malformed_item_raises_rather_than_half_populating():
    item = firm_client_item(make())
    del item["createdBy"]
    with pytest.raises(ValidationError):
        firm_client_from_item(item)
    with pytest.raises(ValidationError):
        firm_client_from_item({**firm_client_item(make()), "status": "merged"})


# ── The wire ────────────────────────────────────────────────────────


def test_the_wire_is_snake_case_and_omits_absent_fields():
    body = firm_client_json(make({"name": {"surname": "Example"}}))
    assert set(body) == {
        "id",
        "status",
        "created_at",
        "updated_at",
        "created_by",
        "name",
    }
    assert body["name"] == {"surname": "Example"}


def test_the_wire_carries_last_four_and_never_the_ref():
    body = firm_client_json(
        replace(make(), tax_id_ref="ref-1", tax_id_last_four="4320")
    )
    assert body["tax_id_last_four"] == "4320"
    assert "ref-1" not in repr(body)
    assert "tax_id_ref" not in body


def test_a_full_record_survives_the_wire_back_into_the_parser():
    """What GET returns is what PUT accepts — the edit form round-trips."""
    assert parse_firm_client(firm_client_json(make())) == parse_firm_client(FULL)


# ── The feature and the access log ──────────────────────────────────


def test_clients_is_a_feature_with_adr_0022s_defaults():
    assert CLIENTS in FEATURES
    assert default_permissions("attorney")[CLIENTS] == "add_edit"
    assert default_permissions("paralegal")[CLIENTS] == "add_edit"
    assert default_permissions("staff")[CLIENTS] == VIEW_ONLY


def test_an_existing_row_without_the_feature_has_it_hidden():
    """No migration grants it (ADR 0022): a row written before `clients`
    existed reads as hidden until an admin grants it."""
    permissions = dict(default_permissions("attorney"))
    del permissions[CLIENTS]
    user = FirmUser(
        firm_id=FIRM_ID,
        subject=ALICE,
        email="a@example.test",
        first_name="A",
        last_name="B",
        role="attorney",
        is_admin=False,
        access_all_cases=False,
        permissions=permissions,
        status="active",
        created_at="2026-01-01T00:00:00Z",
        updated_at="2026-01-01T00:00:00Z",
    )
    assert permission_for(user, CLIENTS) == HIDDEN


def test_a_client_access_row_is_keyed_by_the_client():
    event = record_access(client_id="c-1", principal=ALICE, action="client.read")
    assert (event.client_id, event.case_id) == ("c-1", None)
    item = access_item(event)
    assert item["PK"] == "CLIENT#c-1"
    assert item["clientId"] == "c-1"
    assert "caseId" not in item


def test_a_case_access_row_is_unchanged_by_the_generalisation():
    """Every row before ADR 0022 was `CASE#<id>` with a `caseId` — the
    subject key must still write exactly that."""
    event = record_access(case_id="k-1", principal=ALICE, action="case.read")
    assert (event.case_id, event.client_id) == ("k-1", None)
    item = access_item(event)
    assert item["PK"] == "CASE#k-1"
    assert item["caseId"] == "k-1"
    assert "clientId" not in item


@pytest.mark.parametrize(
    "ids", [{}, {"case_id": "k-1", "client_id": "c-1"}], ids=["neither", "both"]
)
def test_an_access_row_names_exactly_one_subject(ids):
    with pytest.raises(ValueError, match="exactly one"):
        record_access(principal=ALICE, action="case.read", **ids)
