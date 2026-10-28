// The coverage gate: every public method of the shipped client must be
// called by the suite at least once, or the run FAILS.
//
// Test files run in worker processes, so each one appends the method names it
// calls to a file in a per-run directory (src/support/client.ts); this global
// teardown reads them all and compares against the client class's prototype —
// the list is read from the class itself, so a method added to the client
// without a contract test fails here the next run, by name.
//
// Skipped when the run was filtered (a file or -t argument), because a partial
// run cannot cover everything and saying so would be noise.
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { InsolviaApiClient } from '@insolvia-ai/api-client';
import type { TestProject } from 'vitest/node';

declare module 'vitest' {
  export interface ProvidedContext {
    coverageDir: string;
  }
}

function clientMethods(): string[] {
  return Object.getOwnPropertyNames(InsolviaApiClient.prototype)
    .filter(
      (name) =>
        name !== 'constructor' &&
        typeof Object.getOwnPropertyDescriptor(InsolviaApiClient.prototype, name)?.value ===
          'function',
    )
    .sort();
}

export default function setup(project: TestProject): () => void {
  const dir = mkdtempSync(join(tmpdir(), 'insolvia-contract-coverage-'));
  project.provide('coverageDir', dir);
  // `vitest run <file filter>` or `-t <name>`: anything past `run` that is
  // not a flag is a filter.
  const runAt = process.argv.indexOf('run');
  const filtered =
    project.vitest.config.testNamePattern !== undefined ||
    (runAt >= 0 && process.argv.slice(runAt + 1).some((arg) => !arg.startsWith('-')));

  return () => {
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
    if (filtered) {
      console.log(`${summary} (filtered run — the gate is not enforced)`);
      return;
    }
    console.log(summary);
    if (uncovered.length > 0) {
      process.exitCode = 1;
      throw new Error(
        `the contract suite never called ${uncovered.length} InsolviaApiClient ` +
          `method(s): ${uncovered.join(', ')}. Every public client method needs a ` +
          'contract test (contract/CLAUDE.md).',
      );
    }
  };
}
