#!/usr/bin/env bash
#
# Re-cut the brand marks — the wordmark and the app icon — from the display
# face. The work is in tool/cut-brand-marks.ts; this wrapper is the name
# everything else points at (the generated files' headers, `npm run
# tokens:check`'s advice).
#
# Run it when brand/fonts.json changes its `heading` family, when either mark's
# letters change, when the icon geometry in that file is retuned, or when a
# brand colour changes (the rasters bake the colour in). NOT on every build:
# the outputs are committed, and re-cutting type on every build is how a logo
# changes because an upstream font shipped a revision.
#
#   ./scripts/render-brand-marks.sh          # re-cut
#   npm run tokens                           # then colour them per consumer
#
# Plain `node`, no venv: the shaper, the WOFF2 decoder and the rasteriser are
# root devDependencies, pinned exactly in package.json, so `npm ci` at the
# root is the whole setup.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ ! -d "$ROOT/node_modules/harfbuzzjs" ]; then
  echo "the root's node_modules is missing — run \`npm ci\` at the repo root first" >&2
  exit 1
fi

exec node "$ROOT/tool/cut-brand-marks.ts" "$@"
