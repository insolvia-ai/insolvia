#!/usr/bin/env python3
"""Cut Insolvia's wordmark and app icon out of the brand's own display face.

    ./scripts/render-brand-marks.sh          # the wrapper; it owns the toolchain

Run it when the display FACE changes (brand/fonts.json), when the letters in
either mark change, or when the icon's geometry below is retuned. Not on every
build: the outputs are committed, because a build that re-cuts type is a build
that changes the logo when an upstream font ships a revision.

WHAT IT WRITES

    brand/wordmark.svg                       "Insolvia." as outlines
    brand/icon.svg                           the "I" tile as outlines
    apps/insolvia_app/public/favicon.ico     16/32/48, for the tab strip
    apps/insolvia_app/public/icons/*.png     192/512, plain and maskable

The two SVGs carry NO COLOUR. `npm run tokens` reads them, layers the palette
on, and writes every coloured copy a consumer needs — the same bargain
brand/colors.json already makes, extended to shape. That is why this script
does not take a palette for them, and why it must never be taught to.

The rasters are the exception, and only because a PNG cannot defer its colours
to render time. They take the icon's two roles from brand/colors.json here, so
a re-brand does mean re-running this script — `tool/render-brand-marks.ts`
prints that reminder when it notices the SVGs and the PNGs disagree.

ONE GEOMETRY, TWO RENDERERS, AND WHY THAT IS SAFE

The SVGs come out of fontTools' path pens; the PNGs come out of the same
glyphs through FreeType. Two renderers of one mark is normally how a logo
drifts. It does not here, because both read the same `place()` output — the
shaped, positioned, cropped glyph run — and neither is allowed to nudge
anything afterwards. Keep it that way: geometry decisions belong in `place()`
and in ICON below, never in either writer.

WHY OUTLINES AND NOT TEXT

Every consumer of these files renders them somewhere the brand's webfont is
not: Cognito serves the logo as an <img> from AWS's origin, and a favicon has
no document to inherit from. A <text> element would silently fall back to
whatever the browser has, which is never Cormorant.

The letterforms are Cormorant Garamond, SIL OFL 1.1 — the notice that ships
with them is apps/insolvia_app/public/fonts/OFL.txt. OFL 1.1 §1 covers
outlines extracted from a face, so these files are under the same terms.
"""

from __future__ import annotations

import json
import sys
from io import BytesIO
from pathlib import Path

import uharfbuzz as hb
from fontTools.misc.transform import Transform
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.freetypePen import FreeTypePen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent

# The decompressed face, filled in by open_face() — see the note there.
FACE_BYTES = b""

# The display face, at the weight brand/fonts.json insists on. Cormorant is
# drawn for large sizes and thins out badly below 700 — the same reason the
# app's Heading pins it — and a logo is the one place that matters most.
FACE = ROOT / "apps/insolvia_app/public/fonts/cormorant-700.woff2"

# The app's own letterSpacing on the wordmark, as a ratio of the em, so the
# cut mark and the live <Wordmark> component set identically.
TRACKING = -0.5 / 20

# The wordmark's box, once the letters are cropped to their ink.
#
# THE BOX RATIO IS THE SIZE CONTROL, and it is the only one there is. Cognito's
# managed-login page has no logo size setting, and how it sizes one is not
# guessable — it was measured off the rendered page:
#
#   • the uploaded SVG is REWRITTEN to 300px wide, whatever it was;
#   • the page then renders it at a fixed HEIGHT of 60px.
#
# So the rendered width is 60 × the box's aspect ratio, and nothing else. A
# tighter crop makes the mark WIDER on the page, not smaller; horizontal
# padding does nothing at all, because the rewrite normalises it away. Padding
# the box vertically is what shrinks the letters.
#
# Cut tight, "Insolvia." is 4.08:1 and rendered ~240px wide — as wide as the
# sign-in card, reading as a second heading above Cognito's own "Sign in",
# which is AWS's string and cannot be removed or edited from the branding
# document. At 2.4 it renders ~144px: identity, not a headline.
#
# BEWARE WHEN RETUNING THIS. Both the page and the asset are served through
# CloudFront and cache hard; three applies in a row appeared to change nothing
# because the browser was holding an older version. Verify by reading the
# <img>'s naturalWidth/naturalHeight, not by looking at a screenshot.
WORDMARK = {
    # Box width : height. Rendered width on the sign-in page is 60 × this.
    # Cognito refuses a LOGO asset outside 1:1–4:1 ("Invalid file dimension…
    # must have a width:height ratio between 1:1 and 4:1"), so stay inside it.
    "ratio": 2.4,
}

