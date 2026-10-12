/**
 * "Recently viewed cases" — a per-viewer trail of which cases this browser
 * opened, for the dashboard's first card (issue 14.7 / #359).
 *
 * **Per-viewer, not per-firm.** Two colleagues at the same firm see different
 * rows here, on purpose: it is a shortcut back to what THIS person was just
 * looking at, the same way a browser's own history is per-profile. That is
 * exactly what `localStorage` gives for free and what a server-side "recently
 * viewed" table would not — it would need its own accessor plumbing to answer
 * "recent for whom", for a feature that is a convenience, not a record.
 *
 * A PLAIN LIST OF IDS, not the case bodies. Storing the id and re-reading the
 * case (`getCase`) is what keeps a stale localStorage entry from ever showing
 * stale chapter, status or debtor names — the id is a pointer, and pointers do
 * not go out of date the way cached rows do. It also means a case that moved
 * out of this viewer's reach (a permission withdrawn, ADR 0009) simply fails to
 * load and drops out of the list, rather than showing rows for cases the
 * viewer can no longer open.
 *
 * Uses `@/platform/browser`'s persistent store — this app's one localStorage
 * seam — rather than a new one; see that file for why every read and write
 * already tolerates "there is no browser here" and a throwing store.
 */

import { persistentStore, readFrom, writeTo } from '@/platform/browser';

const STORAGE_KEY = 'insolvia.recent-cases';

/** How many case ids the trail keeps. Only the front few are ever shown. */
export const MAX_RECENT_CASES = 8;

/**
 * Every case id this browser has recorded as viewed, most recent first.
 *
 * `null`/invalid JSON, a non-array, or a non-string entry all read as "no
 * history" rather than throwing — the same "absent store reads as empty"
 * trade `platform/browser.ts` makes throughout, so a corrupted or
 * hand-edited value degrades to the dashboard's empty-history fallback
 * instead of taking the page down.
 */
export function recentCaseIds(): readonly string[] {
  const raw = readFrom(persistentStore(), STORAGE_KEY);
  if (raw === null) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((entry): entry is string => typeof entry === 'string');
  } catch {
    return [];
  }
}

/**
 * Records that `caseId` was just opened — moved to the front if it was
 * already in the trail, inserted if not, and the trail trimmed to
 * {@link MAX_RECENT_CASES}.
 *
 * Called once per successful case load, from `CaseShell` — see its own
 * comment for why there and not from each of the six case screens.
 */
export function recordRecentCase(caseId: string): void {
  const deduped = [caseId, ...recentCaseIds().filter((id) => id !== caseId)];
  writeTo(persistentStore(), STORAGE_KEY, JSON.stringify(deduped.slice(0, MAX_RECENT_CASES)));
}
