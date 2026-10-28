// Who the suite acts as, and the scratch records it acts on.
//
// Seeded data (seeds/dev.json and its fixture cases) is READ, never written:
// the same rule the integration tier keeps, so a suite run cannot change what
// the next one starts from. Writes land in scratch firm clients and cases the
// suite opens itself.
import type { CaseChapter, FirmClient } from '@insolvia-ai/api-client';

import { contractClient, type ContractClient } from './client.ts';
import { seeded } from './env.ts';
import { portalToken, staffToken } from './tokens.ts';

/** The seeded firm's administrator (seeds/dev.json `admin`). */
export function firmAdmin(): ContractClient {
  return contractClient(() => staffToken(seeded.firmAdmin.subject));
}

/** A firm user by subject — someone the suite added to the firm. */
export function firmUser(subject: string): ContractClient {
  return contractClient(() => staffToken(subject));
}

/** A portal client by subject. */
export function portalClient(subject: string): ContractClient {
  return contractClient(() => portalToken(subject));
}

/** A unique-enough label for scratch records, so runs never collide. */
export function tag(): string {
  return crypto.randomUUID().slice(0, 8);
}

/** A scratch firm client in the seeded firm. */
export async function scratchClient(given = 'Scratch'): Promise<FirmClient> {
  return firmAdmin().api.createFirmClient({
    name: { given, surname: `Contract-${tag()}` },
    email: `client-${tag()}@insolvia.test`,
  });
}

/**
 * Where scratch cases are opened: a court no fixture case uses, so the
 * fixture cases stay findable (fixtures.ts) however many scratch cases a
 * run leaves behind.
 */
export const SCRATCH_COURT = { court: 'flnb', division: 'gainesville' } as const;

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
  return { caseId: opened.id, client };
}