# The icon, in units of its own side. Everything about the tile is here.
ICON = {
    "letter": "I",
    # A rounded square, not a squircle. The brand states small radii (see
    # brand/radii.json); this is the same restraint at icon scale, and it
    # still resolves at the 16px favicon, where a 22% iOS radius turns the
    # tile into a blob.
    "radius": 0.12,
    # Cap height as a fraction of the side. Cormorant's I is narrow and
    # serifed, so it can sit larger than a sans would without crowding.
    "cap": 0.58,
    # Optical centring: the serifed I is symmetric, but its bounding box sits
    # marginally high against the baseline, so the mathematical centre reads
    # low. Nudge up by this fraction of the side.
    "rise": 0.015,
    # Maskable icons are cropped to a circle of 80% width by some launchers,
    # so everything must sit inside that. Shrink the tile's content, keep the
    # ground full-bleed.
    "maskable_safe": 0.70,
}


def place(font: TTFont, text: str, tracking: float) -> tuple[list, tuple[float, ...]]:
    """Shape `text`, position each glyph, and return the run with its ink box.

    HarfBuzz does the shaping, so the kerning is the font's own rather than a
    guess — Cormorant kerns "In" and "vi" noticeably. It cannot read WOFF2, so
    the caller hands us a font already decompressed.
    """
    upm = font["head"].unitsPerEm
    glyphs = font.getGlyphSet()
    order = font.getGlyphOrder()

    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(hb.Font(hb.Face(hb.Blob(FACE_BYTES))), buf, {"kern": True, "liga": True})

    run, pen_x = [], 0.0
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        run.append((order[info.codepoint], pen_x + pos.x_offset, float(pos.y_offset)))
        pen_x += pos.x_advance + tracking * upm

    bounds = BoundsPen(glyphs)
    for name, dx, dy in run:
        glyphs[name].draw(TransformPen(bounds, Transform(1, 0, 0, 1, dx, dy)))
    return run, bounds.bounds


def svg(paths: dict[str, str], width: float, height: float, label: str, note: str) -> str:
    body = "\n".join(f'  <path id="{k}" d="{v}"/>' for k, v in paths.items())
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {round(width)} {round(height)}"'
        f' role="img" aria-label="{label}">\n'
        f"  <!--\n"
        f"    GENERATED by scripts/render-brand-marks.sh — do not hand-edit.\n"
        f"    {note}\n"
        f"    No colour here on purpose: `npm run tokens` writes the coloured copies.\n"
        f"  -->\n{body}\n</svg>\n"
    )


def open_face() -> TTFont:
    """The face, decompressed in memory.

    HarfBuzz has no WOFF2 support, so shaping needs the plain SFNT bytes; the
    module-level FACE_BYTES is where `place` reads them. Nothing is written to
    disk — the .woff2 in the app's public directory stays the only copy.
    """
    global FACE_BYTES
    font = TTFont(FACE)
    font.flavor = None
    buf = BytesIO()
    font.save(buf)
    FACE_BYTES = buf.getvalue()
    return TTFont(BytesIO(FACE_BYTES))


def write_wordmark(font: TTFont) -> None:
    glyphs = font.getGlyphSet()
    period = font.getBestCmap()[ord(".")]
    run, (xmin, ymin, xmax, ymax) = place(font, "Insolvia.", TRACKING)

    # The letters span the box's full width — only the height is padded, which
    # is what sets the rendered size. See WORDMARK above.
    width, ink_h = xmax - xmin, ymax - ymin
    height = width / WORDMARK["ratio"]
    left, top = 0.0, (height - ink_h) / 2  # centre the ink vertically

    pens = {"mark": SVGPathPen(glyphs), "dot": SVGPathPen(glyphs)}
    for name, dx, dy in run:
        # translate the ink box to the origin, then flip y — fonts are y-up
        role = "dot" if name == period else "mark"
        glyphs[name].draw(
            TransformPen(pens[role], Transform(1, 0, 0, -1, dx - xmin + left, ymax + top + dy))
        )

    out = ROOT / "brand/wordmark.svg"
    out.write_text(
        svg(
            {k: v.getCommands() for k, v in pens.items()},
            width,
            height,
            "Insolvia",
            '"Insolvia." shaped, tracked, outlined and cropped to the ink. `mark` is the '
            "letters, `dot` the brass full stop — the same two roles the app's <Wordmark> uses.",
        )
    )
    print(f"  {out.relative_to(ROOT)}  {round(width)}x{round(height)}  {width / height:.2f}:1")


