"""Both FirmStore implementations, held to the same rules (ADR 0030).

Every test runs twice: against the memory twin and against the Postgres
adapter on a real, migrated database (`insolvia_core.adapters.postgres.testing`).
A suite that only ran the looser of two stores would prove nothing about the
one holding the data, and none of these conditions can be observed from a
route.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

from dataclasses import replace

import pytest
from insolvia_core.adapters.memory.firm_store import MemoryFirmStore
from insolvia_core.adapters.postgres.firm_store import (
    PostgresFirmStore,
    lock_linkable_client,
)
from insolvia_core.firm_clients import (
    create_firm_client,
    parse_firm_client,
    set_firm_client_status,
)
from insolvia_core.firms import (
    DefaultCourt,
    Firm,
    FirmUser,
    default_permissions,
)
from insolvia_core.library_creditors import (
    create_library_creditor,
    parse_library_creditor,
)
from insolvia_core.questionnaire import (
    SECTION_IDS,
    build_config,
    parse_questionnaire_update,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM_ID = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"


@pytest.fixture(params=["memory", "postgres"])
def empty_store(request):
    """Each FirmStore, holding nothing."""
    if request.param == "memory":
        return MemoryFirmStore()
    request.getfixturevalue("postgres")  # empties the tables
    return PostgresFirmStore(request.getfixturevalue("postgres_url"))


@pytest.fixture
def store(empty_store):
    """Each FirmStore, holding the two firms the tests below write into (a
    Postgres row needs its firm: `firm_id` is a foreign key)."""
    empty_store.create_firm(firm(FIRM_ID))
    empty_store.create_firm(firm(OTHER_FIRM_ID, "Other Firm LLP"))
    return empty_store


def library_creditor(firm_id: str = FIRM_ID, name: str = "Acme Collections"):
    draft = parse_library_creditor({"name": name, "preferred": True})
    return create_library_creditor(draft, firm_id=firm_id)


def firm(firm_id: str = FIRM_ID, name: str = "Example & Partners") -> Firm:
    return Firm(
        id=firm_id,
        name=name,
        status="active",
        created_at="2026-01-01T00:00:00.000Z",
        updated_at="2026-01-01T00:00:00.000Z",
    )


def user(
    subject: str = ALICE,
    firm_id: str = FIRM_ID,
    first_name: str = "Alice",
    last_name: str = "Attorney",
    **overrides: object,
) -> FirmUser:
    defaults: dict[str, object] = {
        "firm_id": firm_id,
        "subject": subject,
        "email": f"{first_name.lower()}@example.test",
        "first_name": first_name,
        "last_name": last_name,
        "role": "attorney",
        "is_admin": False,
        "access_all_cases": False,
        "permissions": default_permissions("attorney"),
        "status": "active",
        "created_at": "2026-01-01T00:00:00.000Z",
        "updated_at": "2026-01-01T00:00:00.000Z",
    }
    return FirmUser(**{**defaults, **overrides})  # type: ignore[arg-type]


# ── Firm users ──────────────────────────────────────────────────────


def test_adding_the_same_person_twice_is_refused(store):
    """attribute_not_exists(SK), as a dict. An overwrite would reset a
    colleague's permissions to whatever the caller sent, while the caller
    believes they are adding somebody new."""
    store.add_user(user())
    with pytest.raises(RuntimeError):
        store.add_user(user(first_name="Alice", last_name="Impostor"))
    assert store.get_user(FIRM_ID, ALICE).last_name == "Attorney"


def test_a_user_is_read_within_their_firm(store):
    """The key is (firm, subject), as it is in the table. If it were the
    subject alone, an admin of one firm could read a user of another by
    knowing their Cognito sub."""
    store.add_user(user())
    assert store.get_user(FIRM_ID, ALICE) is not None
    assert store.get_user(OTHER_FIRM_ID, ALICE) is None


def test_finding_a_subject_in_two_firms_raises_rather_than_guessing(store):
    """One person, one firm is an application invariant — nothing in the key
    schema enforces it, because the key is per firm.
    Picking one would make somebody's tenancy depend on index ordering, and it
    would be stable enough in testing to look correct."""
    store.add_user(user())
    store.add_user(user(firm_id=OTHER_FIRM_ID))
    with pytest.raises(RuntimeError):
        store.find_user(ALICE)


def test_finding_an_unprovisioned_subject_is_none_not_an_error(store):
    """Authenticated but not in any firm. It is a real state — a Cognito user
    exists before anyone attaches them to a firm — and resolution turns it into
    a 403, so the store must not raise on it."""
    assert store.find_user(ALICE) is None


def test_a_staff_list_is_one_firm_ordered_by_surname(store):
    """BY SURNAME, which is how a staff list is normally read and what the
    split made expressible.

    The two names are chosen so the orders DISAGREE: by surname it is Adams
    then Zimmerman, by first name it would be Adam then Zoe. A pair that sorted
    the same either way would pass under the old key and prove nothing."""
    store.add_user(user(subject=BOB, first_name="Adam", last_name="Zimmerman"))
    store.add_user(user(first_name="Zoe", last_name="Adams"))
    store.add_user(
        user(
            subject=BOB,
            firm_id=OTHER_FIRM_ID,
            first_name="Aaron",
            last_name="Other",
        )
    )

    listed = store.list_users(FIRM_ID)
    assert [(p.first_name, p.last_name) for p in listed] == [
        ("Zoe", "Adams"),
        ("Adam", "Zimmerman"),
    ]


def test_updating_someone_who_is_not_there_is_none(store):
    """The window between an administration route reading a user and writing
    them back. If a removal lands in between, the write must not resurrect the
    row — None rather than an exception, so the route answers the same 404 a
    foreign subject gets."""
    assert store.update_user(user()) is None
    assert store.get_user(FIRM_ID, ALICE) is None


def test_updating_across_firms_does_not_move_someone(store):
    """A PATCH carrying another firm's id must not create a row there. The
    Postgres adapter's `firm_id` condition says the same thing from the
    other side; without this the memory store would happily insert."""
    store.add_user(user())
    moved = replace(user(), firm_id=OTHER_FIRM_ID)
    assert store.update_user(moved) is None
    assert store.get_user(OTHER_FIRM_ID, ALICE) is None


def test_removing_someone_twice_reports_the_truth(store):
    store.add_user(user())
    assert store.remove_user(FIRM_ID, ALICE) is True
    assert store.remove_user(FIRM_ID, ALICE) is False


# ── Firms cross-tenant (#212): list and update, both stores ─────────


def test_the_firm_list_is_ordered_by_name_then_id(empty_store):
    empty_store.create_firm(firm(OTHER_FIRM_ID, "Other Firm LLP"))
    empty_store.create_firm(firm(FIRM_ID, "Example & Partners"))
    assert [f.name for f in empty_store.list_firms()] == [
        "Example & Partners",
        "Other Firm LLP",
    ]


def test_updating_a_firm_that_is_not_there_is_none(empty_store):
    """A deletion racing an update must not resurrect the firm from the
    caller's stale read — None, exactly as update_user answers."""
    assert empty_store.update_firm(firm()) is None
    assert empty_store.get_firm(FIRM_ID) is None


