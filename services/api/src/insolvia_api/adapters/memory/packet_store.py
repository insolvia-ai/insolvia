from __future__ import annotations

from insolvia_api.adapters.memory.case_store import MemoryCaseStore
from insolvia_api.core.cases import Case
from insolvia_api.core.packets import Packet, list_order


class MemoryPacketStore:
    """Ephemeral PacketStore for tests and the plain development server.

    Takes the MemoryCaseStore it shares a "table" with, because `create` is a
    transaction over both: the packet record and the case's pins land
    together or not at all, with the same conditions the DynamoDB adapter
    expresses — a test that could pin a filed case, or pin without storing
    the packet, would be testing a weaker store than production runs.
    """

    def __init__(self, case_store: MemoryCaseStore) -> None:
        self.case_store = case_store
        self.packets: dict[tuple[str, str], Packet] = {}

    def create(
        self, packet: Packet, *, pinned_case: Case, expected_updated_at: str
    ) -> bool:
        key = (packet.case_id, packet.id)
        if key in self.packets:
            raise RuntimeError("packet id already exists in this case")
        stored_case = self.case_store.cases.get(pinned_case.id)
        if (
            stored_case is None
            or stored_case.updated_at != expected_updated_at
            or stored_case.status == "filed"
        ):
            return False
        # Both, together — nothing can fail between these lines, which is the
        # property the DynamoDB adapter buys with TransactWriteItems.
        self.packets[key] = packet
        self.case_store.cases[pinned_case.id] = pinned_case
        return True

    def get(self, case_id: str, packet_id: str) -> Packet | None:
        return self.packets.get((case_id, packet_id))

    def list_for_case(self, case_id: str) -> tuple[Packet, ...]:
        return tuple(
            sorted(
                (
                    packet
                    for (stored_case_id, _), packet in self.packets.items()
                    if stored_case_id == case_id
                ),
                key=list_order,
                reverse=True,
            )
        )
