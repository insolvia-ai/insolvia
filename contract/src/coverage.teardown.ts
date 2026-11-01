// Jest's globalTeardown for the coverage gate; coverage.ts says how it works.
// Throwing here fails the run.
import { readdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

import { clientMethods, coverageDir, isFiltered } from './coverage.ts';

export default function teardown(): void {
  const dir = coverageDir();
  const called = new Set<string>();
  for (const file of readdirSync(dir)) {
    for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
      if (line) called.add(line);
    }
  }
  rmSync(dir, { recursive: true, force: true });
  const methods = clientMethods();
  const uncovered = methods.filter((name) => !called.has(name));
  const summary = `client coverage: ${methods.length - uncovered.length}/${methods.length} methods called, ${uncovered.length} uncovered`;
  if (isFiltered()) {
    console.log(`${summary} (filtered run — the gate is not enforced)`);
    return;
  }
  console.log(summary);
  if (uncovered.length > 0) {
    throw new Error(
      `the contract suite never called ${uncovered.length} InsolviaApiClient ` +
        `method(s): ${uncovered.join(', ')}. Every public client method needs a ` +
        'contract test (contract/CLAUDE.md).',
    );
  }
}