def test_updating_a_firm_writes_it_back(empty_store):
    from insolvia_core.firms import set_firm_status

    empty_store.create_firm(firm())
    suspended = set_firm_status(firm(), "suspended")
    assert empty_store.update_firm(suspended) == suspended
    assert empty_store.get_firm(FIRM_ID).status == "suspended"


# ── Library creditors (issue 13.9 / #350) ────────────────────────────


def test_creating_the_same_library_creditor_twice_is_refused(store):
    creditor = library_creditor()
    store.create_library_creditor(creditor)
    with pytest.raises(RuntimeError):
        store.create_library_creditor(creditor)


def test_a_library_creditor_is_read_within_its_firm(store):
    creditor = library_creditor()
    store.create_library_creditor(creditor)
    assert store.get_library_creditor(FIRM_ID, creditor.id) is not None
    assert store.get_library_creditor(OTHER_FIRM_ID, creditor.id) is None


def test_the_library_is_listed_by_name_within_one_firm(store):
    store.create_library_creditor(library_creditor(name="Zeta Bank"))
    store.create_library_creditor(library_creditor(name="Acme Collections"))
    store.create_library_creditor(
        library_creditor(firm_id=OTHER_FIRM_ID, name="Other Firm's Bank")
    )

    listed = store.list_library_creditors(FIRM_ID)
    assert [c.name for c in listed] == ["Acme Collections", "Zeta Bank"]


def test_updating_a_library_creditor_that_is_not_there_is_none(store):
    assert store.update_library_creditor(library_creditor()) is None


def test_updating_across_firms_does_not_move_a_library_creditor(store):
    creditor = library_creditor()
    store.create_library_creditor(creditor)
    moved = replace(creditor, firm_id=OTHER_FIRM_ID)
    assert store.update_library_creditor(moved) is None
    assert store.get_library_creditor(OTHER_FIRM_ID, creditor.id) is None


def test_removing_a_library_creditor_twice_reports_the_truth(store):
    creditor = library_creditor()
    store.create_library_creditor(creditor)
    assert store.delete_library_creditor(FIRM_ID, creditor.id) is True
    assert store.delete_library_creditor(FIRM_ID, creditor.id) is False


