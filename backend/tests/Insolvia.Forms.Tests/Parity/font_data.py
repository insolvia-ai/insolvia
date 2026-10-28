"""Write backend/src/Insolvia.Forms/Pdf/FontData.json from pypdf's own tables.

The .NET appearance-stream generator is a port of pypdf 6.16.2's
TextStreamAppearance, and its widths, font descriptors, named encodings and
glyph list ARE pypdf's data — read here, not retyped. Run from the repo root
in the parity venv (backend/tests/Insolvia.Forms.Tests/Parity/README in
Parity/run.sh); the output is deterministic, so a diff after a run means
pypdf's tables moved.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pypdf
from pypdf._codecs import charset_encoding, encoding_dict_from_named_encoding
from pypdf._codecs.adobe_glyphs import adobe_glyphs
from pypdf._codecs.core_font_metrics import CORE_FONT_METRICS

OUT = Path(__file__).resolve().parents[3] / "src/Insolvia.Forms/Pdf/FontData.json"


def main() -> None:
    assert pypdf.__version__ == "6.16.2", pypdf.__version__
    fonts = {}
    for name, metrics in CORE_FONT_METRICS.items():
        d = metrics.font_descriptor
        fonts[name] = {
            "descriptor": {
                "name": d.name,
                "ascent": float(d.ascent),
                "descent": float(d.descent),
                "flags": int(d.flags),
                "bbox": [float(v) for v in d.bbox],
            },
            # char -> width, plus "default"
            "widths": dict(sorted(metrics.character_widths.items())),
        }
    data = {
        "pypdf": pypdf.__version__,
        "encodings": {name: list(table) for name, table in sorted(charset_encoding.items())},
        "cp1252": [encoding_dict_from_named_encoding("cp1252").get(i) for i in range(256)],
        "glyphs": dict(sorted(adobe_glyphs.items())),
        "fonts": fonts,
    }
    OUT.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=True) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)", file=sys.stderr)


if __name__ == "__main__":
    main()
