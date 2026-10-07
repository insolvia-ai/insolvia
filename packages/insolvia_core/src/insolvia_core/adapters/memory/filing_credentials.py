"""The credential vault in memory — for tests and the bare development
server, which has no real table to protect.

The sealer and opener are the tax-id local cipher's two halves, kept apart
the way the AWS adapters are: the API side composes only the sealer, so a
unit test of the routes is a test of a server that CANNOT open what it
stores, exactly as the deployed one cannot.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import replace

from insolvia_core.adapters.memory.tax_id_cipher import LocalTaxIdCipher
from insolvia_core.errors import ConflictError
from insolvia_core.filing_authorization import FilingAuthorization
from insolvia_core.filing_credentials import FilingCredential
from insolvia_core.tax_ids import Envelope


class LocalCredentialSealer:
    """FilingCredentialSealer with the KMS call replaced by a local wrap
    (adapters/memory/tax_id_cipher — the same envelope code path)."""

    def __init__(self) -> None:
        self._cipher = LocalTaxIdCipher()

    def seal(self, plaintext: str, *, context: Mapping[str, str]) -> Envelope:
        return self._cipher.seal(plaintext, context=context)


class LocalCredentialOpener:
    """FilingCredentialOpener, the local wrap's inverse. Refuses any context
    but the sealing one (the GCM tag), as KMS does."""

    def __init__(self) -> None:
        self._cipher = LocalTaxIdCipher()

    def open(self, envelope: Envelope, *, context: Mapping[str, str]) -> str:
        return self._cipher.open(envelope, context=context)


class MemoryFilingCredentialStore:
    def __init__(self) -> None:
        self.items: dict[tuple[str, str, str], FilingCredential] = {}

    def create(self, credential: FilingCredential) -> None:
        key = (credential.firm_id, credential.attorney_id, credential.credential_id)
        if key in self.items:
            raise RuntimeError("credential id already exists")
        self.items[key] = credential

    def get(
        self, firm_id: str, attorney_id: str, credential_id: str
    ) -> FilingCredential | None:
        return self.items.get((firm_id, attorney_id, credential_id))

    def list_for_attorney(
        self, firm_id: str, attorney_id: str
    ) -> tuple[FilingCredential, ...]:
        return tuple(
            sorted(
                (
                    c
                    for (f, a, _), c in self.items.items()
                    if f == firm_id and a == attorney_id
                ),
                key=lambda c: (c.created_at, c.credential_id),
            )
        )

    def delete(self, firm_id: str, attorney_id: str, credential_id: str) -> bool:
        return self.items.pop((firm_id, attorney_id, credential_id), None) is not None


class MemoryFilingAuthorizationStore:
    """FilingAuthorizationStore in memory: the current item per attorney,
    and the history keyed by signature id — the two item kinds the
    DynamoDB adapter writes."""

    def __init__(self) -> None:
        self.current: dict[tuple[str, str], FilingAuthorization] = {}
        self.history: dict[str, FilingAuthorization] = {}

    def get_current(self, firm_id: str, attorney_id: str) -> FilingAuthorization | None:
        return self.current.get((firm_id, attorney_id))

    def put_current(
        self,
        authorization: FilingAuthorization,
        *,
        replacing: FilingAuthorization | None,
    ) -> None:
        key = (authorization.firm_id, authorization.attorney_id)
        held = self.current.get(key)
        held_id = held.authorization_id if held is not None else None
        wanted_id = replacing.authorization_id if replacing is not None else None
        if held_id != wanted_id:
            raise ConflictError("the authorization changed while signing")
        self.current[key] = authorization
        self.history[authorization.authorization_id] = authorization
        if replacing is not None:
            self.history[replacing.authorization_id] = replace(
                replacing, status="superseded"
            )

    def withdraw(
        self, authorization: FilingAuthorization, *, withdrawn_at: str
    ) -> bool:
        key = (authorization.firm_id, authorization.attorney_id)
        held = self.current.get(key)
        if held is None or held.authorization_id != authorization.authorization_id:
            return False
        del self.current[key]
        self.history[authorization.authorization_id] = replace(
            authorization, status="withdrawn", withdrawn_at=withdrawn_at
        )
        return True