# ── Firm clients (ADR 0022 / #353) ───────────────────────────────────


def firm_client(firm_id: str = FIRM_ID, surname: str = "Example"):
    draft = parse_firm_client(
        {
            "name": {"given": "Jordan", "surname": surname},
            "residence_address": {"line1": "1 Example St", "county": "Sangamon"},
            "other_names_used": [{"id": "alias-1", "surname": "Sample"}],
            "date_of_birth": "1980-02-29",
        }
    )
    return create_firm_client(draft, firm_id=firm_id, created_by=ALICE)


def test_creating_the_same_client_twice_is_refused(store):
    client = firm_client()
    store.create_client(client)
    with pytest.raises(RuntimeError):
        store.create_client(client)


def test_a_client_is_read_within_its_firm(store):
    client = firm_client()
    store.create_client(client)
    assert store.get_client(FIRM_ID, client.id) == client
    assert store.get_client(OTHER_FIRM_ID, client.id) is None


def test_the_directory_is_one_firm_in_directory_order(store):
    store.create_client(firm_client(surname="Zeta"))
    store.create_client(firm_client(surname="adams"))
    store.create_client(firm_client(firm_id=OTHER_FIRM_ID, surname="Other"))
    archived = set_firm_client_status(firm_client(surname="Middle"), "archived")
    store.create_client(archived)

    listed = store.list_clients(FIRM_ID)
    # Archived clients are listed too: the directory is the whole directory.
    assert [c.name.surname for c in listed] == ["adams", "Middle", "Zeta"]


def test_updating_a_client_that_is_not_there_is_none(store):
    assert store.update_client(firm_client()) is None


def test_updating_across_firms_does_not_move_a_client(store):
    client = firm_client()
    store.create_client(client)
    assert store.update_client(replace(client, firm_id=OTHER_FIRM_ID)) is None
    assert store.get_client(OTHER_FIRM_ID, client.id) is None


# ── The client questionnaire's config (ADR 0023 PR 3 / #362) ───────


def questionnaire(firm_id: str = FIRM_ID, *, other: bool = False):
    body = {
        "sections": [{"id": s, "enabled": other or s != "other"} for s in SECTION_IDS]
    }
    return build_config(
        parse_questionnaire_update(body), firm_id=firm_id, updated_by=ALICE
    )


def test_a_firm_with_no_questionnaire_config_reads_none(store):
    assert store.get_questionnaire(FIRM_ID) is None


def test_a_questionnaire_config_is_read_within_its_firm(store):
    saved = questionnaire()

    store.put_questionnaire(saved)

    assert store.get_questionnaire(FIRM_ID) == saved
    assert store.get_questionnaire(OTHER_FIRM_ID) is None


def test_a_questionnaire_save_replaces_the_stored_one(store):
    store.put_questionnaire(questionnaire())

    store.put_questionnaire(questionnaire(other=True))

    config = store.get_questionnaire(FIRM_ID)
    assert config is not None
    assert config.sections["other"].enabled is True


def test_resetting_the_questionnaire_twice_reports_the_truth(store):
    store.put_questionnaire(questionnaire())

    assert store.delete_questionnaire(FIRM_ID) is True
    assert store.delete_questionnaire(FIRM_ID) is False
    assert store.get_questionnaire(FIRM_ID) is None


# ── What only a real database can show (ADR 0030) ───────────────────


def test_a_user_round_trips_with_its_types_intact(store):
    """THE SILENT FAILURE: a boolean stored as anything else would make every
    admin quietly stop being one, with nothing raising anywhere."""
    person = user(is_admin=True, access_all_cases=True)
    store.add_user(person)
    assert store.get_user(FIRM_ID, ALICE) == person
    assert store.find_user(ALICE) == person


def test_a_firm_round_trips_with_its_defaults_and_provenance(empty_store):
    record = replace(
        firm(),
        created_by=ALICE,
        created_by_email="alice@example.test",
        default_court=DefaultCourt(court="ilcb", division="springfield"),
        default_chapter=13,
    )
    empty_store.create_firm(record)
    assert empty_store.get_firm(FIRM_ID) == record


def test_creating_the_same_firm_twice_is_refused(store):
    with pytest.raises(RuntimeError):
        store.create_firm(firm())


def test_a_library_creditor_round_trips_with_its_nested_shapes(store):
    draft = parse_library_creditor(
        {
            "name": "Acme Collections",
            "address": {"line1": "1 Example St", "city": "Springfield"},
            "additional_notice_parties": [
                {
                    "id": "party-1",
                    "name": "Acme Legal",
                    "address": {"line1": "2 Example St"},
                }
            ],
            "notes": "Prefers mail.",
        }
    )
    creditor = create_library_creditor(draft, firm_id=FIRM_ID)
    store.create_library_creditor(creditor)
    assert store.get_library_creditor(FIRM_ID, creditor.id) == creditor


