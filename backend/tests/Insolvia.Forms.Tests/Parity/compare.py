"""The parity gate: pypdf's renders against Insolvia.Forms', field by field.

Productionised from spike #514's checker (backend/spikes/pdf/python/
compare.py). For every case in <out>/inputs/ it reads <out>/python/ and
<out>/dotnet/ back with pypdf and compares, per PDF:

- fields       every AcroForm field node: /FT, /V's value and its PDF type
- widgets      every button widget's /AS, in page order
- appearances  every text/choice widget's /AP /N: the decoded stream bytes
               and its dictionary (references resolved, /Length dropped)
- official     each page's own content streams, byte for byte, with the
               engine's overlay stream split out
- overlays     the engine's overlay streams, byte for byte
- text runs    every text-showing operator as (page, text, x, y, size) after
               the CTM — overlay boxes, stamps and the cover sheet included
- golden       for the synthetic form cases, the Python goldens' OWN reader
               (test_form_fill.read_form) over the .NET output, against the
               committed golden JSON
- pixels       both files rendered by PDFium (pypdfium2) at 144 dpi with the
               form environment initialised; the share of pixels that differ

A packet case also compares its outcome (the parts in order, the pins, or
the gate's problems) and every non-PDF part byte for byte.

The ONE deliberate difference (Insolvia.Forms' README, "pypdf quirks"):
quirk 1, a button field's /V — pypdf writes a text string, .NET the name the
spec requires. A /Btn field whose /V differs only in that type is counted
equal and reported under "quirk 1". Anything else fails the run (exit 1).

Run from the repo root with PYTHONPATH=services/api/src:packages/insolvia_core/src
(Parity/run.sh does). Writes <out>/report.json and prints the parity table.
"""

from __future__ import annotations

import importlib.util
import io
import json
import sys
from collections import Counter
from pathlib import Path

import pypdfium2 as pdfium
from PIL import ImageChops
from pypdf import PdfReader
from pypdf.generic import ArrayObject, NameObject, StreamObject, TextStringObject

from insolvia_api.core.form_templates import latest_form

REPO = Path(__file__).resolve().parents[4]
GOLDENS = REPO / "services/api/tests/unit/goldens"
OVERLAY_MARKER = b"% insolvia-overlay\n"


