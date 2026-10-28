"""Regenerate the Python-oracle data the .NET parity tests compare against.

The .NET engines and projections are ports of services/api's (ADR 0028), and
until 18.23 deletes the Python, the Python is the oracle. This script runs
the Python over four case files —

    reference-ch7, reference-ch13   test_form_projections.py's reference cases
    chapter-7-sample, chapter-13-sample   seeds/fixtures/<latest>/cases.json

— and writes, per case file, `<name>.case.json` (the case's records as
stored bodies, the shape both languages' parsers accept) and
`<name>.expected.json` (every form series' projected field values, plus the
means-test trace, the liens, the exemption analysis, the plan calculation,
the IRS standards and the creditor matrix, each in its wire shape). The .NET
side (ParityTests.cs) parses the SAME case file with Core's parsers, runs the
ports, and diffs every value. Run from the repo root:

    PYTHONPATH=services/api/src:packages/insolvia_core/src:services/api \\
      services/api/.venv/bin/python backend/tests/Insolvia.Engines.Tests/Parity/generate.py

The output is deterministic; a diff after regenerating is a behaviour change
in the Python oracle (or a new fixture version) and must be reviewed.
"""

from __future__ import annotations

import json
import sys
from dataclasses import asdict, is_dataclass
from datetime import date
from decimal import Decimal
from pathlib import Path
from typing import Any

from insolvia_api.core.chapter13_plan import (
    build_plan_inputs,
    calculate_plan,
    exemption_case_of,
    plan_calculation_json,
)
from insolvia_api.core.creditor_matrix import (
    format_for_court,
    generate_creditor_matrix,
    matrix_json,
)
from insolvia_api.core.exemption_analysis import analyse, analysis_json
from insolvia_api.core.form_fill import Check, Option, Text, WidgetStates
from insolvia_api.core.form_projections import CaseFile, FormProjectionError, project
from insolvia_api.core.form_templates import form_series_ids, latest_form
from insolvia_api.core.liens import derive_liens, liens_json
from insolvia_api.core.means_test_trace import means_test_json, trace_means_test
from insolvia_api.core.standards import resolve_standards
from insolvia_core import courts
from insolvia_core.case_entities import CaseEntity
from insolvia_core.cases import Case
from insolvia_core.creditors import CREDITOR
from insolvia_core.debtors import Debtor, _record_fields, debtor_body, parse_debtor
from insolvia_core.tax_ids import TaxIdRef

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
TODAY = date(2026, 10, 1)
CREATED_AT = "2026-09-01T12:00:00Z"

# CaseFile member -> (fixture collection, carries ids)
COLLECTIONS: dict[str, tuple[str, bool]] = {
    "prior_cases": ("prior_cases", False),
    "related_cases": ("related_cases", False),
    "sole_proprietorships": ("sole_proprietorships", False),
    "filing_professionals": ("filing_professionals", False),
    "employments": ("employments", True),
    "income_summaries": ("income_summaries", False),
    "pay_period_records": ("pay_period_records", False),
    "other_income_records": ("other_income_records", False),
    "means_test_inputs": ("means_test_inputs", False),
    "assets": ("assets", True),
    "exemptions": ("exemptions", False),
    "creditors": ("creditors", True),
    "claims": ("claims", True),
    "contract_leases": ("contract_leases", True),
    "codebtors": ("codebtors", False),
    "community_household_members": ("community_household_members", False),
    "households": ("households", True),
    "expenses": ("expenses", False),
    "dependents": ("dependents", False),
    "sofa_entries": ("sofa_entries", False),
    "plans": ("plans", False),
}


def _plain(value: Any) -> Any:
    if is_dataclass(value) and not isinstance(value, type):
        return _plain(asdict(value))
    if isinstance(value, dict):
        return {k: _plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_plain(v) for v in value]
    if isinstance(value, date):
        return value.isoformat()
    if isinstance(value, Decimal):
        return f"{value:f}"
    return value


# --- case files: to and from the stored-body JSON ----------------------------


