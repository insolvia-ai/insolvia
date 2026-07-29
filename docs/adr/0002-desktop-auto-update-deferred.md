# ADR 0002 — Desktop auto-update is deferred, with a hard revisit trigger

- **Status:** **Superseded by [ADR 0004](0004-react-native-replaces-flutter.md)**
  — there is no desktop build to update. Flutter is gone, both desktop targets
  are deleted rather than deferred, and D8 (which this rested on) is superseded
  by D9. The reasoning below is kept intact because it is the checklist anyone
  reversing the desktop deferral has to satisfy, and none of it stopped being
  true — see *What survives the supersession* at the end.
- **Date:** 2026-07-27
- **Relates to:** decision D8 in `docs/MVP_PLAN.md`; issues #17, #16

## Decision

**The desktop app ships with no auto-update mechanism while distribution is
hand-held and low-volume.** New builds reach people the same way the first one
did: we hand them a URL. No update feed, no differential updater, no
in-app "a new version is available" prompt.

**This expires before any firm depends on a desktop build day-to-day.** That is
the trigger, and it is not "when we have time" — it is a precondition on the
first real desktop dependency. Two things make it sharp:

- **Attorneys will not manually re-download.** A firm that installed once has a
  frozen client, and every subsequent fix — including a filing-rules or
  regulatory correction — silently does not reach them. On a product that
  produces court filings, a stale client is a correctness problem, not a
  convenience one.
- **Retrofitting an updater is far worse than building one.** The first version
  that ships without an update channel is the one version that can never be
  updated automatically; reaching it always requires the manual re-download we
  just said nobody does. Bootstrapping out of that costs a support call per
  seat.

## Context

Under D8 desktop is built but not promoted, distributed unsigned to people we
are talking to directly. At that volume an updater is pure carrying cost: we
know every installation by name and can tell each one to re-download.

The work is also not one implementation, and that is the main reason it is not
a quick "add it later" task:

- **macOS** wants a Sparkle-style appcast — an XML feed, a hosted archive per
  release, and EdDSA signing of each update so the updater will accept it.
- **Windows** wants an MSIX package with an update URI, or an installer
  framework carrying its own update check. Different packaging, different
  signing, different failure modes.

Both paths also assume code signing, which D8 explicitly defers: an unsigned
updater on macOS cannot satisfy Gatekeeper for the replacement it installs, and
on Windows every update re-triggers the SmartScreen dialog. So the revisit
trigger is really a bundle — certificates, notarization, and two updaters — and
the Windows OV/EV validation window is measured in weeks (see D8's sequencing
note). Discovering that when a firm is already dependent is discovering it too
late.

## Consequences

- Version support is manual: we track who has which build, and a fix reaches
  them only if we tell them.
- Nothing in the desktop targets may assume an update path exists. In
  particular, do not ship a client-side check that nags about a version it has
  no way to install.
- This decision rests on issue #16 keeping both desktop targets green in CI. If
  the targets rot, the day we need an updater we will be building it on top of
  a broken build — and the revisit trigger arrives with no warning, because it
  is a *prospect's* decision, not ours.
- Reversing this is a milestone, not a ticket: certificate procurement (weeks of
  lead time), notarization, then two independent updater implementations. Budget
  it as such the moment a firm's desktop dependency looks likely — not once it
  is real.

## What survives the supersession

ADR 0004 removed the subject of this decision rather than reversing it. Three
things above outlive it and should be read by anyone who proposes desktop
again:

- **The revisit trigger is still the right one.** "Before any firm depends on a
  desktop build day-to-day" was never about updaters specifically; it is the
  point at which a hand-held distribution stops being honest.
- **The last bullet's premise is void, and that is the whole change.** This ADR
  rested on issue #16 keeping both desktop targets green in CI. Under Flutter
  that was one job on a shared toolchain; under React Native, desktop means
  `react-native-macos` / `react-native-windows` — separate forks on their own
  cadence. So the targets are deleted rather than kept warm, and D9 records the
  optionality that was traded away and the (mobile, via `expo prebuild`)
  optionality bought in its place.
- **The cost estimate got larger, not smaller.** Certificates, notarization and
  two updaters were the old bill. A desktop return now adds a port before any
  of that starts. Nothing here argues against desktop; it argues against
  costing it as a ticket.
