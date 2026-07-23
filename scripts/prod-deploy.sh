#!/usr/bin/env bash
#
# Dispatch a production deploy workflow from the command line.
#
# Every production workflow in this repo is `workflow_dispatch`-only — nothing
# deploys to prod on a push to main (only `infra/envs/shared` auto-applies).
# That is deliberate, and it makes "deploy prod" a thing you have to go and do
# in the GitHub UI. This script is that button, with the checks the UI does not
# give you: it shows the exact commit GitHub will build, tells you when your
# local checkout disagrees with it, and makes you confirm before anything runs.
#
# It dispatches; it never applies anything locally. All AWS access happens in
# the workflow via OIDC, so this script needs a `gh` login and nothing else —
# no AWS credentials on your machine.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

log()  { printf '\033[1;34m[prod-deploy]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not installed."
}

# target → workflow file, human name. Keep in sync with .github/workflows/.
# `shared-infra` is not a prod environment, but it is account-wide and
# dispatchable, so it belongs on the same dashboard as the prod deploys.
targets() {
  cat <<'EOF'
api|api-prod.yml|API · Deploy · Production
app|app-prod.yml|App · Deploy · Production
marketing|marketing-prod.yml|Marketing · Deploy · Production
shared-infra|shared-infra-deploy.yml|Infra · Terraform apply · Shared
EOF
}

workflow_for() { targets | awk -F'|' -v t="$1" '$1 == t { print $2 }'; }
name_for()     { targets | awk -F'|' -v t="$1" '$1 == t { print $3 }'; }
target_names() { targets | cut -d'|' -f1 | paste -sd' ' -; }

usage() {
  cat <<EOF
Dispatch a production deploy workflow.

Usage: ${0##*/} [options] <target>

Targets:
$(targets | awk -F'|' '{ printf "  %-14s %-34s (%s)\n", $1, $3, $2 }')

Options:
  --ref REF      Git ref to deploy from (default: main)
  --yes, -y      Skip the confirmation prompt
  --no-watch     Dispatch and return instead of following the run
  --list, -l     Show every target's most recent run, then exit
  --help, -h     Show this help

Examples:
  ${0##*/} --list                 # what ran last, and how it went
  ${0##*/} api                    # deploy the API to production from main
  ${0##*/} --yes --no-watch app   # fire and forget
EOF
}

TARGET=""
REF="main"
AUTO_APPROVE=0
WATCH=1
LIST_ONLY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) [[ $# -ge 2 ]] || die "--ref requires a value."; REF="$2"; shift ;;
    --yes|-y) AUTO_APPROVE=1 ;;
    --no-watch) WATCH=0 ;;
    --list|-l) LIST_ONLY=1 ;;
    --help|-h) usage; exit 0 ;;
    -*) die "Unknown option: $1" ;;
    *)
      [[ -z "$TARGET" ]] || die "Only one target at a time (got '$TARGET' and '$1')."
      TARGET="$1"
      ;;
  esac
  shift
done

require_command gh
require_command git
gh auth status >/dev/null 2>&1 || die "Not logged in to GitHub. Run: gh auth login"

REPO="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"

if [[ "$LIST_ONLY" -eq 1 ]]; then
  while IFS='|' read -r target workflow name; do
    printf '\n\033[1m%s\033[0m  (%s)\n' "$target" "$name"
    gh run list --repo "$REPO" \
      --workflow "$workflow" --limit 3 \
      --json displayTitle,status,conclusion,createdAt,headSha,url \
      --template '{{range .}}  {{printf "%.7s" .headSha}}  {{.createdAt | timeago}}	{{if .conclusion}}{{.conclusion}}{{else}}{{.status}}{{end}}	{{.url}}
{{end}}' 2>/dev/null || printf '  (no runs yet)\n'
  done < <(targets)
  exit 0
fi

[[ -n "$TARGET" ]] || { usage; exit 1; }

WORKFLOW="$(workflow_for "$TARGET")"
[[ -n "$WORKFLOW" ]] || die "Unknown target '$TARGET'. Valid targets: $(target_names)"
DISPLAY_NAME="$(name_for "$TARGET")"
[[ -f "$REPO_ROOT/.github/workflows/$WORKFLOW" ]] ||
  die "Workflow '$WORKFLOW' does not exist — this script is out of sync with .github/workflows/."

