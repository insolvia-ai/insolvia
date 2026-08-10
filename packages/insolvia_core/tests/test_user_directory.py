"""The memory fake's duplicate detection matches the real pool's.

The pool is case-insensitive (`username_configuration { case_sensitive =
false }`, issue #179), so the fake must refuse `A@X.TEST` when `a@x.test`
exists. An exact-match fake would be weaker than production and let a suite
pass on code the real pool refuses.
"""

import pytest
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.errors import ConflictError, NotFoundError


@pytest.mark.parametrize(
    "duplicate",
    ["taken@example.test", "TAKEN@EXAMPLE.TEST", "Taken@Example.Test"],
)
def test_a_duplicate_address_is_refused_in_any_casing(duplicate):
    directory = MemoryUserDirectory()
    directory.create_user("taken@example.test")
    with pytest.raises(ConflictError):
        directory.create_user(duplicate)


def test_distinct_addresses_each_get_their_own_subject():
    directory = MemoryUserDirectory()
    first = directory.create_user("a@example.test")
    second = directory.create_user("b@example.test")
    assert first != second
    assert set(directory.subjects) == {"a@example.test", "b@example.test"}


def test_resending_to_an_unknown_address_is_not_found():
    with pytest.raises(NotFoundError):
        MemoryUserDirectory().resend_invite("nobody@example.test")


def test_resending_to_an_uncompleted_invite_records_the_resend():
    directory = MemoryUserDirectory()
    directory.create_user("invited@example.test")
    directory.resend_invite("invited@example.test")
    assert directory.resent == ["invited@example.test"]


def test_resending_to_a_confirmed_user_is_refused():
    """Cognito refuses RESEND for a CONFIRMED user (UnsupportedUserState); the
    fake must be exactly as strict, or a route test would pass on a resend the
    real pool rejects — forgot-password owns that user's way back in."""
    directory = MemoryUserDirectory()
    directory.create_user("active@example.test")
    directory.confirmed.add("active@example.test")
    with pytest.raises(ConflictError):
        directory.resend_invite("active@example.test")
