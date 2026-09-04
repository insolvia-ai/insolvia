from __future__ import annotations

from typing import Any

from insolvia_core.case_entities import CaseEntity, EntityKind, list_order


class MemoryCaseEntityStore:
    """Ephemeral CaseEntityStore for tests and the plain development server.

    Keyed by (case_id, sk_prefix, id) — the DynamoDB adapter's PK and SK split
    apart — so the case scope and the kind scope are properties of this dict
    rather than something every caller has to remember, exactly as they are
    properties of the table's key schema on the other side.
    """

    def __init__(self) -> None:
        self.entities: dict[tuple[str, str, str], CaseEntity[Any]] = {}

    def _key(self, entity: CaseEntity[Any]) -> tuple[str, str, str]:
        return (entity.case_id, entity.kind.sk_prefix, entity.id)

    def create(self, entity: CaseEntity[Any]) -> None:
        key = self._key(entity)
        if key in self.entities:
            # The Protocol's contract: an existing (case, kind, id) means the
            # server's id minting is broken, and replacing would erase a record
            # to hide it.
            raise RuntimeError("entity id already exists in this case")
        self.entities[key] = entity

    def get(
        self, case_id: str, kind: EntityKind[Any], entity_id: str
    ) -> CaseEntity[Any] | None:
        return self.entities.get((case_id, kind.sk_prefix, entity_id))

    def put(self, entity: CaseEntity[Any]) -> bool:
        key = self._key(entity)
        if key not in self.entities:
            return False
        self.entities[key] = entity
        return True

    def delete(self, case_id: str, kind: EntityKind[Any], entity_id: str) -> bool:
        return self.entities.pop((case_id, kind.sk_prefix, entity_id), None) is not None

    def list_for_case(
        self, case_id: str, kind: EntityKind[Any]
    ) -> tuple[CaseEntity[Any], ...]:
        return tuple(
            sorted(
                (
                    entity
                    for (
                        stored_case_id,
                        stored_prefix,
                        _,
                    ), entity in self.entities.items()
                    if stored_case_id == case_id and stored_prefix == kind.sk_prefix
                ),
                key=list_order,
            )
        )
