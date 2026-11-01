// Which stack the suite is aimed at, and who exists there (ADR 0029 rule 2).
//
//   offline   the default — the .NET hosts on a throwaway Postgres, composed
//             by contract/scripts/dev-test.sh, tokens signed by the run's own
//             key. Every PR (`Contract (.NET)`), any laptop.
//   deployed  a running dev stack or staging, signed in by real Cognito SRP as
//             the people in seeds/<environment>.json (support/session.ts).
//             After each staging API deploy (api-staging.yml), and on a
//             developer's dev stack on demand (`dev-test.sh --target deployed`).
//
// THERE IS NO PRODUCTION TARGET. `CONTRACT_ENVIRONMENT` must name dev or
// staging, and the global setup (src/global.setup.ts) refuses to sign anyone
// in unless the API's own /health reports the environment that name implies.
//
// support/gates.ts holds the one way a spec says it cannot run on a target;
// this file stays free of Jest, because the global setup reads it.
//
// ONE FILE DECIDES WHO EXISTS: seeds/<environment>.json, the file the seeder
// loaded — the same lookup e2e/support/env.ts does — so the suite and the seed
// cannot drift onto two different people. The offline stack is seeded from
// seeds/dev.json by the migrator, so offline reads that file too.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export type Target = 'offline' | 'deployed';
export type Environment = 'dev' | 'staging';

const ENVIRONMENTS: readonly Environment[] = ['dev', 'staging'];

/** What /health reports per environment; the dev stack runs as `local`. */
const API_ENVIRONMENT: Record<Environment, string> = { dev: 'local', staging: 'staging' };

// Jest runs this file transpiled to CommonJS (jest.base.js), where
// `import.meta` does not exist and `__dirname` does.
const REPO = join(__dirname, '..', '..', '..');

function readTarget(): Target {
  const raw = process.env.CONTRACT_TARGET?.trim() || 'offline';
  if (raw !== 'offline' && raw !== 'deployed') {
    throw new Error(`CONTRACT_TARGET must be offline or deployed, not '${raw}'.`);
  }
  return raw;
}

function readEnvironment(target: Target): Environment {
  if (target === 'offline') return 'dev';
  const raw = process.env.CONTRACT_ENVIRONMENT?.trim();
  if (!raw) {
    throw new Error(
      'CONTRACT_ENVIRONMENT is not set. The deployed target takes every input from the ' +
        'environment, with no defaults — see contract/README.md.',
    );
  }
  if (!(ENVIRONMENTS as readonly string[]).includes(raw)) {
    throw new Error(
      `CONTRACT_ENVIRONMENT must be one of ${ENVIRONMENTS.join(', ')}, not '${raw}'. ` +
        'There is no production target: production holds real case data and no test ' +
        'identity (ADR 0021, ADR 0029).',
    );
  }
  return raw as Environment;
}

export const TARGET: Target = readTarget();
export const ENVIRONMENT: Environment = readEnvironment(TARGET);
/** The `environment` the API's /health must report for this run. */
export const EXPECTED_API_ENVIRONMENT = API_ENVIRONMENT[ENVIRONMENT];

// ── who exists ──────────────────────────────────────────────────

/** A person seeds/<environment>.json names: a firm user or a portal client. */
export interface SeededPerson {
  readonly handle: string;
  readonly email: string;
  readonly kind: 'staff' | 'client';
  readonly firmName: string;
  readonly role?: string;
  readonly isAdmin: boolean;
  readonly accessAllCases: boolean;
  /** Portal clients only. */
  readonly displayName?: string;
  readonly roles?: readonly string[];
}

interface SeedFile {
  readonly firms?: readonly {
    readonly name: string;
    readonly users?: readonly Record<string, unknown>[];
  }[];
  readonly cases?: readonly {
    readonly fixture: string;
    readonly case: string;
    readonly firm: string;
    readonly clients?: readonly Record<string, unknown>[];
  }[];
}

/** One fixture case's spec, from seeds/fixtures/<version>/cases.json. */
export interface FixtureSpec {
  readonly handle: string;
  readonly chapter: number;
  readonly court: string;
  readonly division: string;
  readonly debtors?: { readonly debtor_1?: Record<string, unknown> };
  readonly collections?: { readonly creditors?: readonly Record<string, unknown>[] };
}

function readJson<T>(...path: string[]): T {
  return JSON.parse(readFileSync(join(REPO, ...path), 'utf8')) as T;
}

const seedFile = readJson<SeedFile>('seeds', `${ENVIRONMENT}.json`);

function loadPeople(): SeededPerson[] {
  const people: SeededPerson[] = [];
  for (const firm of seedFile.firms ?? []) {
    for (const user of firm.users ?? []) {
      if (typeof user.handle !== 'string' || typeof user.email !== 'string') continue;
      people.push({
        handle: user.handle,
        email: user.email,
        kind: 'staff',
        firmName: firm.name,
        ...(typeof user.role === 'string' ? { role: user.role } : {}),
        isAdmin: user.isAdmin === true,
        accessAllCases: user.accessAllCases === true,
      });
    }
  }
  for (const entry of seedFile.cases ?? []) {
    for (const client of entry.clients ?? []) {
      if (typeof client.handle !== 'string' || typeof client.email !== 'string') continue;
      people.push({
        handle: client.handle,
        email: client.email,
        kind: 'client',
        firmName: entry.firm,
        isAdmin: false,
        accessAllCases: false,
        ...(typeof client.displayName === 'string' ? { displayName: client.displayName } : {}),
        roles: Array.isArray(client.roles) ? (client.roles as string[]) : [],
      });
    }
  }
  return people;
}

/** Everyone the seed names, staff and portal clients alike. */
export const PEOPLE: readonly SeededPerson[] = loadPeople();

export function hasPerson(handle: string): boolean {
  return PEOPLE.some((p) => p.handle === handle);
}

export function person(handle: string): SeededPerson {
  const found = PEOPLE.find((p) => p.handle === handle);
  if (!found) {
    throw new Error(
      `seeds/${ENVIRONMENT}.json names nobody with handle '${handle}'; known: ` +
        PEOPLE.map((p) => p.handle).join(', '),
    );
  }
  return found;
}

/** The fixture cases seeds/<environment>.json loads, from their versions. */
export function fixtureSpecs(): FixtureSpec[] {
  const specs: FixtureSpec[] = [];
  for (const entry of seedFile.cases ?? []) {
    const version = readJson<{ cases?: readonly FixtureSpec[] }>(
      'seeds',
      'fixtures',
      entry.fixture,
      'cases.json',
    );
    const spec = version.cases?.find((c) => c.handle === entry.case);
    if (spec) specs.push(spec);
  }
  return specs;
}
