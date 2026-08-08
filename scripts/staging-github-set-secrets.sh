#!/usr/bin/env bash
#
# Put the staging E2E password into GitHub Actions as an ENVIRONMENT secret on
# `insolvia-staging`, where the E2E job in .github/workflows/app-staging.yml can
# see it. One-time setup, and re-run to rotate.
#
#   ./scripts/staging-github-set-secrets.sh              # prompts, without echo
#   ./scripts/staging-github-set-secrets.sh --check      # list what is set, change nothing
#
# ## ONE secret, where there used to be three
#
# The addresses live in seeds/staging.json — they end in `.test`, a reserved TLD
# that can never be a real mailbox, so they are safe to commit and belong next
# to the firms they are seeded into. The subjects are resolved from the pool at
# seed time rather than pinned, because a pinned sub rots the moment the pool is
# replaced. What is left is a password, shared by every seeded account: they are
# throwaway identities in an environment with no customer data, and one value
# that rotates cleanly beats one per person that rotates by hand.
#
# ADDING A TEST USER DOES NOT COME BACK HERE. It is an edit to
# seeds/staging.json; the next staging deploy creates the account with this same
# password. That is the whole reason the other two secrets went away.
#
# ## Why the ENVIRONMENT and not the repository
#
# Same scoping as `AWS_ROLE_ARN` today. A repository secret is visible to every
# workflow in the repo, including ones that have nothing to do with staging; an
# environment secret is visible only to a job that declares
# `environment: insolvia-staging`, which is exactly one job. The flip side is
# the trap infra/CLAUDE.md warns about: a job that forgets the `environment:`
# key — or borrows another environment's name — sees the secret as an empty
# string, silently. The E2E suite fails fast on an empty credential rather than
# trying to sign in as nobody, but the fix is always to check `environment:`
# first.
#
# ## Credentials
#
# `gh`, not AWS: this touches GitHub only. Values are read from the environment
# (or a no-echo prompt) and never from a file in this repo — the repo is public.
# Both are piped to `gh secret set` on stdin rather than passed as `--body`, so
# neither appears in this process's argv.
#
# ## Idempotence
#
# `gh secret set` is an upsert, so re-running is how you ROTATE a credential:
# the new value replaces the old one under the same name. That is deliberate,
# and the reason the script prints what it is about to overwrite and asks first
# when a secret already exists.
#
set -euo pipefail

REPO="${INSOLVIA_REPO:-insolvia-ai/insolvia}"

# Must match `environment:` on the e2e job in app-staging.yml, the name the
# suite reads in e2e/support/env.ts, and the one the seed step passes through to
# expand ${E2E_TEST_USER_PASSWORD} in seeds/staging.json. All four are one
# contract; renaming any without the others makes the job see an empty string.
readonly ENVIRONMENT="insolvia-staging"
readonly SECRETS=(E2E_TEST_USER_PASSWORD)

log()  { printf '\033[1;34m[e2e-secrets]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

CHECK_ONLY=0
ASSUME_YES=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    --yes|-y) ASSUME_YES=1 ;;
    --help|-h)
      printf 'Usage: %s [--check] [--yes]\n\n' "$0"
      printf '  E2E_TEST_USER_PASSWORD  optional — prompted for, without echo, when unset\n'
      printf '  INSOLVIA_REPO           optional — defaults to %s\n' "$REPO"
      exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
  shift
done

command -v gh >/dev/null || die "gh CLI not installed."
gh auth status >/dev/null 2>&1 || die "Not logged in to GitHub. Run 'gh auth login'."

# Catch a typo'd or not-yet-created environment here, rather than as a
# `gh secret set` failure whose message does not say which of the two is wrong.
gh api "repos/$REPO/environments/$ENVIRONMENT" --silent >/dev/null 2>&1 ||
  die "No '$ENVIRONMENT' environment on $REPO. It is created alongside the deploy workflows —
       see docs/runbooks/aws-bootstrap.md."

existing="$(gh secret list --env "$ENVIRONMENT" --repo "$REPO" --json name --jq '.[].name')"

report() {
  log "Environment secrets on $ENVIRONMENT ($REPO):"
  for name in "${SECRETS[@]}"; do
    if grep -qx "$name" <<<"$existing"; then
      printf '  \033[1;32m✓\033[0m %s\n' "$name"
    else
      printf '  \033[1;31m✗\033[0m %s  (not set)\n' "$name"
    fi
  done
}

if [[ "$CHECK_ONLY" -eq 1 ]]; then
  report
  for name in "${SECRETS[@]}"; do
    grep -qx "$name" <<<"$existing" || exit 1
  done
  ok "The E2E password is set on $ENVIRONMENT."
  exit 0
fi

report

# An existing value is about to be replaced. Say so before doing it — a
# re-run is a rotation, and an accidental rotation breaks the E2E job on the
# next deploy with a credential error nobody expected.
already=()
for name in "${SECRETS[@]}"; do
  grep -qx "$name" <<<"$existing" && already+=("$name")
done
if [[ ${#already[@]} -ne 0 && "$ASSUME_YES" -ne 1 ]]; then
  warn "About to OVERWRITE: ${already[*]}"
  warn "The next staging deploy resets every seeded account to the new value, so"
  warn "a rotation here converges rather than locking the suite out."
  read -rp "Continue? [y/N] " reply
  [[ "$reply" == [yY]* ]] || die "Aborted; nothing was changed."
fi

PASSWORD="${E2E_TEST_USER_PASSWORD:-}"
if [[ -z "$PASSWORD" ]]; then
  read -rsp "Password for the seeded staging accounts (not echoed, not stored): " PASSWORD
  printf '\n'
fi
[[ -n "$PASSWORD" ]] || die "No password given."

# Checked against the pool's own policy here (12+, upper, lower, digit —
# infra/modules/auth/main.tf) so a weak one fails in one line, rather than as an
# InvalidPasswordException inside a seed step on the next deploy.
{
  [[ ${#PASSWORD} -ge 12 ]] &&
    [[ "$PASSWORD" == *[[:lower:]]* ]] &&
    [[ "$PASSWORD" == *[[:upper:]]* ]] &&
    [[ "$PASSWORD" == *[[:digit:]]* ]]
} || die "Password does not meet the pool policy: 12+ characters with at least one
       lower-case letter, one upper-case letter and one digit."

# `printf` (a shell builtin) into gh's stdin: the value never becomes an argv
# entry, so it cannot be read out of `ps` by another user on this machine.
set_secret() {
  local name="$1" value="$2"
  printf '%s' "$value" | gh secret set "$name" --env "$ENVIRONMENT" --repo "$REPO" ||
    die "Failed to set $name. Your token needs write access to $REPO's Actions secrets."
  log "Set $name"
}

set_secret E2E_TEST_USER_PASSWORD "$PASSWORD"

existing="$(gh secret list --env "$ENVIRONMENT" --repo "$REPO" --json name --jq '.[].name')"
report
for name in "${SECRETS[@]}"; do
  grep -qx "$name" <<<"$existing" || die "$name is still missing after being set — check $REPO's settings."
done

ok "The E2E password is set on the $ENVIRONMENT environment."
log "Next: merge to main (or dispatch App · Deploy · Staging) and watch the 'e2e' job."