def case_file_json(case_file: CaseFile) -> dict[str, Any]:
    out: dict[str, Any] = {
        "case": _plain(case_file.case),
        "debtors": [
            {
                "id": d.id,
                "filing_role": d.filing_role,
                "created_at": d.created_at,
                "updated_at": d.updated_at,
                "tax_id": _plain(d.tax_id) if d.tax_id is not None else None,
                "body": _plain(debtor_body(d)),
            }
            for d in case_file.debtors
        ],
        "tax_ids": dict(case_file.tax_ids),
        "petition": _plain(case_file.petition) if case_file.petition else None,
    }
    for member, (_, with_ids) in COLLECTIONS.items():
        items = getattr(case_file, member)
        out[member] = (
            [[i, _plain(b)] for i, b in items] if with_ids else [_plain(b) for b in items]
        )
    return out


def _kind_for(collection: str) -> Any:
    from insolvia_core import (
        assets,
        claims,
        codebtors,
        contract_leases,
        creditors,
        exemption_claims,
        expenses,
        income,
        means_test_inputs,
        petitions,
        plans,
        sofa,
    )

    for module in (
        assets,
        claims,
        codebtors,
        contract_leases,
        creditors,
        exemption_claims,
        expenses,
        income,
        means_test_inputs,
        petitions,
        plans,
        sofa,
    ):
        for value in vars(module).values():
            if getattr(value, "collection", None) == collection and hasattr(
                value, "parse_body"
            ):
                return value
    raise KeyError(collection)


def case_file_from_json(data: dict[str, Any]) -> CaseFile:
    """Every body re-parsed through insolvia_core's own parsers — the same
    trip the .NET side makes through Core's."""
    case = Case(**data["case"])
    debtors = []
    for raw in data["debtors"]:
        draft = parse_debtor(dict(raw["body"]), enforce_provenance=False)
        debtors.append(
            Debtor(
                id=raw["id"],
                case_id=case.id,
                filing_role=raw["filing_role"],
                created_at=raw["created_at"],
                updated_at=raw["updated_at"],
                tax_id=TaxIdRef(**raw["tax_id"]) if raw["tax_id"] else None,
                **_record_fields(draft),
            )
        )
    kwargs: dict[str, Any] = {
        "case": case,
        "debtors": tuple(debtors),
        "tax_ids": dict(data["tax_ids"]),
        "petition": (
            _kind_for("petitions").parse_body(data["petition"])
            if data["petition"]
            else None
        ),
    }
    for member, (collection, with_ids) in COLLECTIONS.items():
        kind = _kind_for(collection)
        items = data.get(member, [])
        kwargs[member] = tuple(
            (i, kind.parse_body(b)) if with_ids else kind.parse_body(b)
            for i, b in ((item if with_ids else (None, item)) for item in items)
        )
    return CaseFile(**kwargs)


# --- the seed fixtures --------------------------------------------------------


def _resolve_refs(value: Any, debtor_ids: dict[str, str]) -> Any:
    if isinstance(value, dict):
        if set(value) == {"$ref"}:
            collection, _, handle = value["$ref"].partition("/")
            return debtor_ids[handle] if collection == "debtors" else handle
        return {
            k: _resolve_refs(v, debtor_ids)
            for k, v in value.items()
            if k not in ("provenance", "$handle", "client_id")
        }
    if isinstance(value, list):
        return [_resolve_refs(v, debtor_ids) for v in value]
    return value


def fixture_case_json(fixture: dict[str, Any]) -> dict[str, Any]:
    handle = fixture["handle"]
    district = courts.district(fixture["court"])
    debtor_ids = {role: f"debtor-{role}" for role in fixture["debtors"]}
    debtors = []
    tax_ids: dict[str, str] = {}
    for role, raw in fixture["debtors"].items():
        body = _resolve_refs(raw, debtor_ids)
        tax = body.pop("tax_id", None)
        tax_ref = None
        if tax:
            digits = "".join(ch for ch in tax["value"] if ch.isdigit())
            tax_ids[role] = digits
            tax_ref = {"kind": tax["kind"], "last_four": digits[-4:], "ref": f"ref-{role}"}
        debtors.append(
            {
                "id": debtor_ids[role],
                "filing_role": role,
                "created_at": CREATED_AT,
                "updated_at": CREATED_AT,
                "tax_id": tax_ref,
                "body": body,
            }
        )
    collections = fixture["collections"]

    def items(name: str) -> list[Any]:
        return list(collections.get(name, []))

    out: dict[str, Any] = {
        "case": {
            "id": handle,
            "firm_id": "firm-fixture",
            "created_by": "subject-fixture",
            "chapter": fixture["chapter"],
            "district": district.name if district is not None else fixture["court"],
            "status": "intake",
            "created_at": CREATED_AT,
            "updated_at": CREATED_AT,
            "court": fixture["court"],
            "division": fixture["division"],
        },
        "debtors": debtors,
        "tax_ids": tax_ids,
        "petition": (
            _resolve_refs(items("petitions")[0], debtor_ids)
            if items("petitions")
            else None
        ),
    }
    for member, (collection, with_ids) in COLLECTIONS.items():
        rows = items(collection)
        out[member] = [
            [row.get("$handle", f"{collection}-{n + 1}"), _resolve_refs(row, debtor_ids)]
            if with_ids
            else _resolve_refs(row, debtor_ids)
            for n, row in enumerate(rows)
        ]
    return out


