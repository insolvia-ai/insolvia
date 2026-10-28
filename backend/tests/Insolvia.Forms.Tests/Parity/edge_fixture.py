"""Write Fixtures/edge.pdf and Fixtures/edge.expected.json — the appearance
paths no court form exercises, with pypdf 6.16.2's own output as the answer.

The court PDFs only ever give the generator /Helv text and combo fields on
unrotated pages. pypdf also handles — and Insolvia.Forms ports — list boxes,
widget rotation (/MK /R), a rotated page, other core fonts by their metrics,
a font with its own /Widths, a /DA font missing from /DR, alignment, comb
cells at a fixed size, multiline wrap-and-shrink, a thick /BS border, and
non-ASCII text. This script builds one synthetic page carrying each, fills it
the way services/api fills (update_page_form_field_values,
auto_regenerate=False), and records every widget's resulting /AP /N stream
and dictionary. AppearanceTests fills the same PDF with the same values and
must produce the same streams. Run from the repo root in the parity venv;
deterministic, synthetic data only.
"""

from __future__ import annotations

import io
import json
from pathlib import Path

import pypdf
from pypdf import PdfWriter
from pypdf.generic import (
    ArrayObject,
    DictionaryObject,
    FloatObject,
    NameObject,
    NumberObject,
    TextStringObject,
)

OUT = Path(__file__).resolve().parents[1] / "Fixtures"


def name(value: str) -> NameObject:
    return NameObject(value)


def font(base: str, widths: list[int] | None = None, first: int = 32) -> DictionaryObject:
    d = DictionaryObject({
        name("/Type"): name("/Font"),
        name("/Subtype"): name("/Type1"),
        name("/BaseFont"): name(base),
        name("/Encoding"): name("/WinAnsiEncoding"),
    })
    if widths is not None:
        d[name("/FirstChar")] = NumberObject(first)
        d[name("/LastChar")] = NumberObject(first + len(widths) - 1)
        d[name("/Widths")] = ArrayObject(NumberObject(w) for w in widths)
    return d


# (name, rect, field type, flags, DA, extra field keys, value)
FIELDS = [
    ("plain", (40, 700, 300, 720), "/Tx", 0, "/Helv 10 Tf 0 g", {}, "Plain text"),
    ("auto", (40, 670, 200, 690), "/Tx", 0, "/Helv 0 Tf 0 g", {}, "Auto-sized text"),
    ("right", (320, 700, 560, 720), "/Tx", 0, "/Helv 9 Tf 0 g", {"/Q": NumberObject(2)}, "Right aligned"),
    ("center", (320, 670, 560, 690), "/Tx", 0, "/Helv 9 Tf 0 g", {"/Q": NumberObject(1)}, "Centred"),
    ("comb", (40, 640, 160, 660), "/Tx", 1 << 24, "/Helv 10 Tf 0 g", {"/MaxLen": NumberObject(6)}, "AB12"),
    ("multi", (40, 560, 200, 630), "/Tx", 1 << 12, "/Helv 0 Tf 0 g", {}, "A multiline value that wraps across several lines of the box and then shrinks until it fits"),
    ("multifixed", (220, 560, 400, 630), "/Tx", 1 << 12, "/Helv 8 Tf 0 g", {}, "Line one\nLine two (with parens) \\ slash"),
    ("thick", (420, 560, 560, 600), "/Tx", 0, "/Helv 10 Tf 0 g", {"/BS": DictionaryObject({name("/W"): NumberObject(3), name("/S"): name("/B")})}, "Beveled"),
    ("halfborder", (420, 520, 560, 545), "/Tx", 0, "/Helv 10 Tf 0 g", {"/BS": DictionaryObject({name("/W"): FloatObject(0.5), name("/S"): name("/S")})}, "Half-point border"),
    ("times", (40, 520, 300, 545), "/Tx", 0, "/TiRo 11 Tf 0 0 1 rg", {}, "Times Roman, in blue"),
    ("widths", (40, 490, 300, 510), "/Tx", 0, "/WiFo 10 Tf 0 g", {}, "Own widths"),
    ("courier", (40, 460, 300, 480), "/Tx", 0, "/Courier 10 Tf 0 g", {}, "Courier, not in DR"),
    ("unknownfont", (40, 430, 300, 450), "/Tx", 0, "/NoSuch 10 Tf 0 g", {}, "Falls back to Helvetica"),
    ("rotated", (320, 380, 360, 500), "/Tx", 0, "/Helv 10 Tf 0 g", {"/MK": DictionaryObject({name("/R"): NumberObject(90)})}, "Turned"),
    ("listbox", (40, 330, 200, 410), "/Ch", 0, "/Helv 10 Tf 0 g", {"/Opt": ArrayObject([TextStringObject("Alpha"), TextStringObject("Beta"), TextStringObject("Gamma")])}, "Beta"),
    ("listauto", (220, 330, 380, 410), "/Ch", 0, "/Helv 0 Tf 0 g", {"/Opt": ArrayObject([TextStringObject("One"), TextStringObject("Two")])}, "Two"),
    ("combo", (400, 330, 560, 350), "/Ch", 1 << 17, "/Helv 10 Tf 0 g", {"/Opt": ArrayObject([TextStringObject("Yes"), TextStringObject("No")])}, "No"),
    ("accents", (40, 290, 560, 310), "/Tx", 0, "/Helv 10 Tf 0 g", {}, "José Núñez-Ibáñez, Zoë, Søren, Æsir — “quoted” €5 ½"),
]


