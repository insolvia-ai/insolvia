// Jest's globalTeardown for the coverage gate; coverage.ts says how it works.
// Throwing here fails the run. The gate is enforced on a FULL OFFLINE run
// only (ADR 0029 rule 2): a filtered run cannot cover everything, and the
// deployed target skips the offline-only specs that call some methods.
import { readdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

import { clientMethods, coverageDir, isFiltered } from './coverage.ts';
import { TARGET } from './support/target.ts';

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
  if (TARGET !== 'offline') {
    console.log(`${summary} (${TARGET} target — the gate is enforced offline only)`);
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
