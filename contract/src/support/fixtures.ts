// The seeded fixture cases (seeds/<environment>.json → seeds/fixtures/<v>),
// found the way a client would find them: through the API. Read-only — see
// actors.ts.
//
// Found by CONTENT, not by derived id: deriving ids is the seeder's business
// (backend/src/Insolvia.Migrator/Seeding/SeedIds.cs), and a long-lived
// environment holds older fixture versions' cases beside the current ones (a
// new version ADDS cases, seeds/README.md). So a fixture case is the one whose
// chapter, court and division match its spec, whose Debtor 1 is a client's
// copy where the spec says so (v4 onward, ADR 0022), whose creditors match the
// spec's — and whose Debtor 1 still carries the spec's name. One that matches
// on everything but that name is REPORTED, not skipped: it is the fixture
// case, modified after the seed wrote it, and since the loader leaves an
// existing row alone (ADR 0021) no re-seed will put it back.
import { isDeepStrictEqual } from 'node:util';

import type { Case } from '@insolvia-ai/api-client';

import { firmAdmin } from './actors.ts';
import { ENVIRONMENT, type FixtureSpec, fixtureSpecs } from './target.ts';

export interface FixtureCase {
  readonly case: Case;
  readonly spec: FixtureSpec;
}

const creditorKeys = (items: readonly Record<string, unknown>[]): string[] =>
  items
    .map((item) => {
      const address = (item.address ?? {}) as Record<string, unknown>;
      return `${String(item.name)}|${String(address.postal_code)}`;
    })
    .sort();

async function find(spec: FixtureSpec): Promise<Case> {
  const api = firmAdmin().api;
  const debtor1 = spec.debtors?.debtor_1 ?? {};
  const wanted = creditorKeys(spec.collections?.creditors ?? []);
  const modified: string[] = [];
  let cursor: string | undefined;
  do {
    // Every page: a long-lived stack accumulates scratch cases run after run.
    const page = await api.listCases({ limit: 100, cursor });
    for (const candidate of page.cases) {
      if (
        candidate.chapter !== spec.chapter ||
        candidate.court !== spec.court ||
        candidate.division !== spec.division
      ) {
        continue;
      }
      const debtors = await api.listDebtors(candidate.id);
      const first = debtors.find((d) => d.filing_role === 'debtor_1');
      if ('client_id' in debtor1 && !first?.client_id) continue;
      const creditors = await api.listCaseEntities(candidate.id, 'creditors');
      if (!isDeepStrictEqual(creditorKeys(creditors as never), wanted)) continue;
      if (isDeepStrictEqual(first?.name, debtor1.name)) return candidate;
      modified.push(`${candidate.id} (Debtor 1 is now ${JSON.stringify(first?.name)})`);
    }
    cursor = page.nextCursor;
  } while (cursor !== undefined);
  if (modified.length > 0) {
    throw new Error(
      `fixture case '${spec.handle}' was modified after seeding: ${modified.join(', ')}; ` +
        `the fixture's Debtor 1 is ${JSON.stringify(debtor1.name)}. The loader leaves an ` +
        'existing row alone (ADR 0021), so re-seeding will not repair it — put Debtor 1 ' +
        'back through the API, and find what wrote to it: a spec writes only to scratch.',
    );
  }
  throw new Error(
    `fixture case '${spec.handle}' is not in this stack — was it seeded from ` +
      `seeds/${ENVIRONMENT}.json? (scripts/dev-aws-seed.sh, or the seed-staging action)`,
  );
}

const found = new Map<string, Promise<Case>>();

/** Every fixture case this target's seed loads, with the spec it was seeded from. */
export async function fixtureCases(): Promise<FixtureCase[]> {
  return Promise.all(
    fixtureSpecs().map(async (spec) => {
      let pending = found.get(spec.handle);
      if (!pending) {
        pending = find(spec);
        found.set(spec.handle, pending);
      }
      return { case: await pending, spec };
    }),
  );
}

/** The seeded fixture case of one chapter. */
export async function fixtureCase(chapter: 7 | 13): Promise<Case> {
  const match = (await fixtureCases()).find((f) => f.spec.chapter === chapter);
  if (!match) throw new Error(`seeds/${ENVIRONMENT}.json loads no chapter ${chapter} fixture case`);
  return match.case;
}
