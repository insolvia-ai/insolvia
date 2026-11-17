"""CaseEntityStore over Postgres (ADR 0030): every case collection in one
`case_entities` table, `kind` being the collection's `EntityKind.sk_prefix`."""

from __future__ import annotations

from typing import Any

from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.case_entities import (
    CaseEntity,
    EntityKind,
    entity_from_item,
    entity_item,
    list_order,
)


def _key(case_id: str, kind: EntityKind[Any], entity_id: str) -> dict[str, str]:
    return {"case_id": case_id, "kind": kind.sk_prefix, "id": entity_id}


class PostgresCaseEntityStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "case_entities", ("case_id", "kind", "id"))

    def create(self, entity: CaseEntity[Any]) -> None:
        self.rows.create(
            _key(entity.case_id, entity.kind, entity.id), entity_item(entity)
        )

    def get(
        self, case_id: str, kind: EntityKind[Any], entity_id: str
    ) -> CaseEntity[Any] | None:
        item = self.rows.get(_key(case_id, kind, entity_id))
        return None if item is None else entity_from_item(kind, item)

    def put(self, entity: CaseEntity[Any]) -> bool:
        return self.rows.replace(
            _key(entity.case_id, entity.kind, entity.id), entity_item(entity)
        )

    def delete(self, case_id: str, kind: EntityKind[Any], entity_id: str) -> bool:
        return self.rows.delete(_key(case_id, kind, entity_id))

    def list_for_case(
        self, case_id: str, kind: EntityKind[Any]
    ) -> tuple[CaseEntity[Any], ...]:
        items = self.rows.list({"case_id": case_id, "kind": kind.sk_prefix})
        return tuple(
            sorted((entity_from_item(kind, item) for item in items), key=list_order)
        )
