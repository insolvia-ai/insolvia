#!/usr/bin/env bash
# Tests for guard-main.sh: feeds it PreToolUse JSON and checks the exit code.
#
#   .claude/hooks/guard-main.test.sh
#
# Builds a throwaway repo on `main` with a worktree on a feature branch, so the
# cases cover the bug the hook once had: reading the branch from the hook's
# own cwd (the primary checkout, on main) instead of the command's.
set -euo pipefail

hook="$(cd "$(dirname "$0")" && pwd)/guard-main.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

git init -q -b main "$tmp/repo"
git -C "$tmp/repo" -c user.name=t -c user.email=t@example.com \
  commit -q --allow-empty -m init
git -C "$tmp/repo" worktree add -q -b claude/feature "$tmp/repo/.claude/worktrees/wt"
main="$tmp/repo"
wt="$tmp/repo/.claude/worktrees/wt"
mkdir -p "$tmp/not-a-repo"

fail=0
check() { # check <expected-exit> <hook's own cwd> <payload cwd> <command> <name>
  local want="$1" run_in="$2" cwd="$3" cmd="$4" name="$5" got=0
  jq -n --arg cwd "$cwd" --arg cmd "$cmd" \
    '{cwd: $cwd, tool_name: "Bash", tool_input: {command: $cmd}}' \
    | (cd "$run_in" && "$hook") >/dev/null 2>&1 || got=$?
  if [ "$got" = "$want" ]; then
    echo "ok   $name"
  else
    echo "FAIL $name (want $want, got $got)"; fail=1
  fi
}

check 0 "$wt"   "$wt"   'git commit -m x'                    'worktree on a feature branch: commit allowed'
check 0 "$main" "$wt"   'git commit -m x'                    'hook started in main checkout, command in worktree: allowed'
check 0 "$main" "$wt"   'git push -u origin HEAD'            'push from worktree: allowed'
check 2 "$main" "$main" 'git commit -m x'                    'checkout on main: commit blocked'
check 2 "$wt"   "$main" 'git push'                           'checkout on main: push blocked'
check 2 "$main" "$main" 'git status && git commit -am x'     'commit later in a chain on main: blocked'
check 2 "$main" "$main" 'GIT_AUTHOR_NAME=x git commit -m x'  'env-prefixed commit on main: blocked'
check 2 "$main" "$main" 'git -c core.hooksPath=/dev/null commit -m x' 'commit with -c option on main: blocked'
check 2 "$wt"   "$wt"   "cd $main && git commit -m x"        'cd into main checkout first: blocked'
check 2 "$wt"   "$wt"   "cd '$main' && git commit -m x"      'cd into quoted main path: blocked'
check 0 "$main" "$main" "cd $wt && git commit -m x"          'cd into worktree first: allowed'
check 2 "$wt"   "$wt"   "git -C $main push"                  'git -C main checkout: blocked'
check 0 "$main" "$main" "git -C $wt commit -m x"             'git -C worktree: allowed'
check 2 "$main" "$tmp/not-a-repo" 'git commit -m x'          'not a repo: fail safe, blocked'
check 0 "$main" "$main" 'ls .git/hooks/pre-push'             'read-only command mentioning pre-push: allowed'
check 0 "$main" "$main" 'cat .claude/hooks/pre-commit-check' 'read-only command mentioning pre-commit: allowed'
check 0 "$main" "$main" 'grep -rn "git commit" docs'         'grep for the words: allowed'
check 0 "$main" "$main" 'git log --oneline -5'               'read-only git on main: allowed'
check 0 "$main" "$main" ''                                   'empty command: allowed'

exit "$fail"
