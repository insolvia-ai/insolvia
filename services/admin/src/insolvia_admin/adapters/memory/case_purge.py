"""`CaseTableRows` over two dicts of items — the tables as their raw rows, so
tests can plant exactly the shapes `insolvia_core`'s item functions write and
watch exactly which ones go. Mirrors the DynamoDB adapter's two conditions."""

from __future__ import annotations

from collections.abc import Iterator, Mapping

from insolvia_admin.core.pre_client_purge import (
    CASE_PREFIX,
    DEBTOR_PREFIX,
    BindingRow,
)


class MemoryCaseTableRows:
    def __init__(self) -> None:
        self.cases: dict[tuple[str, str], dict[str, object]] = {}
        self.firms: dict[tuple[str, str], dict[str, object]] = {}

    def put_case_item(self, item: Mapping[str, object]) -> None:
        self.cases[(str(item["PK"]), str(item["SK"]))] = dict(item)

    def put_firm_item(self, item: Mapping[str, object]) -> None:
        self.firms[(str(item["PK"]), str(item["SK"]))] = dict(item)

    def summaries(self) -> Iterator[Mapping[str, object]]:
        for item in list(self.cases.values()):
            yield {k: item[k] for k in ("PK", "SK", "clientId") if k in item}

    def partition(self, case_id: str) -> list[Mapping[str, object]]:
        pk = f"{CASE_PREFIX}{case_id}"
        return [dict(item) for (p, _), item in self.cases.items() if p == pk]

    def delete(self, item: Mapping[str, object]) -> bool:
        key = (str(item["PK"]), str(item["SK"]))
        stored = self.cases.get(key)
        if (
            key[1].startswith(DEBTOR_PREFIX)
            and stored is not None
            and stored.get("clientId")
        ):
            return False
        self.cases.pop(key, None)
        return True

    def delete_binding(self, row: BindingRow) -> bool:
        key = (f"FIRM#{row.firm_id}", f"CLIENT#{row.subject}")
        stored = self.firms.get(key)
        if stored is None or stored.get("caseId") != row.case_id:
            return False
        del self.firms[key]
        return True
