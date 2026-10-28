"""Spike #514: field-level equivalence + rendered-page diff, pypdf vs PDFsharp.

Reads <out>/python/<case>.pdf and <out>/dotnet/<case>.pdf for every case in
<out>/inputs/ and reports, per case:

- fields      every AcroForm field node: /FT, /V's value AND its PDF type
- widgets     every button widget's /AS, in page order
- appearances every text/choice widget's /AP /N: decoded stream bytes and
              dictionary (minus /Length and /Filter) — identical streams over
              identical official pages determine the render
- official    each page's official content streams, byte-equal
- text runs   every text-showing operator on every page as (text, x, y,
              size) after the CTM — overlay boxes and stamps included
- golden      for the cases fed the goldens' synthetic values, the goldens'
              OWN reader (tests/unit/test_form_fill.read_form) run over the
              .NET output and compared with the committed golden JSON
- pixels      both files rendered with PDFium (pypdfium2) at 144 dpi, two
              ways: appearance streams only, and with PDFium's form
              environment initialised (what an interactive viewer does);
              the share of pixels that differ at all, per page (max reported)

Run from the repo root with PYTHONPATH=services/api/src (commands: docs/research/dotnet-pdf-engine-2026-10-27.md).
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
from pypdf.generic import ArrayObject, DictionaryObject, StreamObject

from insolvia_api.core.form_templates import latest_form

REPO = Path(__file__).resolve().parents[4]
GOLDEN_CASES = {  # case -> golden file: every plain-form case
    form: form
    for form in (
        "b101 b106ab b106c b106d b106dec b106ef b106g b106h b106i b106j b106j2 "
        "b106sum b107 b108 b113 b121 b122a1 b122a2 b122c1 b122c2 b2010 b2030"
    ).split()
}
OVERLAY_MARKER = b"% insolvia-overlay\n"


def _golden_module():
    path = REPO / "services/api/tests/unit/test_form_fill.py"
    spec = importlib.util.spec_from_file_location("golden_fill", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def qualified(node: DictionaryObject) -> str | None:
    parts = []
    obj = node
    while obj is not None:
        obj = obj.get_object()
        if "/T" in obj:
            parts.append(str(obj["/T"]))
        obj = obj.get("/Parent")
    return ".".join(reversed(parts)) if parts else None


def field_of(widget: DictionaryObject) -> DictionaryObject:
    if "/FT" in widget and "/T" in widget:
        return widget
    return widget["/Parent"].get_object()


def inherited_ft(node: DictionaryObject) -> str | None:
    obj = node
    while obj is not None:
        obj = obj.get_object()
        if "/FT" in obj:
            return str(obj["/FT"])
        obj = obj.get("/Parent")
    return None


def normalize(obj: object, depth: int = 0) -> object:
    """A PDF object as plain data: references resolved (object numbers are
    serialisation detail), numbers as rounded floats, streams by content."""
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
                    meta = normalize(
                        {k: v2 for k, v2 in n.items() if k not in ("/Length", "/Filter")}
                    )
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
    """Per page, the % of pixels that differ at all, PDFium at 144 dpi with
    its form environment initialised — the path an interactive viewer
    takes. (Without init_forms PDFium skips widget annotations entirely, so
    an "appearance-only" render would compare two blank forms.)"""
    out = []
    da, db = pdfium.PdfDocument(a), pdfium.PdfDocument(b)
    da.init_forms()
    db.init_forms()
    for i in range(len(da)):
        ia = da[i].render(scale=2, may_draw_forms=True).to_pil().convert("RGB")
        ib = db[i].render(scale=2, may_draw_forms=True).to_pil().convert("RGB")
        histogram = ImageChops.difference(ia, ib).convert("L").histogram()
        out.append(100.0 * (1 - histogram[0] / (ia.width * ia.height)))
    return out


def template_bytes(case: str, out: Path) -> bytes:
    doc = json.loads((out / "inputs" / f"{case}.json").read_text())
    return (REPO / doc["template"]).read_bytes()


def compare(case: str, out: Path, golden) -> dict:
    py = (out / "python" / f"{case}.pdf").read_bytes()
    net = (out / "dotnet" / f"{case}.pdf").read_bytes()
    a, b = read(py), read(net)

    def match(x: dict, y: dict) -> tuple[int, int, list]:
        keys = set(x) | set(y)
        bad = sorted(k for k in keys if x.get(k) != y.get(k))
        return len(keys) - len(bad), len(keys), bad[:5]

    f_ok, f_all, f_bad = match(a["fields"], b["fields"])
    wa = {(n, i): s for n, i, s in a["widgets"]}
    wb = {(n, i): s for n, i, s in b["widgets"]}
    w_ok, w_all, w_bad = match(wa, wb)
    ap_ok, ap_all, ap_bad = match(a["appearances"], b["appearances"])
    runs_equal = a["runs"] == b["runs"]
    result = {
        "fields": [f_ok, f_all],
        "fields_mismatch": [[k, a["fields"].get(k), b["fields"].get(k)] for k in f_bad],
        "widgets": [w_ok, w_all],
        "widgets_mismatch": [[str(k), wa.get(k), wb.get(k)] for k in w_bad],
        "appearances": [ap_ok, ap_all],
        "appearance_mismatch": [
            [k, (a["appearances"].get(k) or [b""])[0][:160].decode("latin1"),
             (b["appearances"].get(k) or [b""])[0][:160].decode("latin1")]
            for k in ap_bad[:2]
        ],
        "official_pages_equal": a["official"] == b["official"],
        "overlay_streams_equal": a["overlays"] == b["overlays"],
        "text_runs": [sum(a["runs"].values()), sum(b["runs"].values()), runs_equal],
        "text_runs_only_python": [list(k) for k in (a["runs"] - b["runs"])][:5],
        "text_runs_only_dotnet": [list(k) for k in (b["runs"] - a["runs"])][:5],
        "pixels": pixel_diff(py, net),
        # Sanity: the same metric between the blank template and the Python
        # output, so a 0% above is known to be a metric that can see a fill.
        "pixels_template_vs_python": pixel_diff(template_bytes(case, out), py),
        "bytes": [len(py), len(net)],
    }
    if case in GOLDEN_CASES:
        form = GOLDEN_CASES[case]
        doc = json.loads((REPO / f"services/api/tests/unit/goldens/{form}.json").read_text())
        release = latest_form(f"form/{form}")
        assert release.release_id == doc["release"]
        readback = golden.read_form(release, net)
        result["golden_fields_equal"] = readback == doc["fields"]
    return result


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("backend/spikes/pdf/out")
    golden = _golden_module()
    timings_py = json.loads((out / "python" / "timings.json").read_text())
    timings_net = json.loads((out / "dotnet" / "timings.json").read_text())
    report = {}
    print(
        "| case | fields | widget /AS | text APs byte-equal | official pages | "
        "text runs | goldens reader | max px diff | (template→py) | ms py / .NET |"
    )
    print("|---|---|---|---|---|---|---|---|---|---|")
    for path in sorted((out / "inputs").glob("*.json")):
        case = path.stem
        r = compare(case, out, golden)
        report[case] = r
        gold = {True: "pass", False: "FAIL", None: "—"}[r.get("golden_fields_equal")]
        print(
            f"| {case} | {r['fields'][0]}/{r['fields'][1]} | {r['widgets'][0]}/{r['widgets'][1]} "
            f"| {r['appearances'][0]}/{r['appearances'][1]} "
            f"| {'equal' if r['official_pages_equal'] else 'DIFFER'} "
            f"| {r['text_runs'][1]}/{r['text_runs'][0]} {'equal' if r['text_runs'][2] else 'DIFFER'} "
            f"| {gold} "
            f"| {max(r['pixels']):.4f}% | ({max(r['pixels_template_vs_python']):.2f}%) "
            f"| {timings_py[case]} / {timings_net[case]} |"
        )
    (out / "report.json").write_text(json.dumps(report, indent=1, default=str) + "\n")


if __name__ == "__main__":
    main()
