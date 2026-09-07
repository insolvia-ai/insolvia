#!/usr/bin/env bash
#
# Re-cut the brand marks — the wordmark and the app icon — from the display
# face. The work is in render-brand-marks.py beside this file; this wrapper
# exists only to give it a toolchain.
#
# Run it when brand/fonts.json changes its `heading` family, when either mark's
# letters change, or when the icon geometry in that script is retuned. NOT on
# every build: the outputs are committed, and re-cutting type on every build is
# how a logo changes because an upstream font shipped a revision.
#
#   ./scripts/render-brand-marks.sh          # re-cut
#   npm run tokens                           # then colour them per consumer
#
# WHY A VENV. Every other Python script in this directory is stdlib-only so a
# fresh clone can run it with bare python3 — see the note at the top of
# ingest-ust-data.py. Cutting type cannot be: it needs a shaping engine
# (HarfBuzz, for the font's own kerning), a font toolkit, a rasteriser and an
# image library. All four are pip wheels with no system libraries behind them,
# so the venv is self-contained and disposable. It lives under .cache/, which
# is gitignored; delete it and this script rebuilds it.
#
# The alternative — adding four Python packages to a service's requirements —
# would put a build dependency of a once-a-year task into something that ships.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENV="$ROOT/.cache/brand-marks-venv"

# Pinned, because the outputs are committed and a diff in them should mean
# somebody changed the brand — not that fontTools rounded a control point
# differently this month.
DEPS=(
  "fonttools==4.60.2"   # glyph outlines, WOFF2, the path pens
  "brotli==1.1.0"       #   ↳ what fontTools decompresses WOFF2 with
  "uharfbuzz==0.51.1"   # shaping: kerning and ligatures, the font's own
  "freetype-py==2.5.1"  # rasterising the same outlines for the PNG/ICO set
  "pillow==11.3.0"      # the tile, the compositing, the ICO container
)

if [ ! -x "$VENV/bin/python" ]; then
  echo "creating ${VENV#"$ROOT/"}"
  python3 -m venv "$VENV"
fi

# Idempotent and quiet when already satisfied, so a re-run costs nothing.
"$VENV/bin/pip" install --quiet --disable-pip-version-check "${DEPS[@]}"

exec "$VENV/bin/python" "$ROOT/scripts/render-brand-marks.py" "$@"
