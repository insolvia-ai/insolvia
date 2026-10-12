#!/usr/bin/env bash
#
# Unit tests for release-diff-base.sh — no network, no git, no GitHub.
#
#   .github/scripts/release-diff-base.test.sh
#
# Each case feeds a commit list on stdin and a fake STATUS_LOOKUP that reads
# commit states from a fixture (`<sha> <state>` per line; a state of `ERROR`
# makes the lookup fail, as `gh api` does when the API or the token does).
# scripts/dev-test-unit.sh runs this as its `ci-scripts` area, so the
# pre-push hook runs it whenever this directory or release.yml changes.
#
# Bash 3.2 (macOS) compatible.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="$HERE/release-diff-base.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# The fake lookup: prints the fixture's state for $1, `missing` when the
# fixture does not name it, and exits 1 on ERROR. It also logs every SHA it
# was asked about, so a case can assert how far the walk went.
cat >"$TMP/lookup" <<'EOF'
#!/usr/bin/env bash
echo "$1" >>"$FIXTURE.calls"
state="$(awk -v s="$1" '$1 == s { print $2 }' "$FIXTURE")"
[ "$state" = "ERROR" ] && exit 1
echo "${state:-missing}"
EOF
chmod +x "$TMP/lookup"

A=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
B=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
C=cccccccccccccccccccccccccccccccccccccccc
D=dddddddddddddddddddddddddddddddddddddddd
PREV=eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee
ZERO=0000000000000000000000000000000000000000

pass=0
fail=0

# run <name> <expected stdout> <expected lookup calls> <commits> <fixture> [VAR=value …]
run() {
  local name="$1" want="$2" want_calls="$3" commits="$4" fixture="$5"
  shift 5
  local f="$TMP/fixture.$((pass + fail))"
  printf '%s' "$fixture" >"$f"
  : >"$f.calls"
  local got calls
  got="$(printf '%s' "$commits" |
    env ALL=false BEFORE= "$@" FIXTURE="$f" STATUS_LOOKUP="$TMP/lookup" "$SUT" 2>"$f.err")" || {
    got="<exit $?>"
  }
  calls="$(wc -l <"$f.calls" | tr -d ' ')"
  if [ "$got" = "$want" ] && [ "$calls" = "$want_calls" ]; then
    pass=$((pass + 1))
    printf '  ok    %s\n' "$name"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n        want %s after %s lookup(s), got %s after %s\n' \
      "$name" "$want" "$want_calls" "$got" "$calls"
    sed 's/^/        | /' "$f.err"
  fi
}

nl=$'\n'

run "the parent is green: it is the base" \
  "$A" 1 "$A$nl$B$nl" "$A success$nl$B success$nl"

run "skips commits whose release fell short, to the newest green one" \
  "$C" 3 "$A$nl$B$nl$C$nl$D$nl" "$A pending$nl$B failure$nl$C success$nl$D success$nl"

run "a commit with no status at all is not a base" \
  "$B" 2 "$A$nl$B$nl" "$B success$nl"

run "nothing green in the window: deploy everything, having checked it all" \
  "all" 3 "$A$nl$B$nl$C$nl" "$A pending$nl" BEFORE="$PREV"

run "an empty candidate list (a root commit): deploy everything" \
  "all" 0 "" "" BEFORE="$PREV"

run "all=true wins without a single lookup" \
  "all" 0 "$A$nl" "$A success$nl" ALL=true BEFORE="$PREV"

run "a failed lookup falls back to the push's previous tip" \
  "$PREV" 2 "$A$nl$B$nl$C$nl" "$A pending$nl${B} ERROR$nl$C success$nl" BEFORE="$PREV"

run "a failed lookup with no previous tip deploys everything" \
  "all" 1 "$A$nl" "$A ERROR$nl"

run "a failed lookup with an all-zero previous tip deploys everything" \
  "all" 1 "$A$nl" "$A ERROR$nl" BEFORE="$ZERO"

run "blank lines in the candidate list are ignored" \
  "$B" 1 "$nl$B$nl$nl" "$B success$nl"

run "only the exact word success counts" \
  "all" 2 "$A$nl$B$nl" "$A successful$nl$B SUCCESS$nl"

# Bad usage is an error, not an answer.
if "$SUT" unexpected-arg </dev/null >/dev/null 2>&1; then
  fail=$((fail + 1)); echo "  FAIL  a positional argument is refused"
else
  pass=$((pass + 1)); echo "  ok    a positional argument is refused"
fi

echo "release-diff-base: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