def build() -> bytes:
    writer = PdfWriter()
    page = writer.add_blank_page(width=612, height=792)
    helv = writer._add_object(font("/Helvetica"))
    tiro = writer._add_object(font("/Times-Roman"))
    wifo = writer._add_object(font("/Helvetica-Bold", widths=[300] * 95))
    annots = ArrayObject()
    fields = ArrayObject()
    for field_name, rect, ft, flags, da, extra, _ in FIELDS:
        widget = DictionaryObject({
            name("/Type"): name("/Annot"),
            name("/Subtype"): name("/Widget"),
            name("/FT"): name(ft),
            name("/T"): TextStringObject(field_name),
            name("/Rect"): ArrayObject(NumberObject(v) for v in rect),
            name("/DA"): TextStringObject(da),
            name("/F"): NumberObject(4),
            name("/P"): page.indirect_reference,
        })
        if flags:
            widget[name("/Ff")] = NumberObject(flags)
        for key, value in extra.items():
            widget[name(key)] = value
        ref = writer._add_object(widget)
        annots.append(ref)
        fields.append(ref)
    page[name("/Annots")] = annots
    writer._root_object[name("/AcroForm")] = DictionaryObject({
        name("/Fields"): fields,
        name("/DA"): TextStringObject("/Helv 0 Tf 0 g"),
        name("/DR"): DictionaryObject({
            name("/Font"): DictionaryObject({name("/Helv"): helv, name("/TiRo"): tiro, name("/WiFo"): wifo}),
        }),
    })
    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()


def fill(template: bytes) -> bytes:
    writer = PdfWriter(clone_from=io.BytesIO(template))
    for field_name, *_, value in FIELDS:
        for page in writer.pages:
            writer.update_page_form_field_values(page, {field_name: value}, auto_regenerate=False)
    out = io.BytesIO()
    writer.write(out)
    return out.getvalue()


def appearances(data: bytes) -> dict[str, dict]:
    reader = pypdf.PdfReader(io.BytesIO(data))
    out = {}
    for annot in reader.pages[0]["/Annots"]:
        widget = annot.get_object()
        n = widget["/AP"]["/N"].get_object()
        out[str(widget["/T"])] = {
            "stream": n.get_data().decode("latin-1"),
            "bbox": [float(v) for v in n["/BBox"]],
            "matrix": [float(v) for v in n["/Matrix"]] if "/Matrix" in n else None,
            "fonts": sorted(str(k) for k in n["/Resources"]["/Font"]),
            "da": str(widget["/DA"]),
        }
    acro = reader.trailer["/Root"]["/AcroForm"]
    out["$dr"] = {k: str(v.get_object().get("/BaseFont")) for k, v in sorted(acro["/DR"]["/Font"].items())}
    return out


def main() -> None:
    assert pypdf.__version__ == "6.16.2", pypdf.__version__
    template = build()
    OUT.mkdir(exist_ok=True)
    (OUT / "edge.pdf").write_bytes(template)
    expected = {
        "values": {field_name: value for field_name, *_, value in FIELDS},
        "appearances": appearances(fill(template)),
    }
    (OUT / "edge.expected.json").write_text(json.dumps(expected, indent=1, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
