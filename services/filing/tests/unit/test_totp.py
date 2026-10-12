"""TOTP against RFC 6238's own SHA-1 test vectors, and against the fake
court's independent implementation."""

from __future__ import annotations

import base64

import pytest
from fake_cmecf.server import _totp
from insolvia_filing.core.totp import totp_at

# RFC 6238 Appendix B: the SHA-1 seed is the ASCII "12345678901234567890";
# the vectors are eight digits, PACER's codes are the last six.
RFC_SEED = base64.b32encode(b"12345678901234567890").decode()


@pytest.mark.parametrize(
    ("moment", "eight_digits"),
    [
        (59, "94287082"),
        (1111111109, "07081804"),
        (1111111111, "14050471"),
        (1234567890, "89005924"),
        (2000000000, "69279037"),
        (20000000000, "65353130"),
    ],
)
def test_the_rfc_6238_vectors(moment, eight_digits):
    assert totp_at(RFC_SEED, moment) == eight_digits[-6:]


def test_the_worker_and_the_fake_court_agree():
    seed = base64.b32encode(b"another-fake-seed-20").decode()
    for moment in range(0, 3000, 30):
        assert totp_at(seed, moment) == _totp(seed, moment // 30)


def test_a_seed_written_lowercase_or_spaced_reads_the_same():
    assert totp_at(RFC_SEED.lower(), 59) == totp_at(RFC_SEED, 59)