# --- the outputs ---------------------------------------------------------------


def _fill_json(fill: Any) -> Any:
    if isinstance(fill, dict):
        return {"rows": {name: _fill_json(f) for name, f in fill.items()}}
    if isinstance(fill, Text):
        return {"text": fill.value}
    if isinstance(fill, Check):
        return {"check": fill.on}
    if isinstance(fill, Option):
        return {"option": fill.export}
    if isinstance(fill, WidgetStates):
        return {"states": list(fill.states), "indexes": list(fill.indexes)}
    raise TypeError(type(fill))


def _outcome(call: Any) -> Any:
    try:
        return {"value": call()}
    except FormProjectionError as error:
        return {"problems": list(error.problems)}
    except Exception as error:  # noqa: BLE001 - the oracle's exact failure is the data
        return {"error": type(error).__name__}


def expected(case_file: CaseFile) -> dict[str, Any]:
    forms = {}
    for series in form_series_ids():
        release = latest_form(series)
        forms[series] = _outcome(
            lambda release=release: {
                k: _fill_json(v) for k, v in project(release, case_file).items()
            }
        )
    plan = case_file.plans[0] if case_file.plans else None
    creditors = [
        CaseEntity(
            kind=CREDITOR,
            id=i,
            case_id=case_file.case.id,
            created_at=CREATED_AT,
            updated_at=CREATED_AT,
            body=b,
            provenance={},
        )
        for i, b in case_file.creditors
    ]
    return {
        "forms": forms,
        "means_test": _outcome(lambda: means_test_json(trace_means_test(case_file))),
        "liens": _outcome(lambda: liens_json(derive_liens(case_file))),
        "exemptions": _outcome(
            lambda: analysis_json(analyse(exemption_case_of(case_file), today=TODAY))
        ),
        "plan": _outcome(
            lambda: plan_calculation_json(
                calculate_plan(
                    build_plan_inputs(
                        case_file, exemption_case_of(case_file), today=TODAY
                    ),
                    plan,
                )
            )
        ),
        "standards": _outcome(
            lambda: _plain(
                resolve_standards(
                    as_of=TODAY,
                    debtors=case_file.debtors,
                    dependents=case_file.dependents,
                )
            )
        ),
        "creditor_matrix": _outcome(
            lambda: matrix_json(
                generate_creditor_matrix(
                    creditors, format_for_court(case_file.case.court)
                )
            )
        ),
    }


def _write(path: Path, data: Any) -> None:
    path.write_text(json.dumps(data, indent=1, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    sys.path.insert(0, str(ROOT / "services" / "api"))
    from tests.unit.test_form_projections import (  # type: ignore[import-not-found]
        reference_case_file,
        reference_case_file_chapter_13,
    )

    sources: dict[str, dict[str, Any]] = {
        "reference-ch7": case_file_json(reference_case_file()),
        "reference-ch13": case_file_json(reference_case_file_chapter_13()),
    }
    fixtures_dir = ROOT / "seeds" / "fixtures"
    latest = max(
        (p for p in fixtures_dir.iterdir() if p.is_dir()),
        key=lambda p: int(p.name.lstrip("v")),
    )
    for fixture in json.loads((latest / "cases.json").read_text(encoding="utf-8"))["cases"]:
        sources[fixture["handle"]] = fixture_case_json(fixture)

    for name, data in sources.items():
        case_file = case_file_from_json(data)
        _write(OUT / f"{name}.case.json", data)
        _write(OUT / f"{name}.expected.json", expected(case_file))
        print(f"{name}: written", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
