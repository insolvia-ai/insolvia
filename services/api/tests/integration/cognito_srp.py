"""Sign in to a Cognito user pool headlessly, with SRP and nothing else.

WHY SRP, WHEN A PASSWORD FLOW WOULD BE THREE LINES. The web app client
(infra/modules/auth/main.tf) allows exactly one explicit flow,
`ALLOW_USER_SRP_AUTH`, and says why in as many words: "nothing should ever
ship a raw password through this client". A test tier that turned
`ALLOW_USER_PASSWORD_AUTH` on to make its own life easier would be loosening a
security decision on every pool it runs against — staging included — for a
convenience. SRP is the flow the client already permits, so this speaks it.

WHY NOT A LIBRARY. `pycognito` and `warrant` implement this in ~300 lines each
and drag in a client library the API's Lambda image must never carry. This is
the same handshake in the standard library plus the boto3 the service already
pins — and because the two Cognito calls involved (`InitiateAuth`,
`RespondToAuthChallenge`) are UNSIGNED operations, no AWS credentials are
needed or read. That last property is load-bearing: it is what lets the
suite run from a CI job holding nothing but a pool id, a client id and a
password, and it is what makes every subsequent API call exercise the
deployed Lambda's OWN execution role rather than a developer's.

The arithmetic is the SRP-6a variant Cognito speaks (RFC 5054 group 15, the
3072-bit prime; Cognito's "Caldera Derived Key" HKDF label; a timestamp in
Cognito's own format). Every constant below is Cognito's, not ours.

NEVER LOG THE PASSWORD. It arrives as an argument, is hashed once, and goes
nowhere else. Errors name the failure, never the credential.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import secrets
from datetime import UTC, datetime

import boto3
from botocore import UNSIGNED
from botocore.config import Config

# RFC 5054, 3072-bit group. Cognito's SRP uses this N and g = 2.
_N_HEX = (
    "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74"
    "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437"
    "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED"
    "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05"
    "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB"
    "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B"
    "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718"
    "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33"
    "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7"
    "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864"
    "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2"
    "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAF"
)
_G_HEX = "2"
_INFO_BITS = b"Caldera Derived Key"

_WEEKDAYS = ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
_MONTHS = (
    "Jan", "Feb", "Mar", "Apr", "May", "Jun",
    "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
)  # fmt: skip


class SignInError(Exception):
    """The pool refused, or answered with something this handshake does not
    understand. The message never carries the password."""


def _hex_to_long(value: str) -> int:
    return int(value, 16)


def _long_to_hex(value: int) -> str:
    return f"{value:x}"


def _hash_sha256(buf: bytes) -> str:
    digest = hashlib.sha256(buf).hexdigest()
    return (64 - len(digest)) * "0" + digest


def _hex_hash(hex_string: str) -> str:
    return _hash_sha256(bytearray.fromhex(hex_string))


def _pad_hex(value: int | str) -> str:
    """Cognito's byte-padding rule: even length, and a leading zero byte when
    the top bit is set so the number is not read as negative."""
    hex_string = value if isinstance(value, str) else _long_to_hex(value)
    if len(hex_string) % 2 == 1:
        hex_string = "0" + hex_string
    elif hex_string[0] in "89ABCDEFabcdef":
        hex_string = "00" + hex_string
    return hex_string


def _hkdf(ikm: bytes, salt: bytes) -> bytes:
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    return hmac.new(prk, _INFO_BITS + b"\x01", hashlib.sha256).digest()[:16]


def _cognito_timestamp(now: datetime) -> str:
    # Day of month UNPADDED — Cognito's format, verified rather than guessed;
    # a zero-padded day makes the signature fail with "Incorrect username or
    # password", which is the least helpful error the pool has.
    return (
        f"{_WEEKDAYS[now.weekday()]} {_MONTHS[now.month - 1]} {now.day:d} "
        f"{now.hour:02d}:{now.minute:02d}:{now.second:02d} UTC {now.year:d}"
    )


class _Srp:
    def __init__(self, pool_id: str) -> None:
        self.big_n = _hex_to_long(_N_HEX)
        self.g = _hex_to_long(_G_HEX)
        self.k = _hex_to_long(_hex_hash("00" + _N_HEX + "0" + _G_HEX))
        self.pool_name = pool_id.split("_", 1)[1]
        self.small_a = self._small_a()
        self.large_a = pow(self.g, self.small_a, self.big_n)
        while self.large_a % self.big_n == 0:
            self.small_a = self._small_a()
            self.large_a = pow(self.g, self.small_a, self.big_n)

    def _small_a(self) -> int:
        return (
            _hex_to_long(binascii.hexlify(secrets.token_bytes(128)).decode())
            % self.big_n
        )

    def _password_key(
        self, user_id: str, password: str, server_b: int, salt: str
    ) -> bytes:
        u = _hex_to_long(_hex_hash(_pad_hex(self.large_a) + _pad_hex(server_b)))
        if u == 0:
            raise SignInError("SRP: the server's public value hashed to zero")
        identity = f"{self.pool_name}{user_id}:{password}".encode()
        x = _hex_to_long(_hex_hash(_pad_hex(salt) + _hash_sha256(identity)))
        base = server_b - self.k * pow(self.g, x, self.big_n)
        s = pow(base, self.small_a + u * x, self.big_n)
        return _hkdf(
            bytearray.fromhex(_pad_hex(s)),
            bytearray.fromhex(_pad_hex(_long_to_hex(u))),
        )

    def challenge_response(
        self, params: dict[str, str], password: str, now: datetime | None = None
    ) -> dict[str, str]:
        try:
            user_id = params["USER_ID_FOR_SRP"]
            salt = params["SALT"]
            server_b = _hex_to_long(params["SRP_B"])
            secret_block = params["SECRET_BLOCK"]
        except KeyError as missing:
            raise SignInError(
                f"SRP: the pool's challenge lacks {missing} — is USER_SRP_AUTH "
                "enabled on this app client?"
            ) from None
        timestamp = _cognito_timestamp(now or datetime.now(UTC))
        key = self._password_key(user_id, password, server_b, salt)
        message = (
            self.pool_name.encode()
            + user_id.encode()
            + base64.standard_b64decode(secret_block)
            + timestamp.encode()
        )
        signature = base64.standard_b64encode(
            hmac.new(key, message, hashlib.sha256).digest()
        ).decode()
        return {
            "TIMESTAMP": timestamp,
            "USERNAME": user_id,
            "PASSWORD_CLAIM_SECRET_BLOCK": secret_block,
            "PASSWORD_CLAIM_SIGNATURE": signature,
        }


def sign_in(
    *, pool_id: str, client_id: str, email: str, password: str
) -> dict[str, str]:
    """Complete the SRP handshake and return Cognito's `AuthenticationResult`.

    The result carries `AccessToken` (what the API verifies), `IdToken` (what
    the app shows the email from) and `RefreshToken`. Callers want the first.
    """
    region = pool_id.split("_", 1)[0]
    client = boto3.client(
        "cognito-idp",
        region_name=region,
        config=Config(signature_version=UNSIGNED),
    )
    srp = _Srp(pool_id)
    try:
        opened = client.initiate_auth(
            AuthFlow="USER_SRP_AUTH",
            AuthParameters={"USERNAME": email, "SRP_A": _long_to_hex(srp.large_a)},
            ClientId=client_id,
        )
    except client.exceptions.NotAuthorizedException as refusal:
        raise SignInError(
            "the pool refused the sign-in (NotAuthorized). With "
            "prevent_user_existence_errors on, a wrong password and a missing "
            "account look identical — check the seed step ran for this target."
        ) from refusal

    if opened.get("ChallengeName") != "PASSWORD_VERIFIER":
        raise SignInError(
            "expected a PASSWORD_VERIFIER challenge, got "
            f"{opened.get('ChallengeName')!r}"
        )
    try:
        answered = client.respond_to_auth_challenge(
            ClientId=client_id,
            ChallengeName="PASSWORD_VERIFIER",
            ChallengeResponses=srp.challenge_response(
                opened["ChallengeParameters"], password
            ),
        )
    except client.exceptions.NotAuthorizedException as refusal:
        raise SignInError(
            "the pool rejected the password proof (NotAuthorized): wrong "
            "password, or the account is not CONFIRMED."
        ) from refusal

    challenge = answered.get("ChallengeName")
    if challenge:
        # NEW_PASSWORD_REQUIRED is the one a seeded account can present when
        # the seeder's permanent-password step did not run; MFA challenges
        # mean somebody enrolled a factor on a test account, which the
        # staging runbook forbids.
        raise SignInError(
            f"the pool wants a further challenge ({challenge}); a seeded test "
            "account must sign in without one."
        )
    result = answered.get("AuthenticationResult")
    if not result or "AccessToken" not in result:
        raise SignInError("the pool returned no AuthenticationResult")
    return dict(result)
