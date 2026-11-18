from __future__ import annotations

import pytest
from insolvia_core.adapters.development_keys import development_key_adapters


@pytest.mark.parametrize("environment", ["staging", "production", "", "Local"])
def test_the_development_keys_are_refused_outside_local(environment):
    with pytest.raises(RuntimeError, match="refused outside INSOLVIA_ENV=local"):
        development_key_adapters(environment)


def test_locally_they_seal_and_open_through_the_envelope():
    cipher, sealer = development_key_adapters("local")
    context = {"firmId": "f", "ref": "r"}
    assert cipher.open(
        cipher.seal("123-45-6789", context=context), context=context
    ) == ("123-45-6789")
    assert sealer.seal("secret", context=context) is not None
