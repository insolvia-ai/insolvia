// A spec that cannot run on this target is registered SKIPPED, with its
// reason in its name, never left out or returned from early (ADR 0029 rule 2):
// Jest's verbose report lists it by name and says why.
import { describe, it } from '@jest/globals';

import { ENVIRONMENT, TARGET } from './target.ts';

interface Gate {
  readonly it: (name: string, fn: () => Promise<void>, timeout?: number) => void;
  readonly describe: (name: string, fn: () => void) => void;
}

/**
 * Runs the spec when `runs`, else registers it SKIPPED with `label` in its
 * name — so Jest's report lists it by name and says why. Never a silent
 * skip, and never an early `return` inside a spec.
 */
function gate(runs: boolean, label: string): Gate {
  if (runs) return { it, describe };
  return {
    it: (name, fn, timeout) => it.skip(`${name} [${label}]`, fn, timeout),
    describe: (name, fn) => describe.skip(`${name} [${label}]`, fn),
  };
}

/**
 * THE marker for a spec only the offline stack can express — a forged or
 * claim-broken token, a subject no pool holds, anything that needs the run's
 * own signing key, or host configuration (ADR 0029 rule 2). `reason` is
 * mandatory and lands in the skipped spec's name on the deployed target.
 */
export function offlineOnly(reason: string): Gate {
  return gate(TARGET === 'offline', `offline only: ${reason}`);
}

/** A spec about the REAL pool, which the offline stack does not have. */
export function deployedOnly(reason: string): Gate {
  return gate(TARGET === 'deployed', `deployed only: ${reason}`);
}

/**
 * A spec that needs a kind of person this target's seed may not hold — a
 * colleague without access to every case, say; seeds/dev.json has one seat.
 * `available` says whether this target has one; `who` names what is missing.
 */
export function needsSeeded(available: boolean, who: string): Gate {
  return gate(available, `seeds/${ENVIRONMENT}.json has no ${who}`);
}

/**
 * A spec that fetches a presigned blob URL. The dev stack
 * (backend/docker-compose.yml) keeps its blob store as an in-memory twin whose
 * URLs no client can fetch; the offline stack serves them
 * (scripts/aux-server.ts) and staging's are the real bucket's, which is where
 * the bytes this proves cross the deployed role and bucket policy.
 */
export function needsBlobUrls(): Gate {
  return gate(
    !(TARGET === 'deployed' && ENVIRONMENT === 'dev'),
    'the dev stack’s blob store is an in-memory twin with no URL to fetch',
  );
}
