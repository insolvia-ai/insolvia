from __future__ import annotations

from insolvia_api.core.calendar_feed import CalendarToken
from insolvia_api.core.events import (
    Event,
    EventScope,
    list_order,
    partition_key,
    sort_instant,
)


class MemoryEventStore:
    """Ephemeral EventStore for tests and the plain development server.

    Keyed by (partition, id) — the DynamoDB adapter's PK and SK split apart
    — so a case event and a firm event of one firm live under different
    keys exactly as they do in the table, and `list_for_firm` is the same
    firm-wide sweep the index gives the real adapter.
    """

    def __init__(self) -> None:
        self.events: dict[tuple[str, str], Event] = {}

    def _key(self, event: Event) -> tuple[str, str]:
        return (partition_key(event.scope), event.id)

    def create(self, event: Event) -> None:
        key = self._key(event)
        if key in self.events:
            raise RuntimeError("event id already exists in this scope")
        self.events[key] = event

    def get(self, scope: EventScope, event_id: str) -> Event | None:
        return self.events.get((partition_key(scope), event_id))

    def put(self, event: Event) -> bool:
        key = self._key(event)
        if key not in self.events:
            return False
        self.events[key] = event
        return True

    def delete(self, scope: EventScope, event_id: str) -> bool:
        return self.events.pop((partition_key(scope), event_id), None) is not None

    def list_for_scope(self, scope: EventScope) -> tuple[Event, ...]:
        wanted = partition_key(scope)
        return tuple(
            sorted(
                (e for (pk, _), e in self.events.items() if pk == wanted),
                key=list_order,
            )
        )

    def list_for_firm(
        self, firm_id: str, *, starting_from: str, until: str
    ) -> tuple[Event, ...]:
        return tuple(
            sorted(
                (
                    e
                    for e in self.events.values()
                    if e.firm_id == firm_id
                    and starting_from <= sort_instant(e) <= until
                ),
                key=list_order,
            )
        )


class MemoryCalendarTokenStore:
    def __init__(self) -> None:
        self.tokens: dict[tuple[str, str], CalendarToken] = {}

    def put(self, token: CalendarToken) -> None:
        self.tokens[(token.firm_id, token.subject)] = token

    def get(self, firm_id: str, subject: str) -> CalendarToken | None:
        return self.tokens.get((firm_id, subject))

    def delete(self, firm_id: str, subject: str) -> bool:
        return self.tokens.pop((firm_id, subject), None) is not None
