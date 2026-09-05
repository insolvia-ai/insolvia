#!/usr/bin/env bash
#
# Re-download the self-hosted brand faces into apps/insolvia_app/public/fonts.
#
# The families are declared in brand/fonts.json; this fetches the actual bytes.
# Run it when a family or weight in that file changes — NOT on every build. The
# .woff2 files are committed, because a build that reaches out to a third party
# for a font is a build that breaks when that third party does.
#
# LATIN SUBSET ONLY, which is what keeps all five faces under 80KB together.
# Google Fonts serves a different subset per Accept-Encoding/UA, so this asks
# with a modern browser UA to get woff2 rather than the ttf fallback, then takes
# the block the CSS labels `/* latin */`.
#
# Licences: all three families are SIL OFL 1.1. The notice that has to ship
# with them is apps/insolvia_app/public/fonts/OFL.txt — if you add a family
# here, add it there too.
set -euo pipefail

UA="Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/120 Safari/537.36"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/apps/insolvia_app/public/fonts"

mkdir -p "$OUT"

grab() { # grab <family-query> <weight> <output-name>
  local query="$1" weight="$2" name="$3" css url
  css=$(curl -fsS -m 30 -A "$UA" \
    "https://fonts.googleapis.com/css2?family=${query}:wght@${weight}&display=swap")
  url=$(printf '%s' "$css" \
    | awk '/\/\* latin \*\//{found=1} found && /src: url\(/{print; exit}' \
    | grep -oE 'https://[^)]*woff2')
  if [ -z "$url" ]; then
    echo "no latin woff2 for ${query} ${weight} — did the CSS format change?" >&2
    return 1
  fi
  curl -fsS -m 30 -o "$OUT/$name" "$url"
  printf '  %-26s %6s bytes\n' "$name" "$(wc -c < "$OUT/$name" | tr -d ' ')"
}

echo "fetching brand faces into ${OUT#"$ROOT/"}"
grab "Archivo"       600 "archivo-600.woff2"
grab "Archivo"       700 "archivo-700.woff2"
grab "Public+Sans"   400 "public-sans-400.woff2"
grab "Public+Sans"   600 "public-sans-600.woff2"
grab "IBM+Plex+Mono" 400 "ibm-plex-mono-400.woff2"
echo "done. @font-face rules live in apps/insolvia_app/public/index.html."
