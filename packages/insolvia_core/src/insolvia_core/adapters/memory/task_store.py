from __future__ import annotations

from insolvia_core.tasks import Task, list_order


class MemoryTaskStore:
    """Ephemeral TaskStore for tests and the plain development server.

    Keyed by (case_id, task_id) — the DynamoDB adapter's PK and SK — so "a
    task id only resolves inside its own case" is a property of this dict
    rather than something every caller has to remember, exactly as
    MemoryDocumentStore states for the same reason.
    """

    def __init__(self) -> None:
        self.tasks: dict[tuple[str, str], Task] = {}

    def create(self, task: Task) -> None:
        key = (task.case_id, task.id)
        if key in self.tasks:
            raise RuntimeError(f"task {key} already exists")
        self.tasks[key] = task

    def get(self, case_id: str, task_id: str) -> Task | None:
        return self.tasks.get((case_id, task_id))

    def update(self, task: Task) -> Task | None:
        key = (task.case_id, task.id)
        if key not in self.tasks:
            return None
        self.tasks[key] = task
        return task

    def list_for_case(self, case_id: str) -> tuple[Task, ...]:
        return tuple(
            sorted(
                (
                    task
                    for (stored_case_id, _), task in self.tasks.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
            )
        )

    def delete(self, case_id: str, task_id: str) -> bool:
        return self.tasks.pop((case_id, task_id), None) is not None
