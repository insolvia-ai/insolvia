// Jest's globalSetup for the coverage gate; coverage.ts says how it works.
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { type RunConfig, startRun } from './coverage.ts';

export default function setup(config: RunConfig): void {
  startRun(mkdtempSync(join(tmpdir(), 'insolvia-contract-coverage-')), config);
}
