// The coverage gate: every public method of the shipped client must be
// called by the suite at least once, or the run FAILS.
//
// Jest runs it in three places, which this file is shared between:
// - coverage.setup.ts (globalSetup) makes a per-run directory and hands it to
//   the tests through the environment;
// - every call through the instrumented client (support/client.ts) appends
//   the method's name to a file in it;
// - coverage.teardown.ts (globalTeardown) reads them all and compares them
//   against the client class's prototype — the list is read from the class
//   itself, so a method added to the client without a contract test fails
//   the next run, by name.
//
// Skipped when the run was filtered (a path pattern or `-t`), because a
// partial run cannot cover everything and saying so would be noise.
import { InsolviaApiClient } from '@insolvia-ai/api-client';

const DIR = 'CONTRACT_COVERAGE_DIR';
const FILTERED = 'CONTRACT_COVERAGE_FILTERED';

/** The slice of Jest's global config the gate reads. */
export interface RunConfig {
  readonly testNamePattern?: string | undefined;
  readonly testPathPattern?: string | undefined;
}

export function clientMethods(): string[] {
  return Object.getOwnPropertyNames(InsolviaApiClient.prototype)
    .filter(
      (name) =>
        name !== 'constructor' &&
        typeof Object.getOwnPropertyDescriptor(InsolviaApiClient.prototype, name)?.value ===
          'function',
    )
    .sort();
}

/** Called from globalSetup; the test processes inherit the environment. */
export function startRun(dir: string, config: RunConfig): void {
  process.env[DIR] = dir;
  process.env[FILTERED] = config.testNamePattern || config.testPathPattern ? '1' : '';
}

/** Where this run's call logs go. Throws outside a run Jest set up. */
export function coverageDir(): string {
  const dir = process.env[DIR];
  if (!dir) {
    throw new Error(`${DIR} is not set: run the suite through Jest, whose globalSetup sets it`);
  }
  return dir;
}

export function isFiltered(): boolean {
  return process.env[FILTERED] === '1';
}
