import type { FilingRole, FirmClient } from '@insolvia-ai/api-client';

/**
 * How a firm client (ADR 0022) is named, in the two orders the app uses.
 *
 * `sortName` — "Surname, Given" — is how a client reads in a LIST or a
 * PICKER, because the directory is ordered by surname (`GET
 * /v1/firm/clients`) and a list read top to bottom should read in the order
 * it is sorted. It is also a selector contract: `e2e/support/scratch-case.ts`
 * finds the scratch client by it.
 *
 * `displayName` — "Given Middle Surname" — is how a client reads as the
 * subject of a page: the record's heading, a confirmation dialog.
 *
 * Both fall back to whichever half of the name exists (the server requires
 * one of `surname` / `given`), and never to the id: an unnamed row is a data
 * fault worth seeing as such, not a UUID dressed as a name.
 */
export function sortName(client: Pick<FirmClient, 'name'>): string {
  const { given, surname } = client.name;
  const first = given?.trim() ?? '';
  const last = surname?.trim() ?? '';
  if (first !== '' && last !== '') return `${last}, ${first}`;
  return last || first || 'Unnamed client';
}

export function displayName(client: Pick<FirmClient, 'name'>): string {
  const { given, middle, surname, suffix } = client.name;
  const parts = [given, middle, surname, suffix]
    .map((part) => part?.trim() ?? '')
    .filter((part) => part !== '');
  return parts.length === 0 ? 'Unnamed client' : parts.join(' ');
}

/** Which debtor of a case a client is, in words — never the wire's snake_case. */
export const FILING_ROLE_LABEL: Readonly<Record<FilingRole, string>> = {
  debtor_1: 'Debtor 1',
  debtor_2: 'Debtor 2',
  non_filing_spouse: 'Non-filing spouse',
};
