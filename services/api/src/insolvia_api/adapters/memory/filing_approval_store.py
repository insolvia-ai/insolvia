from __future__ import annotations

from dataclasses import replace

from insolvia_core.errors import ConflictError

from insolvia_api.core.filing_approval import FilingApproval


class MemoryFilingApprovalStore:
    """Ephemeral FilingApprovalStore for tests and the plain development
    server, with the DynamoDB adapter's conditions — a test that could
    consume twice, or create past a moved pointer, would be testing a weaker
    store than production runs."""

    def __init__(self) -> None:
        self.approvals: dict[tuple[str, str], FilingApproval] = {}
        self.pointers: dict[str, str] = {}

    def current(self, case_id: str) -> FilingApproval | None:
        approval_id = self.pointers.get(case_id)
        if approval_id is None:
            return None
        return self.approvals.get((case_id, approval_id))

    def get(self, case_id: str, approval_id: str) -> FilingApproval | None:
        return self.approvals.get((case_id, approval_id))

    def create(
        self,
        approval: FilingApproval,
        *,
        replacing: FilingApproval | None,
        voided_at: str,
    ) -> None:
        key = (approval.case_id, approval.approval_id)
        if key in self.approvals:
            raise RuntimeError("approval id already exists in this case")
        expected = None if replacing is None else replacing.approval_id
        if self.pointers.get(approval.case_id) != expected:
            raise ConflictError("another approval was made first — review again")
        # Every condition first, then every write — the transaction's
        # all-or-nothing, with nothing between the writes that can fail.
        supersede = replacing is not None and replacing.status == "pending"
        if supersede and replacing is not None:
            old_key = (approval.case_id, replacing.approval_id)
            stored = self.approvals.get(old_key)
            if stored is None or stored.status != "pending":
                raise ConflictError("the current approval changed — review again")
            self.approvals[old_key] = replace(
                stored, status="voided", voided_at=voided_at, void_reason="superseded"
            )
        self.approvals[key] = approval
        self.pointers[approval.case_id] = approval.approval_id

    def consume(
        self,
        case_id: str,
        approval_id: str,
        *,
        digest: str,
        now: int,
        consumed_at: str,
    ) -> bool:
        stored = self.approvals.get((case_id, approval_id))
        if (
            stored is None
            or stored.status != "pending"
            or stored.digest != digest
            or not stored.expires_at > now
        ):
            return False
        self.approvals[(case_id, approval_id)] = replace(
            stored, status="consumed", consumed_at=consumed_at
        )
        return True

    def void(
        self, case_id: str, approval_id: str, *, reason: str, voided_at: str
    ) -> bool:
        stored = self.approvals.get((case_id, approval_id))
        if stored is None or stored.status != "pending":
            return False
        self.approvals[(case_id, approval_id)] = replace(
            stored, status="voided", voided_at=voided_at, void_reason=reason
        )
        return True
