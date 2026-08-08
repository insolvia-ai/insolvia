#!/usr/bin/env bash
#
# Put the staging E2E test user's credentials into GitHub Actions as
# ENVIRONMENT secrets on `insolvia-staging`, where the E2E job in
# .github/workflows/app-staging.yml can see them. One-time setup; run it after
# ./scripts/staging-aws-create-test-user.sh has created the user those values name.
#
#   E2E_TEST_USER_EMAIL='e2e@…' ./scripts/staging-github-set-secrets.sh
#   ./scripts/staging-github-set-secrets.sh --check      # list what is set, change nothing
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
# Only used to reach infra/envs/staging for the sub lookup below. Same
# derivation e2e-create-test-user.sh uses, so both work from any directory.
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Must match `environment:` on the e2e job in app-staging.yml, and the names the
# suite reads in e2e/support/env.ts. All of it is one contract; renaming any of
# them without the others makes the job see empty strings.
#
# E2E_TEST_USER_SUBJECT is read by the SEED step rather than by the suite —
# seeds/staging.json names its person by Cognito sub, because ci-trust cannot
# scope an AdminGetUser grant to the staging pool (it is applied before any pool
# exists) and the wildcard would reach prod's. It lives here because it is the
# same person as the two above, and three secrets set by one script cannot
# drift onto two different identities.
readonly ENVIRONMENT="insolvia-staging"
readonly SECRETS=(E2E_TEST_USER_EMAIL E2E_TEST_USER_PASSWORD E2E_TEST_USER_SUBJECT)

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
      printf '  E2E_TEST_USER_EMAIL     required — the staging E2E user'"'"'s address\n'
      printf '  E2E_TEST_USER_PASSWORD  optional — prompted for, without echo, when unset\n'
      printf '  E2E_TEST_USER_SUBJECT   optional — read from the pool when unset\n'
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
  ok "Both E2E secrets are set on $ENVIRONMENT."
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
  warn "The values must match a user that exists in the staging pool (./scripts/staging-aws-create-test-user.sh)."
  read -rp "Continue? [y/N] " reply
  [[ "$reply" == [yY]* ]] || die "Aborted; nothing was changed."
fi

EMAIL="${E2E_TEST_USER_EMAIL:-}"
[[ -n "$EMAIL" ]] || die "E2E_TEST_USER_EMAIL is not set. Export the staging E2E user's address."
[[ "$EMAIL" == *@*.* ]] || die "E2E_TEST_USER_EMAIL ('$EMAIL') is not an email address."

PASSWORD="${E2E_TEST_USER_PASSWORD:-}"
if [[ -z "$PASSWORD" ]]; then
  read -rsp "Password for $EMAIL (not echoed, not stored): " PASSWORD
  printf '\n'
fi
[[ -n "$PASSWORD" ]] || die "No password given."

# The sub is derivable rather than chosen, so this reads it instead of asking —
# a value a human retypes is a value that eventually gets retyped wrong, and a
# wrong sub seeds a firm for nobody while looking like it worked. Needs a
# staging AWS session; export E2E_TEST_USER_SUBJECT to skip the lookup (that is
# what CI-less environments and the --check path do).
SUBJECT="${E2E_TEST_USER_SUBJECT:-}"
if [[ -z "$SUBJECT" ]]; then
  command -v aws >/dev/null ||
    die "aws CLI not installed, and E2E_TEST_USER_SUBJECT is not set to skip the lookup."
  command -v terraform >/dev/null ||
    die "terraform not installed, and E2E_TEST_USER_SUBJECT is not set to skip the lookup."
  log "Reading $EMAIL's Cognito sub from the staging pool"
  pool_id="$(terraform -chdir="$REPO_ROOT/infra/envs/staging" output -raw auth_user_pool_id 2>/dev/null)" ||
    die "Could not read the staging pool id. Run scripts/e2e-create-test-user.sh first — it
       inits that root — or export E2E_TEST_USER_SUBJECT yourself."
  # shellcheck disable=SC2016  # the backticks are JMESPath, not a subshell
  SUBJECT="$(aws --no-cli-pager cognito-idp admin-get-user \
    --user-pool-id "$pool_id" --username "$EMAIL" \
    --query 'UserAttributes[?Name==`sub`].Value' --output text 2>/dev/null)" ||
    die "admin-get-user failed for '$EMAIL'. Does the account exist? See
       scripts/e2e-create-test-user.sh --check."
fi
[[ "$SUBJECT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] ||
  die "'$SUBJECT' is not a Cognito sub (expected a uuid)."

# `printf` (a shell builtin) into gh's stdin: the value never becomes an argv
# entry, so it cannot be read out of `ps` by another user on this machine.
set_secret() {
  local name="$1" value="$2"
  printf '%s' "$value" | gh secret set "$name" --env "$ENVIRONMENT" --repo "$REPO" ||
    die "Failed to set $name. Your token needs write access to $REPO's Actions secrets."
  log "Set $name"
}

set_secret E2E_TEST_USER_EMAIL "$EMAIL"
set_secret E2E_TEST_USER_PASSWORD "$PASSWORD"
set_secret E2E_TEST_USER_SUBJECT "$SUBJECT"

existing="$(gh secret list --env "$ENVIRONMENT" --repo "$REPO" --json name --jq '.[].name')"
report
for name in "${SECRETS[@]}"; do
  grep -qx "$name" <<<"$existing" || die "$name is still missing after being set — check $REPO's settings."
done

ok "All three E2E secrets are set on the $ENVIRONMENT environment."
log "Next: merge to main (or dispatch App · Deploy · Staging) and watch the 'e2e' job."
