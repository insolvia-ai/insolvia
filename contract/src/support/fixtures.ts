// The seeded fixture cases (seeds/dev.json → seeds/fixtures/v4), found the
// way a client would find them: through the API. Read-only — see actors.ts.
import type { Case } from '@insolvia-ai/api-client';

import { firmAdmin } from './actors.ts';

/**
 * Fixture cases sit in flmb/tampa; the suite's scratch cases never do
 * (actors.ts opens them in flnb/gainesville), so court, division and chapter
 * pick a fixture case out unambiguously.
 */
export async function fixtureCase(chapter: 7 | 13): Promise<Case> {
  // Every page: a long-lived stack accumulates scratch cases run after run.
  const api = firmAdmin().api;
  let found: Case | undefined;
  let cursor: string | undefined;
  do {
    const page = await api.listCases({ limit: 100, cursor });
    found = page.cases.find(
      (c) =>
        c.court === 'flmb' &&
        c.division === 'tampa' &&
        c.chapter === chapter &&
        c.status === 'intake',
    );
    cursor = page.nextCursor;
  } while (found === undefined && cursor !== undefined);
  if (!found) {
    throw new Error(
      `no seeded chapter ${chapter} fixture case — was the stack seeded from seeds/dev.json?`,
    );
  }
  return found;
}
