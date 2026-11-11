// Jest's globalTeardown for the coverage gates; coverage.ts says how they
// work. Throwing here fails the run. The gates are enforced on a FULL OFFLINE
// run only (ADR 0029 rule 2): a filtered run cannot cover everything, and the
// deployed target skips the offline-only specs that call some methods.
import { rmSync, writeFileSync } from 'node:fs';

import { adminClientMethods, clientMethods, coverageDir, isFiltered, readRun } from './coverage.ts';
import { checkExchanges } from './support/openapi.ts';
import { TARGET } from './support/target.ts';

interface Gate {
  readonly summary: string;
  readonly failure: string | null;
}

function gate(label: string, unit: string, all: readonly string[], called: Set<string>): Gate {
  const uncovered = all.filter((name) => !called.has(name));
  return {
    summary: `${label} coverage: ${all.length - uncovered.length}/${all.length} ${unit} called, ${uncovered.length} uncovered`,
    failure: uncovered.length === 0 ? null : `${uncovered.length} ${unit}: ${uncovered.join(', ')}`,
  };
}

export default function teardown(): void {
  const dir = coverageDir();
  const { called, listedTools, exchanges } = readRun(dir);
  rmSync(dir, { recursive: true, force: true });
  // Every exchange the run made, for a person reading what the wire said.
  const out = process.env.CONTRACT_EXCHANGES_OUT;
  if (out) writeFileSync(out, exchanges.map((e) => JSON.stringify(e)).join('\n'));

  // Every exchange against the OpenAPI documents (support/openapi.ts). Unlike
  // the coverage gates this holds on any run: an exchange that happened either
  // matches its document or it does not.
  const openapi = checkExchanges(exchanges);
  console.log(openapi.summary);
  if (openapi.failures.length > 0) {
    throw new Error(
      `the OpenAPI documents disagree with the running hosts:\n  ${openapi.failures.join('\n  ')}\n` +
        'Fix the route table (tool/openapi/) or the client type it names, then run `npm run openapi`.',
    );
  }

  const gates: Gate[] = [
    gate('client', 'methods', clientMethods(), called.api),
    gate('admin-client', 'methods', adminClientMethods(), called.admin),
    listedTools === null
      ? {
          summary: 'mcp tool coverage: tools/list was never called',
          failure: 'tools/list was never called, so no tool list to check against',
        }
      : gate('mcp tool', 'tools', [...listedTools].sort(), called.mcp),
  ];

  const note = isFiltered()
    ? ' (filtered run — the gate is not enforced)'
    : TARGET !== 'offline'
      ? ` (${TARGET} target — the gate is enforced offline only)`
      : '';
  for (const { summary } of gates) console.log(`${summary}${note}`);
  if (note) return;

  const failures = gates.flatMap((g) => (g.failure === null ? [] : [g.failure]));
  if (failures.length > 0) {
    throw new Error(
      `the contract suite never exercised ${failures.join('; ')}. Every public method of ` +
        'InsolviaApiClient and AdminClient, and every tool the MCP server lists, needs a ' +
        'contract test (contract/CLAUDE.md).',
    );
  }
}
