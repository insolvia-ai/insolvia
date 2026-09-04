#!/usr/bin/env bash
#
# Seed the ECR image(s) an environment's Image-package Lambdas need to exist
# before Terraform can create them — the one-time bootstrap for a fresh
# environment.
#
# ## The deadlock this breaks
#
# An Image-package Lambda cannot be created until an image exists in its ECR
# repository. But the deploy workflows (`<service>-<env>.yml`) push the image
# *after* `terraform apply`, so the very first apply in a new environment fails
# with:
#
#   InvalidParameterValueException: Source image
#     <acct>.dkr.ecr.<region>.amazonaws.com/insolvia-shared-<component>:<env>
#     does not exist. Provide a valid source image.
#
# The same note lives at the top of infra/modules/{api_service,admin_service,
# job_pipeline,mailer,marketing_site}/main.tf and in each workflow header. This
# script is that manual step, done once, for whatever services you name.
#
# It is safe to run when the ECR *repositories* already exist (e.g. a failed
# apply created them before it reached the Lambdas): it only pushes images.
#
# ## What it does NOT do
#
# It does not apply Terraform and it does not create the ECR repositories. The
# repositories are shared across environments and live in infra/envs/shared
# (one per service, `shared` in the environment slot — insolvia-shared-api and
# friends); if one is genuinely absent, apply that root first:
#
#   terraform -chdir=infra/envs/shared apply
#
# The images it pushes are placeholders. The deploy workflow immediately builds
# its own from the merged commit and rolls the Lambda onto it with
# `update-function-code`, so all that matters here is that a valid image of the
# right platform exists.
#
# ## Usage
#
#   scripts/bootstrap-ecr-images.sh <env> [service ...] [--dispatch] [--yes]
#
#   <env>        staging | prod   (matches infra/envs/<env> and the ECR suffix)
#   service ...  any of: api admin jobs mailer marketing   (default: all five;
#                `jobs` is the pipeline worker image — ADR 0018)
#   --dispatch   after pushing, re-run the matching deploy workflows
#                (<service>-<env>.yml; jobs rides api-<env>.yml)
#   --yes        skip the confirmation prompt
#
# Examples:
#   scripts/bootstrap-ecr-images.sh staging mailer marketing --dispatch
#   scripts/bootstrap-ecr-images.sh staging            # all four, no dispatch
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ACCOUNT=521762924626
REGION=us-east-1
REGISTRY="$ACCOUNT.dkr.ecr.$REGION.amazonaws.com"

# Lambda defaults to x86_64 and no module overrides `architectures`, so the
# image MUST be amd64. Docker on Apple Silicon builds arm64 by default, and the
# resulting CreateFunction failure reads like a corrupt image, not an arch
# mismatch — hence forcing the platform on every build below.
PLATFORM=linux/amd64

log()  { printf '\033[1;34m[bootstrap]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[error]\033[0m %s\n' "$*" >&2; exit 1; }

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not installed."
}

usage() {
  sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; /^set -euo/d'
  exit "${1:-0}"
}

# ── arguments ──────────────────────────────────────────────────────
ENV=""
DISPATCH=false
ASSUME_YES=false
SERVICES=()

# The CLI name for a service is not always its component name: the admin
# SERVICE is `admin` on the command line and in services/admin, but the surface
# it serves is admin-api.insolvia.ai — so its repository is
# insolvia-shared-admin-api. `admin` alone is the staff PORTAL, which has no
# container at all. See the insolvia-aws-naming skill.
#
# A `case`, not an associative array: macOS ships bash 3.2, where `declare -A`
# is not an error but a silent indexed-array declaration — `[admin]=admin-api`
# then evaluates `admin` as arithmetic and dies under `set -u` with the
# baffling "admin: unbound variable". Every script here has to run on system
# bash, so nothing may use a bash 4 construct.
ecr_component() {
  case "$1" in
    api)       printf 'api' ;;
    admin)     printf 'admin-api' ;;
    jobs)      printf 'jobs' ;;
    mailer)    printf 'mailer' ;;
    marketing) printf 'marketing' ;;
    *)         die "no ECR component mapping for service '$1'" ;;
  esac
}

# Which deploy workflow rolls a service forward. Almost always
# <service>-<env>.yml; `jobs` is the exception — the pipeline worker image is
# built from services/api and deployed by the api workflow (ADR 0018), so
# there is no jobs-<env>.yml to dispatch.
deploy_workflow() {
  case "$1" in
    jobs) printf 'api-%s.yml' "$ENV" ;;
    *)    printf '%s-%s.yml' "$1" "$ENV" ;;
  esac
}

for arg in "$@"; do
  case "$arg" in
    -h|--help)  usage 0 ;;
    --dispatch) DISPATCH=true ;;
    --yes|-y)   ASSUME_YES=true ;;
    api|admin|jobs|mailer|marketing) SERVICES+=("$arg") ;;
    staging|prod)
      [[ -z "$ENV" ]] || die "environment given twice ('$ENV' and '$arg')"
      ENV="$arg" ;;
    *) die "unrecognized argument: $arg (see --help)" ;;
  esac
done

