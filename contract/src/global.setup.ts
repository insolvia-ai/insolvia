// Jest's globalSetup: the coverage gate's run directory (coverage.ts), and on
// the deployed target the refusal and the sign-in (support/target.ts,
// support/session.ts) — before any test file loads, so a wrong target fails
// by name before a single spec has written anything.
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { type RunConfig, startRun } from './coverage.ts';
import { startSessions } from './support/session.ts';
import { ENVIRONMENT, EXPECTED_API_ENVIRONMENT, TARGET } from './support/target.ts';

/**
 * The deployed target's last word on where it is aimed: the API's own
 * /health must report the environment CONTRACT_ENVIRONMENT names. A
 * production API — or anything else — is refused before anyone signs in.
 */
async function refuseTheWrongTarget(): Promise<void> {
  const base = process.env.CONTRACT_API_URL?.trim().replace(/\/$/, '');
  if (!base) throw new Error('CONTRACT_API_URL is not set — see contract/README.md.');
  const response = await fetch(`${base}/health`);
  const health = (await response.json().catch(() => ({}))) as { environment?: unknown };
  if (health.environment === 'production') {
    throw new Error(
      `${base} reports environment "production". The contract suite has no production ` +
        'target and signs nobody in there (ADR 0029).',
    );
  }
  if (health.environment !== EXPECTED_API_ENVIRONMENT) {
    throw new Error(
      `${base}/health reports environment ${JSON.stringify(health.environment)}, but ` +
        `CONTRACT_ENVIRONMENT=${ENVIRONMENT} expects "${EXPECTED_API_ENVIRONMENT}". A green run ` +
        'against the wrong environment would bless a deploy nothing tested.',
    );
  }
}

export default async function setup(config: RunConfig): Promise<void> {
  startRun(mkdtempSync(join(tmpdir(), 'insolvia-contract-coverage-')), config);
  console.log(`\ncontract target: ${TARGET} (seeds/${ENVIRONMENT}.json)`);
  if (TARGET === 'deployed') {
    await refuseTheWrongTarget();
    await startSessions();
  }
}
