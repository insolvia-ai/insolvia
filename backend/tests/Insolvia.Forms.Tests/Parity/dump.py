"""The parity harness's Python half: what today's engine (pypdf 6.16.2) makes.

For every parity case this writes, under <out>/:

    inputs/<case>.json     what to render — a form release and its values
                           (the canonical FieldValues JSON both languages
                           read), or a packet: a case file, an assembly date
                           and the output options
    python/<case>.pdf      a form case, as services/api renders it
    python/<case>/<part>   a packet case: every part of the assembled packet,
                           and packet.zip

The .NET half (ParityExportTests, run with INSOLVIA_FORMS_PARITY_DIR=<out>)
renders the same inputs into <out>/dotnet/, and compare.py diffs the two.

The cases:

- the 22 forms, each fed the goldens' own full-coverage synthetic values
  (services/api/tests/unit/test_form_fill.synthetic_values, imported);
- the three special paths: WidgetStates by state name (B101), by index on
  B106D row 2.4's real all-`On` defect, and B2010 with the DRAFT watermark
  and print-date stamp;
- non-ASCII text: B101 and B106I with "José Núñez" and friends;
- the packet assemblies of the four parity case files the Engines suite
  already shares (test_form_projections' two reference cases and the latest
  seed fixture's two sample cases, backend/tests/Insolvia.Engines.Tests/
  Parity/*.case.json), plus a draft print and an amendment.

Synthetic data only. Run from the repo root with
PYTHONPATH=services/api/src:packages/insolvia_core/src (Parity/run.sh does).
"""

from __future__ import annotations

import importlib.util
import json
import sys
from dataclasses import replace
from datetime import UTC, date, datetime
from pathlib import Path
from typing import Any

from insolvia_api.core import form_fill
from insolvia_api.core.form_fill import Check, Option, Text, WidgetStates, fill_form
from insolvia_api.core.form_overlay import OutputOptions, output_options_json, stamp_pages
from insolvia_api.core.form_templates import FormRelease, latest_form
from insolvia_api.core.packet_assembly import (
    AssembledPacket,
    CaseData,
    assemble,
    packet_zip,
    problem_json,
)
from insolvia_core.case_entities import CaseEntity

REPO = Path(__file__).resolve().parents[4]
PARITY_CASES = REPO / "backend/tests/Insolvia.Engines.Tests/Parity"
PRINTED_AT = datetime(2026, 10, 27, 12, 0, tzinfo=UTC)
AS_OF = date(2026, 10, 1)
ENTITY_TIME = "2026-09-01T12:00:00Z"
CASE_FILES = ("reference-ch7", "reference-ch13", "chapter-7-sample", "chapter-13-sample")


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def golden_module():
    return _load(REPO / "services/api/tests/unit/test_form_fill.py", "golden_fill")


def generate_module():
    return _load(PARITY_CASES / "generate.py", "engines_parity_generate")


def fill_json(fill: Any) -> Any:
    if isinstance(fill, dict):
        return {"rows": {name: fill_json(f) for name, f in fill.items()}}
    if isinstance(fill, Text):
        return {"text": fill.value}
    if isinstance(fill, Check):
        return {"check": fill.on}
    if isinstance(fill, Option):
        return {"option": fill.export}
    if isinstance(fill, WidgetStates):
        return {"states": list(fill.states), "indexes": list(fill.indexes)}
    raise TypeError(type(fill))


# --- form cases ----------------------------------------------------------------


def non_ascii_values(release: FormRelease) -> dict[str, Any]:
    """Names and places a debtor can have, in the court font's own encoding."""
    if release.form == "b101":
        return {
            "caption.header_debtor1_name": Text("José Núñez-Ibáñez"),
            "line_1_debtor1_first_name": Text("José"),
            "line_1_debtor1_last_name": Text("Núñez-Ibáñez"),
            "line_5_debtor1_street": Text("12 Rue de l’Église, Ste. 3"),
            "line_5_debtor1_city": Text("São Paulo"),
        }
    return {
        "caption.debtor1_name": Text("Zoë Ångström-Øresund"),
        "caption.debtor2_name": Text("François Müller — ¾ owner, “quoted”"),
    }


def form_cases() -> list[tuple[str, FormRelease, dict[str, Any], bool]]:
    golden = golden_module()
    b101 = latest_form("form/b101")
    b106d = latest_form("form/b106d")
    b106i = latest_form("form/b106i")
    b2010 = latest_form("form/b2010")
    broken = b106d.field("claim.who_owes_row_2_4")
    assert b106d.widgets[broken.pdf_names[0]].states == ("On",)
    cases = [
        (series.removeprefix("form/"), release, golden.synthetic_values(release), False)
        for series in golden.FORM_SERIES
        for release in [latest_form(series)]
    ]
    cases += [
        ("b101-widget-states", b101, {"line_15_debtor1_credit_counseling": WidgetStates(states=("2", "4"))}, False),
        ("b106d-broken-group", b106d, {broken.id: WidgetStates(indexes=(1,))}, False),
        ("b2010-stamped", b2010, {}, True),
        ("b101-non-ascii", b101, non_ascii_values(b101), False),
        ("b106i-non-ascii", b106i, non_ascii_values(b106i), False),
    ]
    return cases