def icon_geometry(font: TTFont, side: float) -> tuple[str, Transform]:
    """The letter's glyph name and the transform that centres it on a `side` tile."""
    glyphs = font.getGlyphSet()
    name = font.getBestCmap()[ord(ICON["letter"])]
    bounds = BoundsPen(glyphs)
    glyphs[name].draw(bounds)
    xmin, ymin, xmax, ymax = bounds.bounds

    scale = (ICON["cap"] * side) / (ymax - ymin)
    dx = (side - (xmax - xmin) * scale) / 2 - xmin * scale
    dy = (side + (ymax - ymin) * scale) / 2 + ymin * scale - ICON["rise"] * side
    return name, Transform(scale, 0, 0, -scale, dx, dy)


def write_icon(font: TTFont) -> None:
    side = 512.0
    glyphs = font.getGlyphSet()
    name, transform = icon_geometry(font, side)

    pen = SVGPathPen(glyphs)
    glyphs[name].draw(TransformPen(pen, transform))
    r = ICON["radius"] * side
    ground = (
        f"M{r},0 H{side - r} A{r},{r} 0 0 1 {side},{r} V{side - r} "
        f"A{r},{r} 0 0 1 {side - r},{side} H{r} A{r},{r} 0 0 1 0,{side - r} "
        f"V{r} A{r},{r} 0 0 1 {r},0 Z"
    )

    out = ROOT / "brand/icon.svg"
    out.write_text(
        svg(
            {"ground": ground, "letter": pen.getCommands()},
            side,
            side,
            "Insolvia",
            'The "I" tile. `ground` is the dark chrome square, `letter` the ivory glyph — '
            "the icon is dark in both schemes, like the case rail, because it is chrome.",
        )
    )
    print(f"  {out.relative_to(ROOT)}  {round(side)}x{round(side)}")


def raster(font: TTFont, side: int, ground: str, letter: str, inset: float = 1.0) -> Image.Image:
    """The icon tile as pixels, from the same glyph the SVG uses."""
    img = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    tile = round(side * inset)
    pad = (side - tile) // 2

    ImageDraw.Draw(img).rounded_rectangle(
        [pad, pad, pad + tile - 1, pad + tile - 1], radius=ICON["radius"] * tile, fill=ground
    )

    name, transform = icon_geometry(font, float(tile))
    pen = FreeTypePen(font.getGlyphSet())
    font.getGlyphSet()[name].draw(TransformPen(pen, Transform(1, 0, 0, -1, 0, tile).transform(transform)))
    # FreeTypePen hands back luminance-plus-alpha; the alpha band is the
    # antialiased coverage, and the only part of it that means anything here.
    glyph = pen.image(width=tile, height=tile).getchannel("A")

    ink = Image.new("RGBA", (tile, tile), letter)
    ink.putalpha(glyph)
    img.alpha_composite(ink, (pad, pad))
    return img


def write_rasters(font: TTFont) -> None:
    colors = json.loads((ROOT / "brand/colors.json").read_text())
    # The tile is chrome — dark in both schemes, like the case rail — so both
    # roles come from the DARK scheme whatever the viewer's browser is doing.
    ground, letter = colors["dark"]["bg"], colors["dark"]["ink"]

    icons = ROOT / "apps/insolvia_app/public/icons"
    icons.mkdir(parents=True, exist_ok=True)
    for size in (192, 512):
        raster(font, size, ground, letter).save(icons / f"Icon-{size}.png")
        raster(font, size, ground, letter, ICON["maskable_safe"]).save(icons / f"Icon-maskable-{size}.png")
        print(f"  {(icons / f'Icon-{size}.png').relative_to(ROOT)} (+ maskable)")

    ico = ROOT / "apps/insolvia_app/public/favicon.ico"
    raster(font, 256, ground, letter).save(ico, sizes=[(16, 16), (32, 32), (48, 48)])
    print(f"  {ico.relative_to(ROOT)}  16/32/48")


def main() -> int:
    if not FACE.exists():
        print(f"missing {FACE.relative_to(ROOT)} — run ./scripts/fetch-brand-fonts.sh", file=sys.stderr)
        return 1

    print(f"cutting marks from {FACE.relative_to(ROOT)}")
    font = open_face()
    write_wordmark(font)
    write_icon(font)
    write_rasters(font)
    print("done. Run `npm run tokens` to colour the SVGs for each consumer.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