# Ask GitHub what the ref points at rather than trusting the local checkout.
# The workflow builds from the remote ref, so that is the only SHA that
# matters — a stale or dirty working tree must not change what is reported.
REMOTE_SHA="$(gh api "repos/$REPO/commits/$REF" --jq .sha 2>/dev/null)" ||
  die "Ref '$REF' does not exist on $REPO."
REMOTE_SUBJECT="$(gh api "repos/$REPO/commits/$REF" --jq .commit.message | head -1)"

log "Repository:  $REPO"
log "Workflow:    $DISPLAY_NAME ($WORKFLOW)"
log "Ref:         $REF"
log "Commit:      ${REMOTE_SHA:0:7}  $REMOTE_SUBJECT"

if [[ "$REF" != "main" ]]; then
  warn "Deploying from '$REF', not main. Infra applies are supposed to run from merged main."
fi

# The single most common way to be surprised by this script: dispatching while
# the change you mean to ship is still sitting uncommitted or unpushed. The
# workflow will not see it.
LOCAL_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
if [[ -n "$LOCAL_SHA" && "$LOCAL_SHA" != "$REMOTE_SHA" ]]; then
  warn "Your local HEAD (${LOCAL_SHA:0:7}) is not $REF (${REMOTE_SHA:0:7}) — the deploy uses $REF."
fi
if [[ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]]; then
  warn "You have uncommitted changes. They are NOT part of this deploy."
fi

# Known trap, worth catching before the run rather than in a red check: the
# marketing workflow's final step smoke-tests https://www.insolvia.ai/ with
# `curl -sSf`. While the site is deliberately parked (site_enabled = false in
# infra/envs/prod), CloudFront answers 403 and that step can only fail.
if [[ "$TARGET" == "marketing" ]] &&
   grep -qE '^\s*site_enabled\s*=\s*false' "$REPO_ROOT/infra/envs/prod/main.tf" 2>/dev/null; then
  warn "The marketing site is parked offline (site_enabled = false in infra/envs/prod)."
  warn "This workflow's smoke check asserts the site is UP, so the run will go red"
  warn "even though the apply itself succeeds. Deploy 'api' instead to apply prod infra."
fi

if [[ "$AUTO_APPROVE" -ne 1 ]]; then
  printf '\n\033[1;33mDeploy %s to PRODUCTION from %s?\033[0m [y/N] ' "$DISPLAY_NAME" "$REF"
  read -r reply || die "Aborted."
  [[ "$reply" =~ ^[Yy]$ ]] || die "Aborted."
fi

latest_run_id() {
  gh run list --repo "$REPO" --workflow "$WORKFLOW" --limit 1 \
    --json databaseId --jq '.[0].databaseId // empty'
}

# Identify the run by "newest run id changed", not by timestamp: run ids
# increase monotonically, so this needs no clock agreement between this machine
# and GitHub. Comparing against a locally-taken timestamp would silently miss
# the run whenever the two clocks disagree by even a second.
PREVIOUS_RUN_ID="$(latest_run_id)"

gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF"
ok "Dispatched $DISPLAY_NAME on $REF."

# `gh workflow run` does not report the run it created, and the run takes a
# moment to show up in the list.
RUN_ID=""
for _ in $(seq 1 20); do
  candidate="$(latest_run_id)"
  if [[ -n "$candidate" && "$candidate" != "$PREVIOUS_RUN_ID" ]]; then
    RUN_ID="$candidate"
    break
  fi
  sleep 3
done

if [[ -z "$RUN_ID" ]]; then
  warn "Dispatched, but could not identify the run. Check: gh run list --workflow $WORKFLOW"
  exit 0
fi

RUN_URL="$(gh run view "$RUN_ID" --repo "$REPO" --json url --jq .url)"
log "Run: $RUN_URL"

if [[ "$WATCH" -eq 0 ]]; then
  exit 0
fi

# --exit-status makes a failed deploy a non-zero exit here, so this script is
# safe to chain with && in a longer command.
gh run watch "$RUN_ID" --repo "$REPO" --exit-status
ok "$DISPLAY_NAME completed successfully."
