"""The fill engine's golden-file tests (issue #93): exact-match output.

The issue's "done" is a form matching the published PDF exactly, so the
goldens pin THREE layers per form, strongest claim first:

1. **Field-exact:** every AcroForm field of the filled output is read back —
   value and per-widget appearance states — and compared against the golden
   JSON. A wrong export state, a value landing in the wrong box, or a box
   left unexpectedly empty is a field-level diff a reviewer can read.
2. **Template-exact:** every page's content stream is byte-identical to the
   official template's. The engine writes form values and appearances and
   nothing else, so the printed form around the answers IS the court's own
   PDF — this is what makes the output "render exact" without a rasteriser.
3. **Byte-exact:** the output's sha256 is pinned. The fill is deterministic,
   so any byte drift — a pypdf upgrade changing serialization, an accidental
   nondeterminism — trips this even when layers 1-2 still hold.

WHY NOT PIXEL DIFFS: rasterising needs a rendering engine whose output varies
by platform and version — a flake source and a heavy dev dependency — and it
is WEAKER than the three layers above: identical content streams plus exact
field values/appearances determine the render. If court feedback ever demands
raster proof, that is a new decision, not a regeneration of these.

REGENERATING: `UPDATE_FORM_GOLDENS=1 pytest tests/test_form_fill.py` rewrites
the golden files, and the diff is the review surface — read it field by
field, exactly as a registry release diff is read (ADR 0014). A sha change
with NO field diff means serialization moved (a pypdf bump does this); that
is reviewable as "fields identical, bytes re-serialized", never blind.
These are not UI snapshots (the kind this repo decided against): they pin a
court-facing output contract that "looks right" cannot approximate.

The fixture fills EVERY logical field of each form with synthetic,
deliberately varied values — every text box, every column of every repeated
row, a rotating pick of every radio's export states — so the goldens exercise
the whole widget surface, including the shared-widget quirks. Semantically
coherent case data arrives with the projection layer, which gets its own
goldens; this file proves the ENGINE, not the mapping.
"""

import hashlib
import io
import json
import os
from collections.abc import Iterator
from pathlib import Path
from typing import cast

import pytest
from insolvia_api.core.form_fill import (
    Check,
    FieldFill,
    FormFillError,
    Option,
    Text,
    WidgetStates,
    fill_form,
)
from insolvia_api.core.form_templates import (
    FieldSpec,
    FormRelease,
    Widget,
    latest_form,
)
from pypdf import PdfReader
from pypdf.generic import ArrayObject, DictionaryObject

GOLDEN_DIR = Path(__file__).resolve().parent / "goldens"

FORM_SERIES = ("form/b101", "form/b106i")


# --- synthetic full-coverage values -------------------------------------------


def _text_value(field: FieldSpec, widget: Widget, i: int, j: int) -> str:
    """A deterministic, distinctive value for one text-widget instance."""
    if field.type == "money":
        return f"1,2{(i + j) % 10}4.56"
    if field.type == "integer":
        return str((i + j) % 9 + 1)
    if field.type == "date":
        return f"01/{(i + j) % 28 + 1:02d}/2026"
    if field.type == "year":
        return "2026"
    if field.type == "state":
        return "FL"
    if field.type == "district":
        return "Middle District of Florida"
    if field.type == "ssn_itin":
        value = f"{1234 + j}"
    elif field.type == "ein":
        value = f"12345678{j}"
    elif field.type == "phone":
        value = f"(305) 555-01{(i + j) % 100:02d}"
    elif field.type == "email":
        value = f"sample{i}.{j}@example.com"
    elif field.type == "signature":
        value = f"Ada Q Lovelace {j + 1}"
    else:
        value = f"S{i}.{j} {field.id}"[:80]
    if widget.max_len is not None:
        value = value[: widget.max_len]
    return value


def _instance_fill(field: FieldSpec, widget: Widget, i: int, j: int) -> FieldFill:
    if field.type == "checkbox":
        return Check()
    if field.type == "radio":
        # Rotate through the WIDGET's own states so multi-widget radios (one
        # group per debtor column) each get a state they actually declare.
        return Option(widget.states[(i + j) % len(widget.states)])
    return Text(_text_value(field, widget, i, j))


def synthetic_values(
    release: FormRelease,
) -> dict[str, FieldFill | dict[str, FieldFill]]:
    """One value for every logical field — full coverage, varied by design."""
    values: dict[str, FieldFill | dict[str, FieldFill]] = {}
    for i, field in enumerate(release.fields):
        if len(field.pdf_names) == 1:
            values[field.id] = _instance_fill(
                field, release.widgets[field.pdf_names[0]], i, 0
            )
        else:
            values[field.id] = {
                name: _instance_fill(field, release.widgets[name], i, j)
                for j, name in enumerate(field.pdf_names)
            }
    return values