[[ -n "$ENV" ]] || { warn "no environment given"; usage 1; }
[[ ${#SERVICES[@]} -gt 0 ]] || SERVICES=(api admin jobs mailer marketing)

require_command aws
require_command docker
require_command gh
require_command npm

# ── credentials ────────────────────────────────────────────────────
# The `aws login` session format is not readable by Docker or the Terraform
# SDK, so resolve it into standard env-var credentials (same step
# scripts/dev-aws-common.sh does). Crucially, check the session is ALIVE first:
# `export-credentials` on a dead session "succeeds" but hands back the expired
# credentials, and the failure downstream is opaque ("refreshed, but the
# refreshed credentials are still expired"). Catch it here with a clear fix.
log "Checking AWS session"
if ! aws sts get-caller-identity >/dev/null 2>&1; then
  # A stale AWS_ACCESS_KEY_ID exported into the shell — e.g. by an earlier
  # `eval "$(aws configure export-credentials --format env)"` whose session has
  # since expired — shadows the profile, because env-var credentials win in the
  # provider chain. A fresh `aws login` refreshes the profile but not those
  # vars, so the check keeps failing even after the user did the right thing.
  # Clear them (in THIS process only) and let the profile answer.
  if [[ -n "${AWS_ACCESS_KEY_ID:-}" ]]; then
    warn "Ignoring stale AWS_* environment credentials; using the profile instead."
    warn "To clear them in your own shell too: unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN AWS_CREDENTIAL_EXPIRATION"
    unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN AWS_CREDENTIAL_EXPIRATION
  fi
  aws sts get-caller-identity >/dev/null 2>&1 || die \
    "AWS session is expired or absent. Run 'aws login' and try again."
fi
eval "$(aws configure export-credentials --format env)"
CALLER="$(aws sts get-caller-identity --query Arn --output text)"
ok "Authenticated as $CALLER"

log "Checking Docker"
docker info >/dev/null 2>&1 || die "Docker is not running. Start Docker Desktop and try again."

# ── confirm ────────────────────────────────────────────────────────
log "Environment : $ENV"
log "Services    : ${SERVICES[*]}"
log "Dispatch    : $DISPATCH"
if ! $ASSUME_YES; then
  read -r -p "Build and push placeholder image(s) to the above ECR repos? [y/N] " reply
  [[ "$reply" == [yY] ]] || die "aborted"
fi

log "Logging Docker in to ECR"
aws ecr get-login-password --region "$REGION" |
  docker login --username AWS --password-stdin "$REGISTRY" >/dev/null
ok "ECR login ok"

ecr_repo_exists() {
  aws ecr describe-repositories --repository-names "$1" >/dev/null 2>&1
}

# ── per-service build + push ───────────────────────────────────────
build_api() {
  # Pure Python; --target lambda selects the public.ecr.aws/lambda stage (the
  # other stage is the local development server). Context is the REPO ROOT:
  # the image installs the shared packages/insolvia_core by relative path
  # (see services/api/Dockerfile's header).
  docker build --platform "$PLATFORM" --target lambda \
    -f "$REPO_ROOT/services/api/Dockerfile" \
    -t "$1:$ENV" "$REPO_ROOT"
}

build_admin() {
  # Same repo-root-context rule as build_api — the image installs the shared
  # packages/insolvia_core by relative path.
  docker build --platform "$PLATFORM" --target lambda \
    -f "$REPO_ROOT/services/admin/Dockerfile" \
    -t "$1:$ENV" "$REPO_ROOT"
}

build_jobs() {
  # The pipeline worker (ADR 0018): services/api's Dockerfile, `worker`
  # target — same source tree as the api image, its own image and repo so
  # worker-only dependencies never land in the request path's image.
  docker build --platform "$PLATFORM" --target worker \
    -f "$REPO_ROOT/services/api/Dockerfile" \
    -t "$1:$ENV" "$REPO_ROOT"
}

build_mailer() {
  docker build --platform "$PLATFORM" --target lambda \
    -t "$1:$ENV" "$REPO_ROOT/services/mailer"
}

build_marketing() {
  # The Dockerfile COPYs a pre-built ./build, so the app is built first. `npm
  # ci` needs a GitHub Packages token — the lockfile pins
  # @insolvia-ai/design-system to npm.pkg.github.com, which requires auth even
  # for a public package. A 403 here means the gh token lacks the scope:
  #   gh auth refresh -s read:packages
  local dir="$REPO_ROOT/apps/insolvia_marketing"
  log "Building the marketing app (npm ci + build)"
  NODE_AUTH_TOKEN="$(gh auth token)" npm ci --prefix "$dir" ||
    die "npm ci failed. If it was a 403, run: gh auth refresh -s read:packages"
  npm run build --prefix "$dir"
  docker build --platform "$PLATFORM" -t "$1:$ENV" "$dir"
}

for svc in "${SERVICES[@]}"; do
  # One repository per SERVICE, shared by every environment
  # (infra/envs/shared). The image is seeded under this environment's moving
  # marker tag, which is what the Lambda's Terraform seed points at
  # (var.image_tag) — not `:latest`, which under a shared repository would mean
  # "whatever any environment pushed last".
  repo="insolvia-shared-$(ecr_component "$svc")"
  image="$REGISTRY/$repo"
  log "── $svc → $repo ─────────────────────────────"

  if ! ecr_repo_exists "$repo"; then
    die "ECR repository '$repo' does not exist yet. Create it first with:
      terraform -chdir=infra/envs/shared apply
    (the repositories are shared across environments — see
    infra/envs/shared/main.tf), then re-run this script."
  fi

  "build_$svc" "$image"
  docker push "$image:$ENV"
  ok "pushed $image:$ENV"
done

# ── optionally re-run the deploys ──────────────────────────────────
if $DISPATCH; then
  log "Re-dispatching deploy workflows on main"
  for svc in "${SERVICES[@]}"; do
    wf="$(deploy_workflow "$svc")"
    if gh workflow run "$wf" --ref main >/dev/null 2>&1; then
      ok "dispatched $wf"
    else
      warn "could not dispatch $wf (is it workflow_dispatch-enabled?)"
    fi
  done
  log "Watch them:  gh run list --limit 6"
else
  log "Images are in place. Re-run the deploys from the GitHub UI, or re-run"
  log "this script with --dispatch. They share one concurrency group per env,"
  log "so they queue rather than race."
fi

ok "Bootstrap complete."
