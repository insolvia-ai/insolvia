#!/usr/bin/env bash
#
# Shared developer / CI toolchain bootstrap for the Insolvia monorepo.
#
# Installs the cross-cutting tools every package needs (Terraform, tflint, AWS
# CLI, jq, Node.js, FVM + the pinned Flutter, Melos, Python 3.12) with Homebrew
# on BOTH macOS and Linux. Package-specific setup lives in per-package scripts,
# e.g. services/api/scripts/dev-setup.sh (venv + pip).
#
# Homebrew details:
#   - macOS (developer machines): brew runs as the normal user.
#   - Linux (cloud sandbox / CI): Homebrew refuses to run as root, so it is
#     installed into the default prefix /home/linuxbrew/.linuxbrew owned by the
#     non-root `ubuntu` user, and every `brew` call is run as that user via
#     sudo. The prefix bin is added to PATH (this run + /etc/profile.d) so root
#     and CI agents can execute the installed tools.
#   - terraform, tflint, and fvm are NOT in homebrew-core; they come from taps
#     (hashicorp/tap, terraform-linters/tap, leoafarias/fvm) on every platform.
#
# Toolchain pins (each is read from the file that owns it — update there, not
# here):
#   - Flutter: .fvmrc (currently the `stable` channel) via FVM.
#   - Melos:   root pubspec.yaml dev_dependencies (`melos: ^6.3.0`), activated
#     globally so `melos bootstrap` / `melos run ...` work from any shell.
#   - Node:    >= 24, matching `engines.node` in apps/insolvia_marketing and
#     packages/insolvia_design_system_react.
#   - Python:  3.12, matching services/api (pyproject `requires-python` and the
#     public.ecr.aws/lambda/python:3.12 base image).
#
# IDEMPOTENT: every tool is checked with `command -v` (or an equivalent probe)
# before install, so re-running is a no-op once the toolchain is present.
#
# Usage:
#   ./scripts/dev-setup.sh            # install everything missing
#   ./scripts/dev-setup.sh --check    # report status only, install nothing
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CHECK_ONLY=0
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=1

NODE_MAJOR_MIN=24           # engines.node ">=24" in both npm packages

log()  { printf '\033[1;34m[dev-setup]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
skip() { printf '\033[1;36m[skip]\033[0m %s already present: %s\n' "$1" "$2"; }

# Detect platform from the kernel name. `uname -s` is the kernel identifier: it
# is reliably "Darwin" on every macOS release (it does NOT track the macOS
# marketing version) and "Linux" on Linux. Match with a glob and treat anything
# else as unsupported rather than silently assuming Linux.
OS="$(uname -s)"
case "$OS" in
  Darwin*) PLATFORM="macos" ;;
  Linux*)  PLATFORM="linux" ;;
  *) printf 'Unsupported OS: %s (this repo supports macOS and Linux only).\n' "$OS" >&2; exit 1 ;;
esac
have() { command -v "$1" >/dev/null 2>&1; }
if [[ "$(id -u)" -eq 0 ]]; then SUDO=""; else SUDO="sudo"; fi

# --- Linux Homebrew (installed & run as the non-root ubuntu user) ----------
LINUXBREW_PREFIX="/home/linuxbrew/.linuxbrew"
LINUX_BREW_USER="ubuntu"
BREW_ENV=(HOMEBREW_NO_ANALYTICS=1 HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_ENV_HINTS=1)

# Run a brew command the right way for the platform.
brew_run() {
  if [[ "$PLATFORM" == "macos" ]]; then
    env "${BREW_ENV[@]}" brew "$@"
  else
    $SUDO -u "$LINUX_BREW_USER" env "${BREW_ENV[@]}" "$LINUXBREW_PREFIX/bin/brew" "$@"
  fi
}

apt_install() { $SUDO apt-get update -y >/dev/null && $SUDO apt-get install -y "$@"; }

ensure_brew() {
  if [[ "$PLATFORM" == "macos" ]]; then
    if have brew; then return 0; fi
    if [[ "$CHECK_ONLY" -eq 1 ]]; then warn "Homebrew is MISSING (required on macOS)"; return 1; fi
    log "installing Homebrew ..."
    NONINTERACTIVE=1 /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    return 0
  fi

  # ---- Linux ----
  if [[ ! -x "$LINUXBREW_PREFIX/bin/brew" ]]; then
    if [[ "$CHECK_ONLY" -eq 1 ]]; then warn "Homebrew (Linux) is MISSING (would install as user '$LINUX_BREW_USER')"; return 1; fi
    if ! id "$LINUX_BREW_USER" >/dev/null 2>&1; then
      warn "non-root user '$LINUX_BREW_USER' not found; Homebrew cannot run as root."
      return 1
    fi
    log "installing Homebrew prerequisites ..."
    apt_install build-essential procps curl file git unzip >/dev/null 2>&1 || \
      warn "could not apt-get prerequisites (continuing; they may already be present)"
    log "installing Homebrew into $LINUXBREW_PREFIX (owned by $LINUX_BREW_USER) ..."
    $SUDO mkdir -p "$LINUXBREW_PREFIX"
    $SUDO chown -R "$LINUX_BREW_USER:$LINUX_BREW_USER" "$(dirname "$LINUXBREW_PREFIX")"
    $SUDO -u "$LINUX_BREW_USER" git clone --depth=1 https://github.com/Homebrew/brew "$LINUXBREW_PREFIX/Homebrew"
    $SUDO -u "$LINUX_BREW_USER" mkdir -p "$LINUXBREW_PREFIX/bin"
    $SUDO -u "$LINUX_BREW_USER" ln -sf "$LINUXBREW_PREFIX/Homebrew/bin/brew" "$LINUXBREW_PREFIX/bin/brew"
  fi

  # Expose brew + its tools on PATH for this run and for future login shells.
  eval "$("$LINUXBREW_PREFIX/bin/brew" shellenv)"
  local profile="/etc/profile.d/homebrew.sh"
  if [[ "$CHECK_ONLY" -ne 1 && ! -f "$profile" ]]; then
    echo "eval \"\$($LINUXBREW_PREFIX/bin/brew shellenv)\"" | $SUDO tee "$profile" >/dev/null || true
  fi
  return 0
}