# --- reading the filled output back -------------------------------------------


def _qualified_name(annotation: DictionaryObject) -> str | None:
    parts: list[str] = []
    node: object | None = annotation
    while node is not None:
        obj = node.get_object()  # type: ignore[attr-defined]
        title = obj.get("/T")
        if title is not None:
            parts.append(str(title))
        node = obj.get("/Parent")
    return ".".join(reversed(parts)) if parts else None


def read_form(release: FormRelease, data: bytes) -> dict[str, dict[str, object]]:
    """Every fillable field of the output: /V, plus per-widget appearance
    states for buttons, in the order widgets appear walking the pages."""
    reader = PdfReader(io.BytesIO(data))
    raw = reader.get_fields() or {}
    out: dict[str, dict[str, object]] = {}
    for name, widget in release.widgets.items():
        if widget.kind == "pushbutton":
            continue
        value = raw[name].get("/V") if name in raw else None
        out[name] = {"value": None if value is None else str(value)}
    for page in reader.pages:
        for ref in page.get("/Annots") or []:
            annotation = ref.get_object()
            name = _qualified_name(annotation)
            if name is None or name not in out:
                continue
            if release.widgets[name].kind not in ("checkbox", "radio"):
                continue
            states = cast("list[str]", out[name].setdefault("widget_states", []))
            appearance = annotation.get("/AS")
            states.append("/Off" if appearance is None else str(appearance))
    return out


def _content_streams(reader: PdfReader) -> Iterator[bytes]:
    for page in reader.pages:
        contents = page.get_object()["/Contents"].get_object()
        if isinstance(contents, ArrayObject):
            yield b"".join(part.get_object().get_data() for part in contents)
        else:
            yield contents.get_data()


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# --- the goldens --------------------------------------------------------------


@pytest.mark.parametrize("series", FORM_SERIES)
def test_filled_form_matches_its_golden(series: str) -> None:
    release = latest_form(series)
    data = fill_form(release, synthetic_values(release))
    observed = {
        "release": release.release_id,
        "sha256": _sha256(data),
        "fields": read_form(release, data),
    }
    path = GOLDEN_DIR / f"{release.form}.json"
    if os.environ.get("UPDATE_FORM_GOLDENS") == "1":  # pragma: no cover
        path.write_text(
            json.dumps(observed, indent=1, sort_keys=True) + "\n",
            encoding="utf-8",
        )
    golden = json.loads(path.read_text(encoding="utf-8"))
    assert golden["release"] == release.release_id
    # Field-exact first: on failure this diff names the box and the value.
    assert observed["fields"] == golden["fields"]
    # Byte-exact second: if this fails ALONE, the field surface is intact and
    # serialization moved — almost always a deliberate pypdf bump. Review,
    # then regenerate with UPDATE_FORM_GOLDENS=1.
    assert observed["sha256"] == golden["sha256"]


@pytest.mark.parametrize("series", FORM_SERIES)
def test_every_fillable_box_is_exercised_and_filled(series: str) -> None:
    """Fixture rot guard: the synthetic fill must land a value or a ticked
    state in every fillable field, or the golden silently stops covering
    part of the form."""
    release = latest_form(series)
    result = read_form(release, fill_form(release, synthetic_values(release)))
    empty = [
        name
        for name, entry in result.items()
        if entry["value"] is None
        and all(s == "/Off" for s in cast("list[str]", entry.get("widget_states", [])))
    ]
    assert empty == []


@pytest.mark.parametrize("series", FORM_SERIES)
def test_fill_leaves_the_official_pages_untouched(series: str) -> None:
    """The engine writes AcroForm values and appearances, never page content:
    the printed form around the answers stays the court's own PDF, byte for
    byte. This is the 'render-exact against the official PDF' claim."""
    release = latest_form(series)
    data = fill_form(release, synthetic_values(release))
    template = PdfReader(io.BytesIO(release.template_pdf))
    filled = PdfReader(io.BytesIO(data))
    assert len(template.pages) == len(filled.pages)
    for before, after in zip(
        _content_streams(template), _content_streams(filled), strict=True
    ):
        assert before == after


@pytest.mark.parametrize("series", FORM_SERIES)
def test_fill_is_deterministic_to_the_byte(series: str) -> None:
    release = latest_form(series)
    values = synthetic_values(release)
    assert fill_form(release, values) == fill_form(release, values)


