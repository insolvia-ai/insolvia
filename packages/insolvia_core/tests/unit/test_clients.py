"""The client binding (ADR 0023): what an invitation may say, which bindings a
new one narrows, and the item shapes both stores write.

Every identifier below is obviously fake. This repo is public.
"""

from __future__ import annotations

import pytest
from insolvia_core.access_log import access_item, record_access
from insolvia_core.auth import (
    AuthenticationError,
    AuthFailureReason,
    portal_settings_or_raise,
)
from insolvia_core.candidates import ORIGIN_CHANNELS
from insolvia_core.clients import (
    ACTIVE,
    INVITED,
    REVOKED,
    ClientBinding,
    activate,
    binding_from_item,
    binding_item,
    create_binding,
    mirror_item,
    parse_invitation,
    plan_binding,
    revoke,
)
from insolvia_core.errors import ConflictError, FieldValidationError, ValidationError
from insolvia_core.firms import (
    CLIENT_PORTAL,
    FEATURES,
    HIDDEN,
    ROLES,
    default_permissions,
)

FIRM = "00000000-0000-4000-8000-00000000f18a"
CASE = "00000000-0000-4000-8000-0000000ca5e1"
OTHER_CASE = "00000000-0000-4000-8000-0000000ca5e2"
STAFF = "00000000-0000-4000-8000-00000000a11c"
PAT = "00000000-0000-4000-8000-0000000000a1"
SAM = "00000000-0000-4000-8000-0000000000a2"


def binding(
    subject: str = PAT,
    roles: tuple[str, ...] = ("debtor_1",),
    status: str = INVITED,
    case_id: str = CASE,
) -> ClientBinding:
    return ClientBinding(
        firm_id=FIRM,
        case_id=case_id,
        subject=subject,
        email=f"{subject[-2:]}@example.test",
        display_name="Pat Example",
        roles=roles,
        status=status,
        invited_by=STAFF,
        created_at="2026-09-26T00:00:00.000000Z",
        updated_at="2026-09-26T00:00:00.000000Z",
    )


# ── The invitation body ─────────────────────────────────────────


def test_an_invitation_defaults_to_the_first_debtor_and_lowercases_the_address():
    draft = parse_invitation({"email": "Pat@Example.TEST", "displayName": " Pat "})

    assert (draft.email, draft.display_name, draft.roles) == (
        "pat@example.test",
        "Pat",
        ("debtor_1",),
    )


def test_both_roles_are_stored_in_canonical_order():
    draft = parse_invitation(
        {
            "email": "pat@example.test",
            "displayName": "Pat",
            "roles": ["debtor_2", "debtor_1"],
        }
    )

    assert draft.roles == ("debtor_1", "debtor_2")


@pytest.mark.parametrize(
    "roles",
    [[], ["non_filing_spouse"], ["debtor_3"], ["debtor_1", "debtor_1"], "debtor_1"],
)
def test_roles_must_be_a_nonempty_set_of_the_two_debtors(roles):
    with pytest.raises(FieldValidationError) as refused:
        parse_invitation(
            {"email": "pat@example.test", "displayName": "Pat", "roles": roles}
        )

    assert "roles" in refused.value.fields


def test_a_body_naming_the_case_or_subject_is_refused_not_ignored():
    with pytest.raises(ValidationError):
        parse_invitation(
            {"email": "pat@example.test", "displayName": "Pat", "caseId": OTHER_CASE}
        )


def test_a_missing_name_and_address_are_both_reported():
    with pytest.raises(FieldValidationError) as refused:
        parse_invitation({})

    assert set(refused.value.fields) == {"email", "displayName"}


# ── Planning a binding ──────────────────────────────────────────


def test_a_subject_with_a_live_binding_cannot_be_bound_again():
    with pytest.raises(ConflictError):
        plan_binding(
            binding(case_id=OTHER_CASE),
            previous=binding(status=ACTIVE),
            on_case=(),
        )


def test_a_revoked_subject_may_be_bound_again_a_refiled_case():
    assert (
        plan_binding(
            binding(case_id=OTHER_CASE), previous=binding(status=REVOKED), on_case=()
        )
        == ()
    )


def test_inviting_the_second_spouse_narrows_the_first_binding():
    first = binding(PAT, roles=("debtor_1", "debtor_2"), status=ACTIVE)
    second = binding(SAM, roles=("debtor_2",))

    narrowed = plan_binding(second, previous=None, on_case=(first,))

    assert [(b.subject, b.roles) for b in narrowed] == [(PAT, ("debtor_1",))]


