"""The firm domain: entities, parsing, permissions, and the stored shape.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.errors import FieldValidationError, ValidationError
from insolvia_core.firms import (
    ADD_EDIT,
    CASES,
    DOCUMENTS,
    EXTRACTION_REVIEW,
    FEATURES,
    FIRM_ADMINISTRATION,
    HIDDEN,
    INTAKE,
    LEVELS,
    VIEW_ONLY,
    FirmUser,
    apply_user_changes,
    create_firm,
    create_firm_user,
    default_permissions,
    firm_from_item,
    firm_item,
    firm_json,
    firm_user_from_item,
    firm_user_item,
    firm_user_json,
    parse_firm_creation,
    parse_firm_user_creation,
    parse_firm_user_update,
    parse_self_update,
    permission_for,
    permits,
    set_firm_status,
)

FIRM_ID = "00000000-0000-4000-8000-00000000f18a"
OTHER_FIRM_ID = "00000000-0000-4000-8000-00000000f18b"
ALICE = "00000000-0000-4000-8000-00000000a11c"
BOB = "00000000-0000-4000-8000-00000000b0b0"


def user(**overrides: object) -> FirmUser:
    defaults: dict[str, object] = {
        "firm_id": FIRM_ID,
        "subject": ALICE,
        "email": "alice@example.test",
        "display_name": "Alice Attorney",
        "role": "attorney",
        "is_admin": False,
        "access_all_cases": False,
        "permissions": default_permissions("attorney"),
        "status": "active",
        "created_at": "2026-01-01T00:00:00.000Z",
        "updated_at": "2026-01-01T00:00:00.000Z",
    }
    return FirmUser(**{**defaults, **overrides})  # type: ignore[arg-type]


# ── Permissions ─────────────────────────────────────────────────────


def test_an_unknown_feature_is_hidden():
    """FAIL CLOSED, and the case this actually guards.

    `extraction_review` is in FEATURES and does not exist yet. When it ships,
    every firm user already in the table has a permission map written before it
    was named. If the missing key defaulted to anything but `hidden`, the
    feature would arrive already granted to everyone — including staff, whose
    defaults deliberately exclude it.
    """
    without = user(permissions={CASES: ADD_EDIT})
    assert permission_for(without, EXTRACTION_REVIEW) == HIDDEN
    assert not permits(without, EXTRACTION_REVIEW, VIEW_ONLY)


def test_a_level_this_version_does_not_know_is_hidden():
    """The same rule applied to a value rather than a key. A row written by a
    newer version with a level we cannot rank must not be ranked wrong — and
    `LEVELS.index` would raise on it, turning an unknown grant into a 500 on
    every request that user makes."""
    assert permission_for(user(permissions={CASES: "full_control"}), CASES) == HIDDEN


def test_a_disabled_user_has_no_permissions():
    """Belt to accessor resolution's braces. Resolution refuses to mint an
    Accessor for a non-active user, so this branch should be unreachable —
    which is exactly why it is worth a test: nothing else would notice if it
    stopped working."""
    disabled = user(status="disabled", is_admin=True)
    assert permission_for(disabled, CASES) == HIDDEN
    # Admin does not rescue it. The status check is above the admin branch on
    # purpose; the other order would give a disabled admin full access.
    assert not permits(disabled, CASES, VIEW_ONLY)


def test_an_admin_gets_add_edit_on_everything():
    admin = user(is_admin=True, permissions={})
    for feature in FEATURES:
        assert permission_for(admin, feature) == ADD_EDIT


@pytest.mark.parametrize(
    ("held", "required", "allowed"),
    [
        (ADD_EDIT, ADD_EDIT, True),
        (ADD_EDIT, VIEW_ONLY, True),
        (ADD_EDIT, HIDDEN, True),
        (VIEW_ONLY, ADD_EDIT, False),
        (VIEW_ONLY, VIEW_ONLY, True),
        (HIDDEN, VIEW_ONLY, False),
        (HIDDEN, ADD_EDIT, False),
    ],
)
def test_levels_are_ordered_weakest_to_strongest(held, required, allowed):
    assert permits(user(permissions={CASES: held}), CASES, required) is allowed


def test_levels_are_ranked_in_the_documented_order():
    """The ordering `permits` compares by. Inserting a level in the middle of
    LEVELS silently changes what every existing check means, so the order is
    pinned here rather than left to the tuple literal."""
    assert LEVELS == (HIDDEN, VIEW_ONLY, ADD_EDIT)


def test_permits_refuses_a_requirement_it_cannot_rank():
    with pytest.raises(ValidationError):
        permits(user(), CASES, "superuser")


def test_no_role_gets_firm_administration_by_default():
    """Managing the firm's users is what `is_admin` is. A role that granted it
    too would make "who can add users here" a two-field question."""
    for role in ("attorney", "paralegal", "staff"):
        assert default_permissions(role)[FIRM_ADMINISTRATION] == HIDDEN


def test_staff_defaults_are_read_only_on_the_case_record():
    staff = default_permissions("staff")
    assert staff[CASES] == VIEW_ONLY
    assert staff[INTAKE] == VIEW_ONLY
    # They chase paperwork; that is the job.
    assert staff[DOCUMENTS] == ADD_EDIT


def test_attorney_and_paralegal_defaults_are_identical():
    """Asserted rather than assumed. Both do case work, and the axes that
    actually differ are is_admin and access_all_cases — see
    default_permissions. A future edit that gives one of them something the
    other lacks should have to change this test and say why."""
    assert default_permissions("attorney") == default_permissions("paralegal")


def test_every_role_default_names_every_feature():
    for role in ("attorney", "paralegal", "staff"):
        assert set(default_permissions(role)) == set(FEATURES)


# ── Parsing ─────────────────────────────────────────────────────────


def test_a_firm_needs_a_name():
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_creation({})
    assert "name" in caught.value.fields


def test_a_firm_starts_active():
    firm = create_firm(parse_firm_creation({"name": "  Example & Partners  "}))
    assert firm.name == "Example & Partners"
    assert firm.status == "active"


def test_creating_a_user_fills_in_the_role_defaults():
    draft = parse_firm_user_creation(
        {"email": "Bob@Example.test", "displayName": "Bob", "role": "staff"}
    )
    # Lowercased: a firm admin typing a capital is not adding a second person.
    assert draft.email == "bob@example.test"
    assert draft.permissions == default_permissions("staff")
    assert draft.is_admin is False
    assert draft.access_all_cases is False


def test_supplied_permissions_merge_over_the_defaults_on_creation():
    """Merge, not replace. A caller granting one feature must not silently
    revoke the rest — which is what a bare replace would do to a staff user
    whose defaults they never sent."""
    draft = parse_firm_user_creation(
        {
            "email": "bob@example.test",
            "displayName": "Bob",
            "role": "staff",
            "permissions": {EXTRACTION_REVIEW: VIEW_ONLY},
        }
    )
    assert draft.permissions[EXTRACTION_REVIEW] == VIEW_ONLY
    assert draft.permissions[DOCUMENTS] == ADD_EDIT


@pytest.mark.parametrize(
    "value", ["true", "TRUE", 1, "yes", None], ids=["str", "STR", "int", "word", "null"]
)
def test_the_admin_flag_must_be_a_real_boolean(value):
    """THE ONE THAT MATTERS MOST HERE. JSON `"false"` is a non-empty string, so
    a truthy check would make every one of these an admin — including the one
    that was trying to say no."""
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_user_creation(
            {
                "email": "bob@example.test",
                "displayName": "Bob",
                "role": "staff",
                "isAdmin": value,
            }
        )
    assert "isAdmin" in caught.value.fields


def test_an_unknown_feature_is_refused_rather_than_dropped():
    """A typo takes exactly this shape. Dropping it would echo the admin's own
    request back at them while the stored row disagreed."""
    with pytest.raises(FieldValidationError) as caught:
        parse_firm_user_creation(
            {
                "email": "bob@example.test",
                "displayName": "Bob",
                "role": "staff",
                "permissions": {"document": ADD_EDIT},
            }
        )
    assert "permissions" in caught.value.fields


def test_an_unknown_level_is_refused():
    with pytest.raises(FieldValidationError):
        parse_firm_user_creation(
            {
                "email": "bob@example.test",
                "displayName": "Bob",
                "role": "staff",
                "permissions": {DOCUMENTS: "full_control"},
            }
        )


def test_a_patch_replaces_the_permission_map():
    """The opposite of creation, and deliberately so: a merging PATCH could
    only ever grant, leaving no way to express "take documents away"."""
    changes = parse_firm_user_update({"permissions": {CASES: VIEW_ONLY}})
    assert changes.permissions == {CASES: VIEW_ONLY}
    updated = apply_user_changes(user(), changes)
    assert permission_for(updated, DOCUMENTS) == HIDDEN


def test_a_patch_cannot_change_an_email():
    """Not in the parser, so it lands in the "no supported fields" branch. The
    address on this row is the one Cognito authenticates and sends to."""
    with pytest.raises(ValidationError):
        parse_firm_user_update({"email": "elsewhere@example.test"})


def test_an_empty_patch_is_refused():
    with pytest.raises(ValidationError):
        parse_firm_user_update({})


def test_changing_a_role_leaves_the_permission_map_alone():
    """Promoting someone must not quietly undo hand-tuned permissions as a
    side effect of a job-title edit."""
    tuned = user(
        role="staff", permissions={**default_permissions("staff"), CASES: HIDDEN}
    )
    promoted = apply_user_changes(tuned, parse_firm_user_update({"role": "attorney"}))
    assert promoted.role == "attorney"
    assert permission_for(promoted, CASES) == HIDDEN


def test_a_self_update_renames_and_nothing_else():
    changes = parse_self_update({"displayName": "Robert"})
    updated = apply_user_changes(user(), changes)
    assert updated.display_name == "Robert"
    assert (updated.role, updated.is_admin, updated.status) == (
        user().role,
        user().is_admin,
        user().status,
    )


@pytest.mark.parametrize(
    "payload",
    [{"role": "attorney"}, {"isAdmin": True}, {"status": "disabled"}, {}],
    ids=["role", "isAdmin", "status", "empty"],
)
def test_a_self_update_without_a_display_name_is_refused(payload):
    """`role` and friends land in the same branch as an empty payload: the
    parser never produces anything but a rename, so a privilege field here is
    ignored the same way `email` is in parse_firm_user_update — and with no
    rename alongside it, there is nothing to do."""
    with pytest.raises(ValidationError):
        parse_self_update(payload)


@pytest.mark.parametrize(
    "value",
    ["", "   ", None, 7, "x" * 201],
    ids=["empty", "blank", "null", "int", "long"],
)
def test_a_self_update_validates_the_name_like_any_other(value):
    with pytest.raises(FieldValidationError) as caught:
        parse_self_update({"displayName": value})
    assert "displayName" in caught.value.fields


def test_a_subject_must_be_a_cognito_sub():
    """It flows into a sort key and a GSI partition key. A value carrying a `#`
    would produce a key that collides with a differently-spelled one."""
    draft = parse_firm_user_creation(
        {"email": "bob@example.test", "displayName": "Bob", "role": "staff"}
    )
    with pytest.raises(ValidationError):
        create_firm_user(draft, firm_id=FIRM_ID, subject="USER#" + BOB)


# ── The stored shape ────────────────────────────────────────────────


def test_a_firm_carries_no_gsi_keys():
    """What makes the by-subject index sparse. With them, a query for a
    subject would return a mix of people and the firms they belong to."""
    item = firm_item(create_firm(parse_firm_creation({"name": "Example"})))
    assert "GSI1PK" not in item
    assert "GSI1SK" not in item


def test_a_firm_user_always_carries_the_index_keys():
    """THE SHARP EDGE OF A SPARSE INDEX. DynamoDB indexes an item only when it
    has every key attribute, so omitting GSI1PK raises nothing — it produces a
    user who simply cannot sign in, with no error anywhere."""
    item = firm_user_item(user())
    assert item["GSI1PK"] == f"USER#{ALICE}"
    assert item["GSI1SK"] == f"FIRM#{FIRM_ID}"
    assert item["PK"] == f"FIRM#{FIRM_ID}"
    assert item["SK"] == f"USER#{ALICE}"


def test_a_firm_user_round_trips():
    original = user(is_admin=True, access_all_cases=True)
    assert firm_user_from_item(firm_user_item(original)) == original


def test_a_firm_round_trips():
    original = create_firm(parse_firm_creation({"name": "Example"}))
    assert firm_from_item(firm_item(original)) == original


def test_the_flags_survive_as_booleans_not_truthy_values():
    """`is_admin` reads `item["isAdmin"] is True`, which is stricter than a
    truthy check on purpose: the number 1 — what a bool stored through an
    isinstance(int) branch becomes — must NOT come back as an admin."""
    item = dict(firm_user_item(user(is_admin=True)))
    item["isAdmin"] = "true"  # type: ignore[assignment]
    assert firm_user_from_item(item).is_admin is False


def test_a_stored_permission_this_version_does_not_know_is_dropped():
    """Fail closed at the boundary too. A row from a newer version naming a
    feature we cannot rank must not enter the service as a grant."""
    item = dict(firm_user_item(user()))
    item["permissions"] = {**default_permissions("attorney"), "billing": ADD_EDIT}
    restored = firm_user_from_item(item)
    assert "billing" not in restored.permissions


def test_a_malformed_row_fails_loudly():
    item = dict(firm_user_item(user()))
    del item["role"]
    with pytest.raises(ValidationError):
        firm_user_from_item(item)


def test_the_api_representation_shows_the_stored_map_not_the_effective_one():
    """An admin's row says firm_administration: hidden while `permission_for`
    answers add_edit for them. Resolving it here would make the admin flag look
    like it had rewritten the map; the client has `isAdmin` and renders the
    override."""
    body = firm_user_json(user(is_admin=True))
    assert body["isAdmin"] is True
    assert body["permissions"][FIRM_ADMINISTRATION] == HIDDEN  # type: ignore[index]
    # The subject IS exposed, unlike a case's ownerPrincipal: every other
    # endpoint addresses a colleague by it.
    assert body["subject"] == ALICE


# ── Provenance and status (#212) ────────────────────────────────────


def test_provenance_is_recorded_when_the_creator_is_known():
    firm = create_firm(
        parse_firm_creation({"name": "Example"}),
        created_by=ALICE,
        created_by_email="staff@example.test",
    )
    assert firm.created_by == ALICE
    assert firm.created_by_email == "staff@example.test"


def test_provenance_is_sparse_in_the_item_and_none_reading_back():
    """A seeded firm has no author, and the item says so by ABSENCE — the
    console reading and the tolerant inverse then agree. A null-valued
    attribute would be a third encoding of the same fact."""
    seeded = firm_item(create_firm(parse_firm_creation({"name": "Example"})))
    assert "createdBy" not in seeded
    assert "createdByEmail" not in seeded
    assert firm_from_item(seeded).created_by is None
    assert firm_from_item(seeded).created_by_email is None


def test_a_provisioned_firm_round_trips_with_its_provenance():
    original = create_firm(
        parse_firm_creation({"name": "Example"}),
        created_by=ALICE,
        created_by_email="staff@example.test",
    )
    item = firm_item(original)
    assert item["createdBy"] == ALICE
    assert firm_from_item(item) == original


def test_firm_json_carries_explicit_nulls_for_unknown_provenance():
    """null tells a JSON consumer "nobody recorded" (a seeded firm); an absent
    key would read as "this API version does not carry the field"."""
    payload = firm_json(create_firm(parse_firm_creation({"name": "Example"})))
    assert payload["createdBy"] is None
    assert payload["createdByEmail"] is None


def test_suspending_a_firm_changes_status_and_refreshes_updated_at():
    firm = create_firm(parse_firm_creation({"name": "Example"}))
    suspended = set_firm_status(firm, "suspended")
    assert suspended.status == "suspended"
    assert suspended.updated_at >= firm.updated_at
    assert suspended.id == firm.id


def test_resuspending_is_idempotent_rather_than_an_error():
    """The portal cannot tell whether its first request landed, so the second
    must succeed — same rule CaseStore.assign states."""
    firm = set_firm_status(
        create_firm(parse_firm_creation({"name": "Example"})), "suspended"
    )
    again = set_firm_status(firm, "suspended")
    assert again.status == "suspended"


def test_an_unknown_status_is_refused_before_it_can_be_stored():
    """Accessor resolution compares this value on every authenticated request;
    a typo'd status must fail here, not fail open or closed at resolution."""
    firm = create_firm(parse_firm_creation({"name": "Example"}))
    with pytest.raises(ValidationError):
        set_firm_status(firm, "disabled")