def test_a_partial_fill_is_allowed_and_touches_only_its_fields() -> None:
    release = latest_form("form/b106i")
    data = fill_form(
        release,
        {
            "caption.debtor1_name": Text("Ada Q Lovelace"),
            "line_13_change_expected": Option("no"),
        },
    )
    result = read_form(release, data)
    assert result["Debtor 1"]["value"] == "Ada Q Lovelace"
    assert result["Check increase"]["value"] == "/no"
    # Everything else reads exactly as the blank template does (the template
    # itself ships a default in the district dropdown, so the baseline is the
    # template's own read-back, not emptiness).
    baseline = read_form(release, release.template_pdf)
    changed = {n for n in result if result[n] != baseline[n]}
    assert changed == {"Debtor 1", "Check increase"}


# --- the broken-group escape hatch --------------------------------------------


def test_widget_states_tick_boxes_without_a_field_value() -> None:
    """B106D row 2.4 and B107 q26 need appearance-level ticks (their groups
    are defective; forms/README.md). The mechanism is exercised here against
    B101's line-15 group, whose widgets are healthy and thus verifiable:
    index 2 must light exactly the widget whose export is 'On' — the
    position-order/export-order agreement verified at ingestion."""
    release = latest_form("form/b101")
    data = fill_form(
        release,
        {"line_15_debtor1_credit_counseling": WidgetStates(indexes=(2,))},
    )
    result = read_form(release, data)
    entry = result["Check Box16"]
    assert entry["value"] is None  # no /V — appearance only
    assert entry["widget_states"] == ["/Off", "/Off", "/On", "/Off"]


def test_widget_states_by_state_name() -> None:
    release = latest_form("form/b101")
    data = fill_form(
        release,
        {"line_15_debtor1_credit_counseling": WidgetStates(states=("2", "4"))},
    )
    entry = read_form(release, data)["Check Box16"]
    assert entry["widget_states"] == ["/Off", "/2", "/Off", "/4"]


# --- refusals -----------------------------------------------------------------


@pytest.mark.parametrize(
    ("series", "values", "problem"),
    [
        pytest.param(
            "form/b106i",
            {"no_such_field": Text("x")},
            "not a field of",
            id="unknown-field-id",
        ),
        pytest.param(
            "form/b106i",
            {"line_13_change_expected": Option("Maybe")},
            "not one of the PDF's states",
            id="export-state-the-pdf-does-not-declare",
        ),
        pytest.param(
            "form/b106i",
            {"caption.district": Text("The Moon District")},
            "not an option of",
            id="district-not-in-the-dropdown",
        ),
        pytest.param(
            "form/b101",
            {"line_3_debtor1_ssn_last4": Text("123456789")},
            "longer than the PDF's 4-character box",
            id="value-the-box-would-truncate",
        ),
        pytest.param(
            "form/b106i",
            {"line_2_gross_wages": Text("4,321.00")},
            "address instances by PDF name",
            id="scalar-on-a-repeated-field",
        ),
        pytest.param(
            "form/b106i",
            {"line_2_gross_wages": {"No such column": Text("4,321.00")}},
            "not among this field's PDF fields",
            id="instance-name-outside-the-claim",
        ),
        pytest.param(
            "form/b106i",
            {"line_13_change_expected": Text("No")},
            "Text on a radio field",
            id="text-on-a-radio",
        ),
        pytest.param(
            "form/b106i",
            {"caption.amended_or_supplement": Check()},
            "Check on a radio field",
            id="check-on-a-group",
        ),
        pytest.param(
            "form/b106i",
            {"caption.debtor1_name": Text("")},
            "omit the field",
            id="empty-value",
        ),
        pytest.param(
            "form/b101",
            {"line_15_debtor1_credit_counseling": WidgetStates()},
            "selects nothing",
            id="widget-states-selecting-nothing",
        ),
        pytest.param(
            "form/b101",
            {"line_15_debtor1_credit_counseling": WidgetStates(states=("5",))},
            "not one of the PDF's states",
            id="widget-states-unknown-state",
        ),
        pytest.param(
            "form/b101",
            {"caption.header_debtor1_name": WidgetStates(states=("On",))},
            "WidgetStates on a text field",
            id="widget-states-on-text",
        ),
    ],
)
def test_values_that_cannot_land_are_refused(
    series: str, values: dict[str, FieldFill], problem: str
) -> None:
    release = latest_form(series)
    with pytest.raises(FormFillError, match=problem):
        fill_form(release, values)


def test_every_problem_is_reported_at_once() -> None:
    release = latest_form("form/b106i")
    with pytest.raises(FormFillError) as excinfo:
        fill_form(
            release,
            {
                "no_such_field": Text("x"),
                "line_13_change_expected": Option("Maybe"),
            },
        )
    assert len(excinfo.value.problems) == 2