def test_a_narrowing_that_would_leave_a_binding_with_no_role_is_refused():
    """Two bindings can never both hold debtor_2 — and taking the only role
    another binding has would be a revocation nobody asked for."""
    first = binding(PAT, roles=("debtor_2",), status=ACTIVE)

    with pytest.raises(ConflictError):
        plan_binding(binding(SAM, roles=("debtor_2",)), previous=None, on_case=(first,))


def test_revoked_bindings_on_the_case_hold_nothing():
    old = binding(PAT, roles=("debtor_2",), status=REVOKED)

    assert (
        plan_binding(binding(SAM, roles=("debtor_2",)), previous=None, on_case=(old,))
        == ()
    )


# ── Transitions ─────────────────────────────────────────────────


def test_activation_moves_only_an_invitation():
    assert activate(binding()).status == ACTIVE
    assert activate(binding(status=REVOKED)).status == REVOKED


def test_revoking_twice_is_a_conflict():
    with pytest.raises(ConflictError):
        revoke(binding(status=REVOKED))


def test_create_binding_starts_invited():
    draft = parse_invitation({"email": "pat@example.test", "displayName": "Pat"})

    made = create_binding(
        draft, firm_id=FIRM, case_id=CASE, subject=PAT, invited_by=STAFF
    )

    assert (made.status, made.invited_by, made.case_id) == (INVITED, STAFF, CASE)


# ── Item shapes ─────────────────────────────────────────────────


def test_the_binding_resolves_under_a_client_key_never_a_firm_user_key():
    """FirmStore.find_user queries GSI1PK = USER#<subject>. A client's row
    under the same key would be parsed as a firm user when the debtor signs
    in to the STAFF app — so it lives under CLIENT#<subject> instead."""
    item = binding_item(binding())

    assert item["GSI1PK"] == f"CLIENT#{PAT}"
    assert item["PK"] == f"FIRM#{FIRM}"
    assert item["SK"] == f"CLIENT#{PAT}"


def test_the_mirror_carries_no_index_keys():
    item = mirror_item(binding())

    assert item["PK"] == f"CASE#{CASE}"
    assert not {key for key in item if key.startswith("GSI")}


def test_an_item_round_trips():
    original = binding(roles=("debtor_1", "debtor_2"), status=ACTIVE)

    assert binding_from_item(binding_item(original)) == original


def test_a_row_this_module_did_not_write_raises():
    item = binding_item(binding())
    del item["caseId"]

    with pytest.raises(ValidationError):
        binding_from_item(item)


# ── The neighbours ADR 0023 touches ─────────────────────────────


@pytest.mark.parametrize("role", ROLES)
def test_client_portal_is_a_feature_hidden_from_every_role_by_default(role):
    assert CLIENT_PORTAL in FEATURES
    assert default_permissions(role)[CLIENT_PORTAL] == HIDDEN


def test_client_is_an_origin_channel():
    assert "client" in ORIGIN_CHANNELS


def test_an_invitation_row_records_its_roles_and_other_rows_do_not():
    invite = access_item(
        record_access(
            case_id=CASE,
            principal=STAFF,
            action="client.invite",
            roles=("debtor_1", "debtor_2"),
        )
    )
    read = access_item(record_access(case_id=CASE, principal=PAT, action="portal.read"))

    assert invite["roles"] == "debtor_1,debtor_2"
    assert "roles" not in read


# ── The portal verify profile ───────────────────────────────────


@pytest.mark.parametrize(
    ("portal", "staff"),
    [(None, "staff-client"), ("portal-client", None), ("same", "same")],
)
def test_the_portal_profile_refuses_to_exist_unless_disjoint_from_staff(portal, staff):
    with pytest.raises(AuthenticationError) as refused:
        portal_settings_or_raise(
            "https://issuer.example.test", portal, staff_client_id=staff
        )

    assert refused.value.reason is AuthFailureReason.NOT_CONFIGURED


def test_the_portal_profile_verifies_the_portal_client():
    settings = portal_settings_or_raise(
        "https://issuer.example.test/", "portal-client", staff_client_id="staff-client"
    )

    assert (settings.issuer_url, settings.client_id) == (
        "https://issuer.example.test",
        "portal-client",
    )