def render_form(release: FormRelease, values: dict[str, Any], stamped: bool) -> bytes:
    data = fill_form(release, values)
    if stamped:
        data = stamp_pages(data, draft_watermark=True, print_date=True, printed_at=PRINTED_AT)
    return data


# --- packet cases ----------------------------------------------------------------

# CaseData member -> CaseFile member (same names) and whether the case file
# carries ids for it.
ID_COLLECTIONS = {"employments", "assets", "creditors", "claims", "contract_leases", "households"}


def case_data(name: str, generate) -> CaseData:
    """A parity case file as CaseData: entities wrapped with the case file's
    ids, or `<collection>-<n>` where it carries none — the same synthetic
    ids ParityExportTests.CaseDataOf builds."""
    raw = json.loads((PARITY_CASES / f"{name}.case.json").read_text(encoding="utf-8"))
    case_file = generate.case_file_from_json(raw)

    def wrap(collection: str, items, with_ids: bool):
        kind = generate._kind_for(collection)
        out = []
        for n, item in enumerate(items):
            entity_id, body = item if with_ids else (f"{collection}-{n + 1}", item)
            out.append(
                CaseEntity(
                    kind=kind,
                    id=entity_id,
                    case_id=case_file.case.id,
                    created_at=ENTITY_TIME,
                    updated_at=ENTITY_TIME,
                    body=body,
                    provenance={},
                )
            )
        return tuple(out)

    kwargs: dict[str, Any] = {
        "case": case_file.case,
        "debtors": case_file.debtors,
        "tax_ids": dict(case_file.tax_ids),
        "petitions": wrap("petitions", [case_file.petition] if case_file.petition else [], False),
    }
    for member, (collection, _) in generate.COLLECTIONS.items():
        kwargs[member] = wrap(collection, getattr(case_file, member), member in ID_COLLECTIONS)
    return CaseData(**kwargs)


def amended(data: CaseData) -> CaseData:
    """The first asset and the first nonpriority claim flagged amended, on a
    filed case — an amendedOnly packet's input."""
    case = replace(data.case, status="filed", filed_at="2026-09-15")
    assets = tuple(replace(e, amended=(i == 0)) for i, e in enumerate(data.assets))
    first = next(
        (e.id for e in data.claims if e.body.claim_class == "nonpriority_unsecured"), None
    )
    claims = tuple(replace(e, amended=(e.id == first)) for e in data.claims)
    return replace(data, case=case, assets=assets, claims=claims)


def packet_cases() -> list[tuple[str, str, OutputOptions, bool]]:
    cases = [(name, name, OutputOptions(), False) for name in CASE_FILES]
    cases.append(("reference-ch7-draft", "reference-ch7", OutputOptions(draft_watermark=True, print_date=True, sign_electronically=True), False))
    cases.append(("reference-ch7-signature-pages", "reference-ch7", OutputOptions(signature_pages="only"), False))
    cases.append(("reference-ch7-amendment", "reference-ch7", OutputOptions(amended_only=True), True))
    return cases


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "backend/tests/Insolvia.Forms.Tests/Parity/out"
    (out / "inputs").mkdir(parents=True, exist_ok=True)
    (out / "python").mkdir(parents=True, exist_ok=True)

    for case, release, values, stamped in form_cases():
        doc = {
            "kind": "form",
            "series": release.series_id,
            "release": release.release_id,
            "values": {fid: fill_json(v) for fid, v in values.items()},
            "stamps": {"printedAt": PRINTED_AT.isoformat()} if stamped else None,
        }
        (out / "inputs" / f"{case}.json").write_text(json.dumps(doc, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        (out / "python" / f"{case}.pdf").write_bytes(render_form(release, values, stamped))
        print(f"form   {case}", file=sys.stderr)

    generate = generate_module()
    for case, source, options, amend in packet_cases():
        data = case_data(source, generate)
        if amend:
            data = amended(data)
        doc = {
            "kind": "packet",
            "caseFile": source,
            "amended": amend,
            "asOf": AS_OF.isoformat(),
            "printedAt": PRINTED_AT.isoformat(),
            "options": output_options_json(options),
        }
        (out / "inputs" / f"{case}.json").write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
        outcome = assemble(data, as_of=AS_OF, options=options, printed_at=PRINTED_AT)
        target = out / "python" / case
        target.mkdir(parents=True, exist_ok=True)
        if isinstance(outcome, AssembledPacket):
            for name, content in outcome.parts:
                (target / name).write_bytes(content)
            (target / "packet.zip").write_bytes(packet_zip(outcome.parts))
            summary = {"parts": [name for name, _ in outcome.parts], "formRevisions": dict(outcome.form_revisions), "constantsSetId": outcome.constants_set_id, "creditorCount": outcome.creditor_count}
        else:
            summary = {"problems": [problem_json(p) for p in outcome]}
        (target / "outcome.json").write_text(json.dumps(summary, indent=1, sort_keys=True) + "\n", encoding="utf-8")
        print(f"packet {case}: {len(summary.get('parts', summary.get('problems', [])))}", file=sys.stderr)


if __name__ == "__main__":
    _ = form_fill  # imported for the goldens module's sake
    main()