def _golden_module():
    path = REPO / "services/api/tests/unit/test_form_fill.py"
    spec = importlib.util.spec_from_file_location("golden_fill", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def qualified(node) -> str | None:
    parts = []
    obj = node
    while obj is not None:
        obj = obj.get_object()
        if "/T" in obj:
            parts.append(str(obj["/T"]))
        obj = obj.get("/Parent")
    return ".".join(reversed(parts)) if parts else None


def field_of(widget):
    if "/FT" in widget and "/T" in widget:
        return widget
    return widget["/Parent"].get_object()


def inherited_ft(node) -> str | None:
    obj = node
    while obj is not None:
        obj = obj.get_object()
        if "/FT" in obj:
            return str(obj["/FT"])
        obj = obj.get("/Parent")
    return None


def normalize(obj, depth: int = 0):
    """A PDF object as plain data: references resolved, numbers as rounded
    floats, streams by content."""
    if depth > 8:
        return "..."
    obj = obj.get_object() if hasattr(obj, "get_object") else obj
    if isinstance(obj, StreamObject):
        return {"stream": obj.get_data().hex()[:64], **normalize(dict(obj.items()) | {"/Length": 0, "/Filter": 0}, depth + 1)}
    if isinstance(obj, dict):
        return {str(k): normalize(v, depth + 1) for k, v in sorted(obj.items()) if k not in ("/Length", "/Filter")}
    if isinstance(obj, list):
        return [normalize(v, depth + 1) for v in obj]
    if isinstance(obj, (int, float)) and not isinstance(obj, bool):
        return round(float(obj), 4)
    return str(obj)


def read(data: bytes) -> dict:
    reader = PdfReader(io.BytesIO(data))
    fields: dict[str, tuple] = {}
    widgets: list[tuple] = []
    appearances: dict[str, tuple] = {}
    official: list[bytes] = []
    overlays: list[bytes] = []
    runs: Counter = Counter()
    seen: Counter = Counter()
    for page_no, page in enumerate(reader.pages, 1):
        for ref in page.get("/Annots") or []:
            w = ref.get_object()
            if w.get("/Subtype") != "/Widget":
                continue
            field = field_of(w)
            name = qualified(field)
            ft = inherited_ft(field)
            v = field.get("/V")
            fields[name] = (ft, None if v is None else str(v), type(v).__name__)
            index = seen[name]
            seen[name] += 1
            if ft == "/Btn":
                widgets.append((name, index, str(w.get("/AS", "/Off"))))
            elif ft in ("/Tx", "/Ch"):
                n = w.get("/AP", {}).get("/N")
                if n is not None:
                    n = n.get_object()
                    meta = normalize({k: v2 for k, v2 in n.items() if k not in ("/Length", "/Filter")})
                    data_n = n.get_data() if isinstance(n, StreamObject) else b""
                    appearances[f"{name}#{index}"] = (data_n, json.dumps(meta, sort_keys=True))
        contents = page.get("/Contents")
        if contents is not None:
            contents = contents.get_object()
            parts = (
                [p.get_object().get_data() for p in contents]
                if isinstance(contents, ArrayObject)
                else [contents.get_data()]
            )
            if parts and parts[-1].startswith(OVERLAY_MARKER):
                overlays.append(parts[-1])
                parts = parts[:-1]
            official.append(b"".join(parts))

        def visit(text, cm, tm, _font, size, page_no=page_no):
            if not text.strip():
                return
            x = tm[4] * cm[0] + tm[5] * cm[2] + cm[4]
            y = tm[4] * cm[1] + tm[5] * cm[3] + cm[5]
            runs[(page_no, text, round(x, 2), round(y, 2), round(size, 2))] += 1

        page.extract_text(visitor_text=visit)
    return {
        "fields": fields,
        "widgets": widgets,
        "appearances": appearances,
        "official": official,
        "overlays": overlays,
        "runs": runs,
        "pages": len(reader.pages),
    }


def pixel_diff(a: bytes, b: bytes) -> list[float]:
    """Per page, the % of pixels that differ at all (PDFium, 144 dpi, form
    environment initialised — without it PDFium skips widgets)."""
    out = []
    da, db = pdfium.PdfDocument(a), pdfium.PdfDocument(b)
    da.init_forms()
    db.init_forms()
    if len(da) != len(db):
        return [100.0]
    for i in range(len(da)):
        ia = da[i].render(scale=2, may_draw_forms=True).to_pil().convert("RGB")
        ib = db[i].render(scale=2, may_draw_forms=True).to_pil().convert("RGB")
        if ia.size != ib.size:
            out.append(100.0)
            continue
        histogram = ImageChops.difference(ia, ib).convert("L").histogram()
        out.append(100.0 * (1 - histogram[0] / (ia.width * ia.height)))
    return out


def quirk_1(name: str, py: tuple | None, net: tuple | None) -> bool:
    """A button /V that differs only by pypdf's text-string type."""
    return (
        py is not None
        and net is not None
        and py[0] == net[0] == "/Btn"
        and py[1] == net[1]
        and py[2] == TextStringObject.__name__
        and net[2] == NameObject.__name__
    )


def compare_pdf(py: bytes, net: bytes, golden_form: str | None, golden) -> dict:
    a, b = read(py), read(net)

    def match(x: dict, y: dict) -> tuple[int, int, list]:
        keys = set(x) | set(y)
        bad = sorted(k for k in keys if x.get(k) != y.get(k))
        return len(keys) - len(bad), len(keys), bad

    keys = set(a["fields"]) | set(b["fields"])
    quirks = sorted(k for k in keys if a["fields"].get(k) != b["fields"].get(k) and quirk_1(k, a["fields"].get(k), b["fields"].get(k)))
    f_bad = sorted(k for k in keys if a["fields"].get(k) != b["fields"].get(k) and k not in quirks)
    wa = {(n, i): s for n, i, s in a["widgets"]}
    wb = {(n, i): s for n, i, s in b["widgets"]}
    w_ok, w_all, w_bad = match(wa, wb)
    ap_ok, ap_all, ap_bad = match(a["appearances"], b["appearances"])
    pixels = pixel_diff(py, net)
    result = {
        "fields": [len(keys) - len(f_bad), len(keys)],
        "fields_mismatch": [[k, a["fields"].get(k), b["fields"].get(k)] for k in f_bad[:5]],
        "quirk_1_button_values": len(quirks),
        "widgets": [w_ok, w_all],
        "widgets_mismatch": [[str(k), wa.get(k), wb.get(k)] for k in w_bad[:5]],
        "appearances": [ap_ok, ap_all],
        "appearance_mismatch": [
            [k, (a["appearances"].get(k) or [b""])[0][:200].decode("latin1"), (b["appearances"].get(k) or [b""])[0][:200].decode("latin1"),
             (a["appearances"].get(k) or [b"", ""])[1][:300], (b["appearances"].get(k) or [b"", ""])[1][:300]]
            for k in ap_bad[:2]
        ],
        "official_pages_equal": a["official"] == b["official"],
        "overlay_streams_equal": a["overlays"] == b["overlays"],
        "text_runs": [sum(a["runs"].values()), sum(b["runs"].values()), a["runs"] == b["runs"]],
        "text_runs_only_python": [list(k) for k in (a["runs"] - b["runs"])][:5],
        "text_runs_only_dotnet": [list(k) for k in (b["runs"] - a["runs"])][:5],
        "pages": [a["pages"], b["pages"]],
        "pixels": pixels,
        "bytes": [len(py), len(net)],
    }
    if golden_form is not None:
        doc = json.loads((GOLDENS / f"{golden_form}.json").read_text())
        release = latest_form(f"form/{golden_form}")
        assert release.release_id == doc["release"]
        result["golden_fields_equal"] = golden.read_form(release, net) == doc["fields"]
    result["ok"] = (
        not f_bad
        and not w_bad
        and not ap_bad
        and result["official_pages_equal"]
        and result["overlay_streams_equal"]
        and result["text_runs"][2]
        and a["pages"] == b["pages"]
        and max(pixels or [0.0]) == 0.0
        and result.get("golden_fields_equal", True)
    )
    return result


def main() -> int:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "backend/tests/Insolvia.Forms.Tests/Parity/out"
    golden = _golden_module()
    report: dict = {}
    failed: list[str] = []
    rows = []
    for path in sorted((out / "inputs").glob("*.json")):
        case = path.stem
        doc = json.loads(path.read_text(encoding="utf-8"))
        if doc["kind"] == "form":
            golden_form = case if (GOLDENS / f"{case}.json").exists() and "-" not in case else None
            r = compare_pdf((out / "python" / f"{case}.pdf").read_bytes(), (out / "dotnet" / f"{case}.pdf").read_bytes(), golden_form, golden)
            report[case] = r
            rows.append((case, r))
            if not r["ok"]:
                failed.append(case)
            continue
        py_dir, net_dir = out / "python" / case, out / "dotnet" / case
        py_outcome = json.loads((py_dir / "outcome.json").read_text())
        net_outcome = json.loads((net_dir / "outcome.json").read_text()) if (net_dir / "outcome.json").exists() else None
        packet = {"outcome_equal": py_outcome == net_outcome, "parts": {}}
        if py_outcome != net_outcome:
            packet["outcome"] = [py_outcome, net_outcome]
            failed.append(case)
        for part in py_outcome.get("parts", []):
            py_bytes = (py_dir / part).read_bytes()
            net_path = net_dir / part
            if not net_path.exists():
                packet["parts"][part] = {"ok": False, "missing": True}
                failed.append(f"{case}/{part}")
                continue
            net_bytes = net_path.read_bytes()
            if part.endswith(".pdf"):
                r = compare_pdf(py_bytes, net_bytes, None, golden)
                rows.append((f"{case}/{part}", r))
            else:
                r = {"ok": py_bytes == net_bytes}
            packet["parts"][part] = r
            if not r["ok"]:
                failed.append(f"{case}/{part}")
        report[case] = packet

    print("| case | fields | widget /AS | text APs byte-equal | official pages | text runs | goldens reader | max px diff | quirk 1 |")
    print("|---|---|---|---|---|---|---|---|---|")
    for case, r in rows:
        gold = {True: "pass", False: "FAIL", None: "—"}[r.get("golden_fields_equal")]
        print(
            f"| {case} | {r['fields'][0]}/{r['fields'][1]} | {r['widgets'][0]}/{r['widgets'][1]} "
            f"| {r['appearances'][0]}/{r['appearances'][1]} "
            f"| {'equal' if r['official_pages_equal'] and r['overlay_streams_equal'] else 'DIFFER'} "
            f"| {r['text_runs'][1]}/{r['text_runs'][0]} {'equal' if r['text_runs'][2] else 'DIFFER'} "
            f"| {gold} | {max(r['pixels'] or [0.0]):.4f}% | {r['quirk_1_button_values']} |"
        )
    (out / "report.json").write_text(json.dumps(report, indent=1, default=str) + "\n")
    if failed:
        print(f"\nPARITY FAILED: {', '.join(failed)} (details in {out / 'report.json'})", file=sys.stderr)
        return 1
    print(f"\nparity: {len(rows)} PDFs, 0 differences beyond quirk 1", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
