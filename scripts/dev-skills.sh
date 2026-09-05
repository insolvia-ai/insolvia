#!/usr/bin/env bash
#
# Agent skills for THIS checkout — including git worktrees.
#
# The skills in `.agents/skills/` are INSTALLED, not vendored: `skills-lock.json`
# is the manifest, this script is what puts the files on disk, and the files
# themselves are gitignored (see the `.gitignore` section and scripts/README.md).
# They used to be committed — 131 files of somebody else's documentation,
# reviewed as if we owned it and updated by hand — which is the thing this
# replaces.
#
# WHY THIS IS ITS OWN SCRIPT: a git worktree is a fresh checkout of TRACKED
# files, and both halves of a skill install are ignored — `.agents/skills/` and
# the `.claude/skills/<name>` symlinks that point into it. So every worktree
# starts with the four `insolvia-*` skills (tracked) and none of the rest, and
# an agent working in one silently loses `design-system-catalogue` — the skill
# whose whole job is stopping a screen from hand-rolling a component the design
# system already ships. It fails quietly: nothing errors, the skill simply is
# not in the list.
#
# TWO WAYS TO GET THEM, and the cheap one is the default:
#
#   link     Symlink each skill from the PRIMARY checkout's `.agents/skills/`
#            (the one holding the shared `.git`), then create this checkout's
#            `.claude/skills/<name>` symlinks. Offline, milliseconds, and it
#            leaves `skills-lock.json` untouched — which matters, because the
#            installer rewrites that file (see below) and a worktree is exactly
#            where a spurious lock diff gets committed by accident.
#   install  `skills add` from the network, per the lock. What a fresh clone
#            needs, and the fallback when the primary checkout has nothing to
#            link.
#
# The per-skill symlinks live INSIDE a real `.agents/skills/` directory rather
# than making `.agents/skills` one big symlink to the primary: `.gitignore`
# ignores `.agents/skills/` with a trailing slash, which matches a directory and
# NOT a symlink, so the one-big-symlink version would show up as untracked in
# every worktree.
#
# NOT PINNED TO THE LOCK'S HASHES. `skills add` installs each source at its
# current HEAD and rewrites `skills-lock.json` with whatever it got; the CLI has
# no "restore exactly what the lock says" command. So an install is reproducible
# in the SET of skills it installs, not in their content, and a dirty
# `skills-lock.json` afterwards is the signal that an upstream skill moved —
# review that diff, don't discard it. Revisit if the CLI grows a real restore.
#
# Usage:
#   ./scripts/dev-skills.sh            # link from the primary checkout, else install
#   ./scripts/dev-skills.sh --link     # link only; never touches the network
#   ./scripts/dev-skills.sh --install  # force a fresh install from the lock
#   ./scripts/dev-skills.sh --check    # report status only, change nothing
#
# Exits 0 in every mode, including "skills are missing" — this is called from a
# SessionStart hook and must never block a session.
#
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LOCK="$REPO_ROOT/skills-lock.json"

MODE="auto"
case "${1:-}" in
  "")         MODE="auto" ;;
  --link)     MODE="link" ;;
  --install)  MODE="install" ;;
  --check)    MODE="check" ;;
  *) printf 'usage: %s [--link|--install|--check]\n' "$0" >&2; exit 2 ;;
esac

