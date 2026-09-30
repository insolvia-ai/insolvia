#!/usr/bin/env bash
# SessionStart hook: make the agent's Bash commands act as the RIGHT GitHub and
# AWS accounts, then say which accounts those are.
#
# Why this exists. A developer machine can hold two GitHub accounts and two AWS
# accounts (personal + Insolvia). The usual per-folder switch is a direnv
# `.envrc` above the checkout exporting `GH_TOKEN` and `AWS_PROFILE`. But direnv
# applies itself from the interactive shell's PROMPT hook, and an agent's Bash
# commands run in non-interactive shells that never draw a prompt — so without
# this, agents here silently ran `gh` as whichever account was machine-wide
# active and `aws` against the developer's PERSONAL account.
#
# Two steps:
#
# 1. Load direnv into every Bash command. Claude Code sources `$CLAUDE_ENV_FILE`
#    before each Bash command; we append ONE line that asks direnv for this
#    checkout's environment at that moment. Deliberately not the exported values:
#    `GH_TOKEN` is a secret, and writing it into a session file would put it on
#    disk and freeze it past a rotation. The line is a no-op on machines without
#    direnv or without an allowed `.envrc` — single-account machines need none.
#
# 2. Report identity into context: which GitHub login `gh` resolves to and its
#    permission on this repo, and which AWS account the CLI resolves to. The
#    agent reads this before acting; the fixes live in the `insolvia-github-auth`
#    and `insolvia-aws-auth` skills.
#
# It must never block session start, so it always exits 0.
set -uo pipefail

repo="insolvia-ai/insolvia"
aws_account="521762924626"
root="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Step 1 — direnv into agent shells.
if command -v direnv >/dev/null 2>&1; then
  q_root="$(printf '%q' "$root")"
  line="command -v direnv >/dev/null 2>&1 && eval \"\$(cd $q_root && direnv export bash 2>/dev/null)\""
  if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -qxF "$line" "$CLAUDE_ENV_FILE" 2>/dev/null; then
    printf '%s\n' "$line" >>"$CLAUDE_ENV_FILE"
  fi
  # Same environment for the checks below.
  eval "$(cd "$root" && direnv export bash 2>/dev/null)"
fi

# Step 2 — identity, both lookups in parallel (each is one network round trip).
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
if command -v gh >/dev/null 2>&1; then
  ( gh api graphql -f query="{viewer{login} repository(owner:\"${repo%/*}\",name:\"${repo#*/}\"){viewerPermission}}" \
      --jq '"\(.data.viewer.login) \(.data.repository.viewerPermission)"' >"$tmp/gh" 2>/dev/null ) &
fi
if command -v aws >/dev/null 2>&1; then
  ( aws sts get-caller-identity --query Account --output text >"$tmp/aws" 2>/dev/null ) &
fi
wait

src="gh's machine-wide active account"
[ -n "${GH_TOKEN:-}" ] && src="GH_TOKEN from this folder's .envrc"
if [ -s "$tmp/gh" ]; then
  read -r login perm <"$tmp/gh"
  case "$perm" in
    ADMIN | MAINTAIN | WRITE) echo "GitHub: gh acts as $login ($perm on $repo, via $src)." ;;
    *) echo "GitHub: gh acts as $login, which has only ${perm:-no} access to $repo (via $src) — GitHub writes will FAIL. Read the insolvia-github-auth skill before any gh/git write; never run gh auth switch." ;;
  esac
elif command -v gh >/dev/null 2>&1; then
  echo "GitHub: could not resolve a gh identity (not logged in, or offline). See the insolvia-github-auth skill before any GitHub write."
fi

if command -v aws >/dev/null 2>&1; then
  acct="$(cat "$tmp/aws" 2>/dev/null)"
  profile="${AWS_PROFILE:-default}"
  if [ "$acct" = "$aws_account" ]; then
    echo "AWS: profile '$profile' → account $acct (Insolvia)."
  elif [ -n "$acct" ]; then
    echo "AWS: profile '$profile' → account $acct, which is NOT Insolvia's ($aws_account). Do not run AWS commands until you've read the insolvia-aws-auth skill (\"The profile\")."
  else
    echo "AWS: profile '$profile' has no valid session (expired or not signed in). Only needed for AWS work — see the insolvia-aws-auth skill."
  fi
fi
exit 0
