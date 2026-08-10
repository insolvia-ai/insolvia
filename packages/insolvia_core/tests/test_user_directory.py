"""The memory fake's duplicate detection matches the real pool's.

The pool is case-insensitive (`username_configuration { case_sensitive =
false }`, issue #179), so the fake must refuse `A@X.TEST` when `a@x.test`
exists. An exact-match fake would be weaker than production and let a suite
pass on code the real pool refuses.
"""

import pytest
from insolvia_core.adapters.memory.user_directory import MemoryUserDirectory
from insolvia_core.errors import ConflictError


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