QUIET="${DEV_SKILLS_QUIET:-0}"     # set by the SessionStart hook; suppresses per-step chatter
log()  { [[ "$QUIET" == "1" ]] || printf '\033[1;34m[dev-skills]\033[0m %s\n' "$*"; }
ok()   { [[ "$QUIET" == "1" ]] || printf '\033[1;32m[ ok ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

# --- the primary checkout ---------------------------------------------------
#
# `--git-common-dir` is the shared `.git` every worktree points at, so its
# parent is the checkout that owns it. In the primary checkout that resolves to
# the primary itself, which is how "am I a worktree?" is decided below.
# `--path-format=absolute` (git >= 2.31) is what makes it an absolute path
# rather than a bare `.git`.
PRIMARY=""
if have git; then
  common_dir="$(git -C "$REPO_ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  [[ -n "$common_dir" ]] && PRIMARY="$(cd "$(dirname "$common_dir")" 2>/dev/null && pwd || true)"
fi
IS_WORKTREE=0
[[ -n "$PRIMARY" && "$PRIMARY" != "$REPO_ROOT" && -d "$PRIMARY/.agents/skills" ]] && IS_WORKTREE=1

# --- reading the manifest ---------------------------------------------------
#
# The lock is the manifest, not the directory listing: a skill removed from the
# lock (gluestack-ui-v5 was) can still be sitting on the primary checkout's
# disk, and linking it back would quietly reinstate something an ADR removed.
lock_names() {
  node -e '
    const lock = require(process.argv[1]);
    for (const [name, meta] of Object.entries(lock.skills ?? {})) if (meta?.source) console.log(name);
  ' "$LOCK" 2>/dev/null
}

# source -> space-separated skill names, one line per source (`skills add` takes
# one source at a time).
lock_sources() {
  node -e '
    const lock = require(process.argv[1]);
    const bySource = new Map();
    for (const [name, meta] of Object.entries(lock.skills ?? {})) {
      if (!meta?.source) continue;
      if (!bySource.has(meta.source)) bySource.set(meta.source, []);
      bySource.get(meta.source).push(name);
    }
    for (const [source, names] of bySource) console.log([source, ...names].join(" "));
  ' "$LOCK" 2>/dev/null
}

# A skill counts as present only when BOTH halves are there: the files under
# `.agents/skills/<name>` (a real directory, or a symlink into the primary
# checkout) AND the `.claude/skills/<name>` symlink that is what actually makes
# the agent see it. Checking only the first is how a worktree reports "installed"
# while loading none of them.
has_files() { [[ -e "$REPO_ROOT/.agents/skills/$1/SKILL.md" ]]; }
has_link()  { [[ -e "$REPO_ROOT/.claude/skills/$1/SKILL.md" ]]; }

# --- linking ----------------------------------------------------------------
link_from_primary() {
  local name linked=0 relinked=0

  mkdir -p "$REPO_ROOT/.agents/skills" "$REPO_ROOT/.claude/skills"

  while read -r name; do
    [[ -n "$name" ]] || continue

    local agents="$REPO_ROOT/.agents/skills/$name"
    # A dangling symlink is left over from a primary checkout that has since
    # been re-installed or moved; -e follows it, so this catches it.
    [[ -L "$agents" && ! -e "$agents" ]] && rm -f "$agents"

    if [[ ! -e "$agents" ]]; then
      if [[ "$IS_WORKTREE" -eq 1 && -e "$PRIMARY/.agents/skills/$name/SKILL.md" ]]; then
        ln -s "$PRIMARY/.agents/skills/$name" "$agents" && linked=$((linked + 1))
      else
        continue     # nothing to link from; the installer has to fetch it
      fi
    fi

    # The `.claude/skills/` half is relative on purpose — it is the layout
    # `skills add --agent universal --agent claude-code` produces, and it keeps
    # working if the checkout is moved.
    local claude="$REPO_ROOT/.claude/skills/$name"
    [[ -L "$claude" && ! -e "$claude" ]] && rm -f "$claude"
    if [[ ! -e "$claude" ]]; then
      ln -s "../../.agents/skills/$name" "$claude" && relinked=$((relinked + 1))
    fi
  done <<< "$(lock_names)"

  [[ "$linked" -gt 0 || "$relinked" -gt 0 ]] && \
    log "linked $linked skill(s) from $PRIMARY, wired $relinked into .claude/skills/"
  return 0
}

# --- installing -------------------------------------------------------------
#
# The installer is `skills` (github.com/vercel-labs/skills), run through npx so
# there is nothing global to pin; Node is its only requirement.
#
# `--agent universal --agent claude-code` is what reproduces the layout the rest
# of the repo assumes: the real directory at `.agents/skills/<name>/` (the
# `universal` path, shared by Codex, Cursor, Copilot and the rest) with
# `.claude/skills/<name>` as a symlink into it. Installing to claude-code ALONE
# copies the files into `.claude/skills/` instead and no `.agents/` tree
# appears — a layout every path in CLAUDE.md and the ADRs would then miss.
#
# A token is passed when `gh` can supply one: GitHub's API rate-limits anonymous
# requests hard enough to fail this step on a shared IP, and
# insolvia-ai/design-system needs it if the repo is ever private.
install_from_lock() {
  local token=""
  have gh && token="$(gh auth token 2>/dev/null || true)"

  local sources
  sources="$(lock_sources)" || { warn "could not read skills-lock.json — skipping agent skills"; return 0; }
  [[ -n "$sources" ]] || { log "skills-lock.json lists none"; return 0; }

  local source names
  while read -r source names; do
    [[ -n "$source" ]] || continue
    local args=() name count=0
    for name in $names; do args+=(--skill "$name"); count=$((count + 1)); done
    log "skills: $source ($count)"
    # `cd "$REPO_ROOT"` in a subshell is load-bearing: `skills add` resolves
    # `.agents/skills/`, `.claude/skills/` and `skills-lock.json` against the
    # PROCESS's working directory, and this script is reached from a
    # per-package one (`cd services/api && ./scripts/dev-setup.sh` → the shared
    # base → here). Without the cd it installs a second skills tree under
    # whatever directory the developer happened to be in — which `ruff check .`
    # then lints as if it were service code, and which .gitignore does NOT
    # cover: `.agents/skills/` contains a slash, so git anchors it to the repo
    # root and a nested copy shows up untracked alongside a stray
    # skills-lock.json. A subshell, not a bare cd, so the rest of the script
    # keeps the caller's cwd.
    #
    # `</dev/null` is load-bearing too: npx inherits this loop's stdin, and
    # without it the first `add` swallows the remaining lines of "$sources" —
    # one source gets installed and the rest vanish silently.
    if ! ( cd "$REPO_ROOT" && GH_TOKEN="${token:-${GH_TOKEN:-}}" npx --yes skills@1 add "$source" \
        "${args[@]}" --agent universal --agent claude-code -y </dev/null >/dev/null 2>&1 ); then
      warn "could not install skills from $source — re-run, or, FROM THE REPO ROOT: npx skills add $source --agent universal --agent claude-code"
    fi
  done <<< "$sources"
}

# --- status -----------------------------------------------------------------
#
# Prints "<total> <ready> <missing-names…>" for the caller to render.
status() {
  local name total=0 ready=0 missing=()
  while read -r name; do
    [[ -n "$name" ]] || continue
    total=$((total + 1))
    if has_files "$name" && has_link "$name"; then ready=$((ready + 1)); else missing+=("$name"); fi
  done <<< "$(lock_names)"
  printf '%s %s %s\n' "$total" "$ready" "${missing[*]:-}"
}

# ---------------------------------------------------------------------------
[[ -r "$LOCK" ]] || { warn "no skills-lock.json at $LOCK — nothing to do"; exit 0; }
have node || { warn "node missing — cannot read skills-lock.json; skipping agent skills"; exit 0; }

case "$MODE" in
  link)    link_from_primary ;;
  install) install_from_lock; link_from_primary ;;   # link_from_primary also repairs .claude/ links
  auto)
    link_from_primary
    read -r total ready _ <<< "$(status)"
    if [[ "$ready" -lt "$total" ]]; then
      install_from_lock
      link_from_primary
    fi
    ;;
esac

read -r total ready missing <<< "$(status)"
if [[ "$ready" -eq "$total" ]]; then
  ok "agent skills: $ready/$total present (.agents/skills/ + .claude/skills/)."
else
  if [[ "$MODE" == "check" || "$MODE" == "link" ]]; then
    warn "agent skills: $ready/$total present — missing: ${missing:-?}"
    warn "run: ./scripts/dev-skills.sh          (links from the primary checkout, or installs)"
  else
    warn "agent skills: $ready/$total present after install — missing: ${missing:-?}"
  fi
fi
exit 0
