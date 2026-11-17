"""TaskStore over Postgres (ADR 0030): the `tasks` table."""

from __future__ import annotations

from insolvia_core.adapters.postgres.children import ChildTable
from insolvia_core.tasks import Task, list_order, task_from_item, task_item


class PostgresTaskStore:
    def __init__(self, database_url: str) -> None:
        self.rows = ChildTable(database_url, "tasks", ("case_id", "id"))

    def create(self, task: Task) -> None:
        self.rows.create({"case_id": task.case_id, "id": task.id}, task_item(task))

    def get(self, case_id: str, task_id: str) -> Task | None:
        item = self.rows.get({"case_id": case_id, "id": task_id})
        return None if item is None else task_from_item(item)

    def update(self, task: Task) -> Task | None:
        key = {"case_id": task.case_id, "id": task.id}
        return task if self.rows.replace(key, task_item(task)) else None

    def list_for_case(self, case_id: str) -> tuple[Task, ...]:
        items = self.rows.list({"case_id": case_id})
        return tuple(sorted(map(task_from_item, items), key=list_order))

    def delete(self, case_id: str, task_id: str) -> bool:
        return self.rows.delete({"case_id": case_id, "id": task_id})