def test_a_client_round_trips_with_its_sealed_tax_id_reference(store):
    client = replace(
        firm_client(),
        tax_id_ref="00000000-0000-4000-8000-0000000000ef",
        tax_id_last_four="6789",
    )
    store.create_client(client)
    assert store.get_client(FIRM_ID, client.id) == client


def test_a_client_save_keeps_the_merge_claim_and_the_funnel_stage(store):
    """A save built from a read taken before a merge claimed the row, or a
    stage was set, must not erase either."""
    client, other = firm_client(), firm_client(surname="Other")
    store.create_client(client)
    store.create_client(other)
    store.set_client_prospect_stage(FIRM_ID, client.id, "possible")
    assert store.claim_client_merge(FIRM_ID, merged_id=client.id, survivor_id=other.id)

    saved = store.update_client(replace(client, date_of_birth="1981-01-01"))

    assert saved is not None
    assert saved.merging_into == other.id
    assert saved.prospect_stage == "possible"
    assert store.get_client(FIRM_ID, client.id) == saved


def test_a_client_save_without_a_tax_id_removes_it(store):
    client = replace(firm_client(), tax_id_ref="ref", tax_id_last_four="6789")
    store.create_client(client)
    store.update_client(replace(client, tax_id_ref=None, tax_id_last_four=None))
    assert store.get_client(FIRM_ID, client.id).tax_id_ref is None


def test_a_retained_client_leaves_the_funnel_and_refuses_a_stage(store):
    client = firm_client()
    store.create_client(client)
    store.set_client_prospect_stage(FIRM_ID, client.id, "possible")

    retained = store.mark_client_retained(FIRM_ID, client.id, retained_on="2026-02-01")
    again = store.mark_client_retained(FIRM_ID, client.id, retained_on="2026-03-01")

    assert retained.prospect_stage is None
    assert again.first_retained_at == "2026-02-01"
    assert store.set_client_prospect_stage(FIRM_ID, client.id, "possible") is None


def test_a_merge_claims_finishes_and_terminates_both_rows(store):
    merged, survivor, third = (
        firm_client(),
        firm_client(surname="Survivor"),
        firm_client(surname="Third"),
    )
    for client in (merged, survivor, third):
        store.create_client(client)

    assert store.claim_client_merge(
        FIRM_ID, merged_id=merged.id, survivor_id=survivor.id
    )
    # Either row already in a merge refuses another, and nothing is written.
    assert not store.claim_client_merge(
        FIRM_ID, merged_id=third.id, survivor_id=merged.id
    )
    assert store.get_client(FIRM_ID, third.id).merging_into is None

    done = store.finish_client_merge(
        FIRM_ID, merged_id=merged.id, survivor_id=survivor.id, merged_at="2026-02-01"
    )

    assert done.status == "archived"
    assert done.merged_into == survivor.id
    assert done.merging_into is None
    assert store.get_client(FIRM_ID, survivor.id).merging_from is None
    assert store.update_client(merged) is None  # a merged client is terminal


def test_a_released_merge_frees_both_rows(store):
    merged, survivor = firm_client(), firm_client(surname="Survivor")
    store.create_client(merged)
    store.create_client(survivor)
    store.claim_client_merge(FIRM_ID, merged_id=merged.id, survivor_id=survivor.id)

    store.release_client_merge(FIRM_ID, merged_id=merged.id, survivor_id=survivor.id)

    assert store.get_client(FIRM_ID, merged.id).merging_into is None
    assert store.get_client(FIRM_ID, survivor.id).merging_from is None
    assert (
        store.finish_client_merge(
            FIRM_ID, merged_id=merged.id, survivor_id=survivor.id, merged_at="x"
        )
        is None
    )


def test_the_link_condition_follows_the_clients_merge_state(postgres, postgres_url):
    """`lock_linkable_client`, the case stores' condition on this row: a
    client being merged away or archived refuses a link; one receiving a
    merge, or active, accepts it."""
    store = PostgresFirmStore(postgres_url)
    store.create_firm(firm())
    merged, survivor = firm_client(), firm_client(surname="Survivor")
    store.create_client(merged)
    store.create_client(survivor)
    store.claim_client_merge(FIRM_ID, merged_id=merged.id, survivor_id=survivor.id)

    assert not lock_linkable_client(postgres, FIRM_ID, merged.id)
    assert lock_linkable_client(postgres, FIRM_ID, survivor.id)
    assert not lock_linkable_client(postgres, OTHER_FIRM_ID, survivor.id)
    assert not lock_linkable_client(postgres, FIRM_ID, "missing")
