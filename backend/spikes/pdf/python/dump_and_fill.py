"""Spike #514: dump the fill engine's input per case, and run the Python engine.

For each case this writes, under <out>/:

    inputs/<case>.json   the PDF-level instructions core/form_fill.fill_form
                         derives from its logical values (after its own
                         validation): text values, button export states,
                         direct widget states, overlay boxes, page stamps.
                         The .NET spike consumes exactly this file.
    python/<case>.pdf    what today's engine (pypdf 6.16.2) produces.
    python/timings.json  median fill time per case.

The values are the golden tests' own synthetic fixture
(services/api/tests/unit/test_form_fill.py `synthetic_values`) — imported, not
copied, so this is byte-for-byte what the goldens feed the engine. Synthetic,
no real data.

Run from the repo root with PYTHONPATH=services/api/src (commands: docs/research/dotnet-pdf-engine-2026-10-27.md).
"""

from __future__ import annotations

import importlib.util
import json
import statistics
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

from insolvia_api.core import form_fill
from insolvia_api.core.form_fill import (
    Check,
    FieldValues,
    Option,
    Text,
    WidgetStates,
    fill_form,
)
from insolvia_api.core.form_overlay import stamp_pages
from insolvia_api.core.form_templates import FormRelease, latest_form

REPO = Path(__file__).resolve().parents[4]
REGISTRY = REPO / "services/api/src/insolvia_api/regulatory"
PRINTED_AT = datetime(2026, 10, 27, 12, 0, tzinfo=UTC)
RUNS = 15


def _golden_module():  # the goldens' own fixture, imported by path
    path = REPO / "services/api/tests/unit/test_form_fill.py"
    spec = importlib.util.spec_from_file_location("golden_fill", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _broken_group_values(release: FormRelease) -> FieldValues:
    """B106D row 2.4: every option exports `On` — only WidgetStates(indexes)
    can tick one. Ticks the second of the four, by position."""
    field = release.field("claim.who_owes_row_2_4")  # widget `check 5`
    assert release.widgets[field.pdf_names[0]].states == ("On",)
    return {field.id: WidgetStates(indexes=(1,))}


def cases() -> list[tuple[str, FormRelease, FieldValues, bool]]:
    golden = _golden_module()
    b101 = latest_form("form/b101")
    b106d = latest_form("form/b106d")
    b2010 = latest_form("form/b2010")
    # Every form series, fed the goldens' full-coverage synthetic values —
    # B101, B2030 and the radio-heaviest form, B106A/B, are the spike's
    # named three; the other 19 cost nothing extra and preview 18.16.
    full = [
        (series.removeprefix("form/"), release, golden.synthetic_values(release), False)
        for series in golden.FORM_SERIES
        for release in [latest_form(series)]
    ]
    return [
        *full,
        (
            "b101-widget-states",
            b101,
            {"line_15_debtor1_credit_counseling": WidgetStates(states=("2", "4"))},
            False,
        ),
        ("b106d-broken-group", b106d, _broken_group_values(b106d), False),
        ("b2010-stamped", b2010, {}, True),
    ]


def instructions(release: FormRelease, values: FieldValues, stamped: bool) -> dict:
    """The same flattening fill_form performs, written down."""
    flat = form_fill._per_widget(release, values)  # validates, like fill_form
    text: list[list[str]] = []
    buttons: list[list[str]] = []
    widget_states: list[dict] = []
    overlay: list[dict] = []
    for name, (_spec, fill) in flat.items():
        if name in release.boxes:
            box = release.boxes[name]
            entry = {
                "name": box.name,
                "page": box.page,
                "x": box.x,
                "y": box.y,
                "w": box.w,
                "h": box.h,
                "size": box.size or form_fill.OVERLAY_FONT_SIZE,
            }
            if isinstance(fill, Check):
                if fill.on:
                    overlay.append({**entry, "kind": "check"})
            else:
                assert isinstance(fill, Text)
                overlay.append({**entry, "kind": "text", "value": fill.value})
        elif isinstance(fill, Text):
            text.append([name, fill.value])
        elif isinstance(fill, Check):
            if fill.on:
                buttons.append([name, release.widgets[name].states[0]])
        elif isinstance(fill, Option):
            buttons.append([name, fill.export])
        elif isinstance(fill, WidgetStates):
            widget_states.append(
                {"name": name, "states": list(fill.states), "indexes": list(fill.indexes)}
            )
    release_dir = (
        REGISTRY / release.series_id / release.effective_date.isoformat()
    )
    return {
        "release": release.release_id,
        "template": str((release_dir / "template.pdf").relative_to(REPO)),
        "text": text,
        "buttons": buttons,
        "widget_states": widget_states,
        "overlay": overlay,
        "stamps": (
            {
                "draft_watermark": True,
                "print_date": True,
                "text": f"Printed {PRINTED_AT.strftime('%Y-%m-%d %H:%M')} UTC",
            }
            if stamped
            else None
        ),
    }


def render(release: FormRelease, values: FieldValues, stamped: bool) -> bytes:
    data = fill_form(release, values)
    if stamped:
        data = stamp_pages(
            data, draft_watermark=True, print_date=True, printed_at=PRINTED_AT
        )
    return data


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("backend/spikes/pdf/out")
    (out / "inputs").mkdir(parents=True, exist_ok=True)
    (out / "python").mkdir(parents=True, exist_ok=True)
    timings: dict[str, float] = {}
    for case, release, values, stamped in cases():
        doc = instructions(release, values, stamped)
        (out / "inputs" / f"{case}.json").write_text(json.dumps(doc, indent=1) + "\n")
        data = render(release, values, stamped)
        (out / "python" / f"{case}.pdf").write_bytes(data)
        samples = []
        for _ in range(RUNS):
            start = time.perf_counter()
            render(release, values, stamped)
            samples.append((time.perf_counter() - start) * 1000)
        timings[case] = round(statistics.median(samples), 2)
        print(
            f"{case:22} text={len(doc['text']):4} buttons={len(doc['buttons']):3} "
            f"widget_states={len(doc['widget_states'])} overlay={len(doc['overlay']):3} "
            f"median {timings[case]} ms"
        )
    (out / "python" / "timings.json").write_text(json.dumps(timings, indent=1) + "\n")


if __name__ == "__main__":
    main()
