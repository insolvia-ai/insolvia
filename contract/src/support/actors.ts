// Who the suite acts as, and the scratch records it acts on.
//
// Seeded data (seeds/<environment>.json and its fixture cases) is READ, never
// written, so a run cannot change what the next one starts from. Writes land
// in scratch firm clients and cases the suite opens itself — and, because the
// deployed target is a stack that outlives the run, every scratch case is
// deleted and every scratch client archived when its file ends
// (src/scratch.setup.ts). Neither has a hard delete (a case's delete is soft,
// #355; clients have none, ADR 0022), so the rows stay out of every read
// rather than out of the table: the cost the integration tier paid for its
// copy and merge specs, now paid by each scratch record, and said here so
// nobody mistakes the rows for a leak.
import {
  ApiException,
  type CaseChapter,
  type FirmClient,
  type InsolviaApiClient,
} from '@insolvia-ai/api-client';

import { contractClient, type ContractClient } from './client.ts';
import { seeded } from './env.ts';
import { freshSignIn, session } from './session.ts';
import { TARGET } from './target.ts';
import { portalToken, staffToken } from './tokens.ts';

/** The bearer a seeded person sends: minted offline, the run's real one deployed. */
export function tokenOf(handle: string): string {
  if (TARGET === 'deployed') return session(handle).token;
  const who = handle === 'client' ? seeded.portalClient : seeded.firmAdmin;
  if (who.handle !== handle) {
    throw new Error(`the offline stack signs for 'admin' and 'client' only, not '${handle}'`);
  }
  return handle === 'client' ? portalToken(who.subject) : staffToken(who.subject);
}

/** A seeded firm user, by handle (seeds/<environment>.json). */
export function seededStaff(handle: string): ContractClient {
  return contractClient(() => tokenOf(handle));
}

/** The seeded firm's administrator (seed handle `admin`). */
export function firmAdmin(): ContractClient {
  return seededStaff('admin');
}

/**
 * The seeded administrator, signed in JUST NOW — for the acts that need a
 * recent `auth_time` (ADR 0024). Offline every minted token is fresh;
 * deployed this is a new SRP sign-in, the suite's `prompt=login`.
 */
export async function freshFirmAdmin(): Promise<ContractClient> {
  if (TARGET === 'offline') return firmAdmin();
  const token = (await freshSignIn('admin')).AccessToken;
  return contractClient(token);
}

/** The seeded portal client (seed handle `client`), through the portal's app client. */
export function seededPortalClient(): ContractClient {
  return contractClient(() => tokenOf('client'));
}

/** A firm user by subject — someone the suite added to the firm. Offline only. */
export function firmUser(subject: string): ContractClient {
  return contractClient(() => staffToken(subject));
}

/** A portal client by subject — someone the suite invited. Offline only. */
export function portalClient(subject: string): ContractClient {
  return contractClient(() => portalToken(subject));
}

/** A unique-enough label for scratch records, so runs never collide. */
export function tag(): string {
  return crypto.randomUUID().slice(0, 8);
}

// ── scratch records, undone when their file ends ────────────────

const scratchCases = new Set<string>();
const scratchClients = new Set<string>();

/** Registers a case a spec opened itself, so the file's teardown deletes it. */
export function trackScratchCase(id: string): string {
  scratchCases.add(id);
  return id;
}

/** Registers a client a spec added itself, so the file's teardown archives it. */
export function trackScratchClient(id: string): string {
  scratchClients.add(id);
  return id;
}

/** 404 (already gone) and 409 (already archived, merged) mean the undo is done. */
async function settled(call: Promise<unknown>): Promise<void> {
  try {
    await call;
  } catch (error) {
    if (error instanceof ApiException && (error.statusCode === 404 || error.statusCode === 409)) {
      return;
    }
    throw error;
  }
}

/** Deletes every scratch case, then archives every scratch client. */
export async function cleanUpScratch(): Promise<void> {
  const api: InsolviaApiClient = firmAdmin().api;
  for (const id of scratchCases) await settled(api.deleteCase(id));
  scratchCases.clear();
  for (const id of scratchClients) await settled(api.setFirmClientStatus(id, 'archived'));
  scratchClients.clear();
}

/** A scratch firm client in the seeded firm. */
export async function scratchClient(given = 'Scratch'): Promise<FirmClient> {
  const client = await firmAdmin().api.createFirmClient({
    name: { given, surname: `Contract-${tag()}` },
    email: `client-${tag()}@insolvia.test`,
  });
  trackScratchClient(client.id);
  return client;
}

/**
 * Where scratch cases are opened: a court no fixture case uses (fixture cases
 * sit in flmb/tampa, seeds/fixtures), so the fixture cases stay findable
 * however many scratch cases a stack holds. The browser suite keeps its own
 * scratch case in a third court (e2e/support/scratch-case.ts).
 */
export const SCRATCH_COURT = { court: 'flnb', division: 'gainesville' } as const;
/** The name the court registry prints for it — what `district` reads back as. */
export const SCRATCH_DISTRICT = 'Northern District of Florida';

/** A scratch case in the seeded firm, Debtor 1 copied from a fresh client. */
export async function scratchCase(chapter: CaseChapter = 7): Promise<{
  caseId: string;
  client: FirmClient;
}> {
  const client = await scratchClient();
  const opened = await firmAdmin().api.createCase({
    chapter,
    ...SCRATCH_COURT,
    clientIds: [client.id],
  });
  return { caseId: trackScratchCase(opened.id), client };
}
