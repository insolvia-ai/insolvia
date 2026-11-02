// The coverage gates (ADR 0029 rule 2): every surface is driven through the
// client its callers run, and the run FAILS when part of that client was
// never exercised.
//
//   api    every public method of the shipped `InsolviaApiClient`
//   admin  every public method of the portal's `AdminClient`
//   mcp    every tool the MCP server lists in `tools/list`
//
// Jest runs it in three places, which this file is shared between:
// - global.setup.ts (globalSetup) makes a per-run directory and hands it to
//   the tests through the environment;
// - every call through an instrumented client (support/client.ts,
//   support/admin.ts, support/mcp.ts) appends its name to a per-surface file
//   in it, and every MCP tool listing is saved there too;
// - global.teardown.ts (globalTeardown) reads them all. The API and admin
//   lists are read from each class's prototype and the MCP list from the
//   server's own answer, so a method or tool added without a contract test
//   fails the next run, by name.
//
// Skipped when the run was filtered (a path pattern or `-t`), because a
// partial run cannot cover everything and saying so would be noise — and on
// the deployed target, whose offline-only specs are skipped.
import { appendFileSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { AdminClient } from '@insolvia-ai/admin-client';
import { InsolviaApiClient } from '@insolvia-ai/api-client';

const DIR = 'CONTRACT_COVERAGE_DIR';
const FILTERED = 'CONTRACT_COVERAGE_FILTERED';

export type Surface = 'api' | 'admin' | 'mcp';

/** The slice of Jest's global config the gate reads. */
export interface RunConfig {
  readonly testNamePattern?: string | undefined;
  readonly testPathPattern?: string | undefined;
}

function publicMethods(prototype: object, hidden: ReadonlySet<string> = new Set()): string[] {
  return Object.getOwnPropertyNames(prototype)
    .filter(
      (name) =>
        name !== 'constructor' &&
        !hidden.has(name) &&
        typeof Object.getOwnPropertyDescriptor(prototype, name)?.value === 'function',
    )
    .sort();
}

export function clientMethods(): string[] {
  return publicMethods(InsolviaApiClient.prototype);
}

// TypeScript's `private` is erased: `request` is on the prototype at runtime
// though no caller can reach it. The type below stops compiling if it ever
// becomes public, so this list cannot hide a method a caller can call.
const ADMIN_PRIVATE = new Set(['request']);
type AdminPrivateStaysPrivate = 'request' extends keyof AdminClient ? never : true;
const adminPrivateStaysPrivate: AdminPrivateStaysPrivate = true;
void adminPrivateStaysPrivate;

export function adminClientMethods(): string[] {
  return publicMethods(AdminClient.prototype, ADMIN_PRIVATE);
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

/** Notes one call of a method (api, admin) or a tool (mcp). */
export function noteCall(surface: Surface, name: string): void {
  appendFileSync(join(coverageDir(), `${process.pid}.${surface}.log`), `${name}\n`);
}

/** Saves what the MCP server's `tools/list` answered, for the mcp gate. */
export function noteToolListing(tools: readonly string[]): void {
  writeFileSync(join(coverageDir(), `${process.pid}.mcp-tools.json`), JSON.stringify(tools));
}

/** Everything the run noted, read back by the teardown. */
export function readRun(dir: string): {
  called: Record<Surface, Set<string>>;
  listedTools: Set<string> | null;
} {
  const called: Record<Surface, Set<string>> = {
    api: new Set(),
    admin: new Set(),
    mcp: new Set(),
  };
  let listedTools: Set<string> | null = null;
  for (const file of readdirSync(dir)) {
    const text = readFileSync(join(dir, file), 'utf8');
    if (file.endsWith('.mcp-tools.json')) {
      listedTools ??= new Set();
      for (const tool of JSON.parse(text) as string[]) listedTools.add(tool);
      continue;
    }
    const surface = /\.(api|admin|mcp)\.log$/.exec(file)?.[1] as Surface | undefined;
    if (!surface) continue;
    for (const line of text.split('\n')) {
      if (line) called[surface].add(line);
    }
  }
  return { called, listedTools };
}