# brew_ensure <cli-name> <formula>  (formula may be tap-qualified)
brew_ensure() {
  local cli="$1" formula="$2"
  if have "$cli"; then skip "$cli" "$(command -v "$cli")"; return 0; fi
  if [[ "$CHECK_ONLY" -eq 1 ]]; then warn "$cli is MISSING (would: brew install $formula)"; return 0; fi
  log "installing $formula ..."
  brew_run install "$formula"
}

# --- Flutter via FVM --------------------------------------------------------
# .fvmrc is the single source of truth for the Flutter pin; `fvm install` run
# at the repo root reads it. The cache probe below only decides whether that
# (idempotent but slow) call is needed at all — and lets --check answer without
# touching the network.
fvm_pin() {
  sed -n 's/.*"flutter"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$REPO_ROOT/.fvmrc" | head -n1
}

fvm_flutter_cached() {
  local pin="$1"
  [[ -n "$pin" ]] || return 1
  local cache="${FVM_CACHE_PATH:-$HOME/fvm}"
  [[ -d "$cache/versions/$pin" ]]
}

ensure_flutter() {
  local pin
  pin="$(fvm_pin)"
  if [[ -z "$pin" ]]; then
    warn "could not parse the Flutter pin from .fvmrc — skipping 'fvm install'."
    return 0
  fi
  if ! have fvm; then
    # brew_ensure above already warned in --check mode; nothing more to say.
    [[ "$CHECK_ONLY" -eq 1 ]] && warn "Flutter '$pin' cannot be checked without fvm"
    return 0
  fi
  if fvm_flutter_cached "$pin"; then
    skip "Flutter ($pin)" "${FVM_CACHE_PATH:-$HOME/fvm}/versions/$pin"
    return 0
  fi
  if [[ "$CHECK_ONLY" -eq 1 ]]; then
    warn "Flutter '$pin' is MISSING from the FVM cache (would: fvm install, per .fvmrc)"
    return 0
  fi
  log "installing Flutter '$pin' via fvm (reads .fvmrc) ..."
  (cd "$REPO_ROOT" && fvm install)
}

# --- Melos (Dart pub global) ------------------------------------------------
# Pinned as a dev dependency in the root pubspec.yaml (melos: ^6.3.0); the
# global activation is what puts `melos` on PATH for day-to-day use. Requires
# the FVM-managed Flutter (for `dart`) to exist first.
ensure_melos() {
  if have melos; then skip melos "$(command -v melos)"; return 0; fi
  if [[ "$CHECK_ONLY" -eq 1 ]]; then
    warn "melos is MISSING (would: fvm dart pub global activate melos)"
    return 0
  fi
  if ! have fvm || ! fvm_flutter_cached "$(fvm_pin)"; then
    warn "cannot activate melos: the FVM-managed Flutter is not installed yet."
    return 1
  fi
  log "activating melos (dart pub global) ..."
  (cd "$REPO_ROOT" && fvm dart pub global activate melos)
  # pub puts global executables here; without it on PATH `melos` stays invisible.
  local pub_bin="$HOME/.pub-cache/bin"
  if ! have melos; then
    warn "melos activated but not on PATH — add: export PATH=\"\$PATH:$pub_bin\""
  fi
}

# ---------------------------------------------------------------------------
log "OS detected: $OS  (check-only: $CHECK_ONLY)"

if ! ensure_brew; then
  warn "Homebrew is not available; cannot continue. See messages above."
  exit 1
fi

brew_ensure terraform hashicorp/tap/terraform
brew_ensure tflint    terraform-linters/tap/tflint
brew_ensure aws       awscli
brew_ensure jq        jq
brew_ensure node      node
brew_ensure fvm       leoafarias/fvm/fvm
ensure_flutter
ensure_melos
brew_ensure python3.12 python@3.12

# Node version floor (the OS may already ship a newer/older node than brew's).
if have node; then
  major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)"
  [[ "$major" -ge "$NODE_MAJOR_MIN" ]] || warn "node $(node --version) is below required v${NODE_MAJOR_MIN} (engines.node in the npm packages)"
fi

# Docker is a daemon/GUI concern — check only, never auto-install. services/api
# local dev (docker compose + dynamodb-local) and its Lambda image build need it.
if have docker; then skip docker "$(command -v docker)"; else
  if [[ "$PLATFORM" == "macos" ]]; then
    warn "docker not found — install Docker Desktop: https://www.docker.com/products/docker-desktop/"
  else
    warn "docker not found — see https://docs.docker.com/engine/install/ubuntu/"
  fi
fi

log "shared toolchain ready. For complete per-package setup run its script, e.g.:"
log "    ./services/api/scripts/dev-setup.sh           # venv + pip"
log "    ./apps/insolvia_marketing/scripts/dev-setup.sh  # packages auth + npm ci"
[[ "$PLATFORM" == "linux" && "$CHECK_ONLY" -ne 1 ]] && log "Tools are on PATH via /etc/profile.d/homebrew.sh (new shells) or: eval \"\$($LINUXBREW_PREFIX/bin/brew shellenv)\""
ok "done."
