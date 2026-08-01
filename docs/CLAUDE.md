# `docs/` — agent guide

Repo-wide reference and runbooks. Area rules live in each area's own
`CLAUDE.md`; this file governs `docs/` itself.

## Four kinds, one per directory

File a new document by **how it will be read**, not what it is about. Getting
this wrong is what produced the flat pile this structure replaced.

| Where | Kind | The test |
|---|---|---|
| `reference/` | How something works, durably | You will reread it in a year; it changes only when the system does. |
| `runbooks/` | A procedure a human executes | It has steps, an order, and a "done". |
| `adr/` | A decision and its reasoning | Someone will otherwise re-litigate it. See [`adr/README.md`](adr/README.md). |
| `business/` | Company artifacts, not engineering | Different audience entirely. Self-contained `noindex` HTML. |
| `plan.md` | The single living plan | Rewritten as work lands. There is exactly one, and it is not a runbook. |

## What does *not* belong in `docs/`

This is the boundary that erodes — it is written down because it already eroded
once. **One fact, one owner:**

- **A procedure an agent must not improvise → a skill** (`.claude/skills/`).
  A skill surfaces itself at the moment the mistake would happen; a doc only
  helps someone who already knew to open it. AWS credential export, deploy
  rules, ruleset edits and design-system PRs are skills for that reason.
  **A doc must not restate a skill's body — name the skill and stop.**
- **Rules for changing one area → that area's `CLAUDE.md`**, which auto-loads
  when an agent works there. `docs/` describes how the system *works*;
  a `CLAUDE.md` constrains how you *edit* it.
- **Which command to run → `scripts/` and the `insolvia-scripts` skill.**

The reverse also holds: a skill that grows a long explanatory passage should
move that passage into `reference/` and link to it.

## Conventions

- `lowercase-kebab-case.md`. ADRs are `NNNN-kebab-title.md`, numbered in
  sequence.
- **Link, never restate.** When two documents need the same fact, one owns it
  and the other links — including across the doc/skill line above.
- Keep a document current when you change the thing it describes. A runbook
  that has been executed says so at the top rather than being deleted.
- **Superseding beats rewriting in `adr/`.** A decision that turned out wrong is
  more useful with its reasoning intact and its status changed.
- Update [`README.md`](README.md)'s table when you add or move a document.
