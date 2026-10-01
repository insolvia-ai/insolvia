import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import { installFakeBrowser, jsonResponse, TEST_AUTH_CONFIG } from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

import { bodiesOf, clientFetch, firmClient, member } from './testing';
import type { Respond } from './testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const DUPE = firmClient('00000000-0000-4000-8000-0000000c11a1', 'Jordy', 'Example');
const KEEP = firmClient('00000000-0000-4000-8000-0000000c11a2', 'Jordan', 'Example');
const GONE = firmClient('00000000-0000-4000-8000-0000000c11a3', 'Old', 'Record', {
  status: 'archived',
});
const MERGED = { ...DUPE, status: 'archived', merged_into: KEEP.id };

/**
 * The directory, each record, their (empty) case lists, and the merge —
 * `merge` answers the POST, so a test can refuse it.
 */
function directory(
  overrides: { dupe?: Record<string, unknown>; merge?: () => Response } = {},
): Respond {
  const records: Record<string, Record<string, unknown>> = {
    [DUPE.id]: overrides.dupe ?? DUPE,
    [KEEP.id]: KEEP,
    [GONE.id]: GONE,
  };
  return (url, init) => {
    if (url.endsWith('/v1/firm/clients')) {
      return jsonResponse(200, { clients: Object.values(records) });
    }
    if (url.endsWith('/merge') && init?.method === 'POST') {
      if (overrides.merge !== undefined) return overrides.merge();
      return jsonResponse(200, { client: KEEP, merged: MERGED });
    }
    if (url.endsWith('/cases')) return jsonResponse(200, { cases: [] });
    const id = url.split('/v1/firm/clients/')[1];
    if (id !== undefined && records[id] !== undefined) {
      return jsonResponse(200, records[id]);
    }
    return undefined;
  };
}

/**
 * "Merge into another client…" on the client record (ADR 0022's PR 7 /
 * #354), through the real router, signed in.
 *
 * Asserted: only other ACTIVE clients are offered; nothing is sent until a
 * client is chosen and Merge pressed; the POST goes to the SURVIVOR naming
 * this client; success lands on the survivor; a 409 is shown in the
 * server's words; a merged client reads as merged, links to its survivor
 * and offers nothing; and `view_only` offers no merge.
 */
describe('merging a client', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(respond: Respond = directory(), me: unknown = member()) {
    const fetchMock = clientFetch(respond, me);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/clients/${DUPE.id}` });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('merges this client into the chosen one, and lands on the survivor', async () => {
    const fetchMock = signedIn();
    await screen.findByRole('heading', { name: 'Jordy Example' });
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Merge into another client…' }));
    expect(await screen.findByText('Merge Jordy Example into another client?')).toBeTruthy();
    await user.press(await screen.findByRole('combobox', { name: 'Client to keep' }));

    // Neither this client nor an archived one can survive a merge.
    expect(screen.queryByRole('option', { name: 'Example, Jordy' })).toBeNull();
    expect(screen.queryByRole('option', { name: 'Record, Old' })).toBeNull();
    await user.press(await screen.findByRole('option', { name: 'Example, Jordan' }));
    expect(bodiesOf(fetchMock, 'POST', '/merge')).toEqual([]);
    await user.press(screen.getByRole('button', { name: 'Merge' }));

    expect(await screen.findByRole('heading', { name: 'Jordan Example' })).toBeTruthy();
    const posts = (fetchMock.mock.calls as [string, RequestInit | undefined][]).filter(
      ([url, init]) => url.endsWith('/merge') && init?.method === 'POST',
    );
    expect(posts.map(([url]) => url)).toEqual([
      expect.stringContaining(`/v1/firm/clients/${KEEP.id}/merge`),
    ]);
    expect(bodiesOf(fetchMock, 'POST', '/merge')).toEqual([{ merged_client_id: DUPE.id }]);
  });

  it('shows a refusal in the server’s own words and stays put', async () => {
    const message =
      "These two clients are both debtors on the same case. Link that case's debtor to " +
      'the right client first, then merge.';
    signedIn(directory({ merge: () => jsonResponse(409, { error: 'ConflictError', message }) }));
    await screen.findByRole('heading', { name: 'Jordy Example' });
    const user = userEvent.setup();

    await user.press(screen.getByRole('button', { name: 'Merge into another client…' }));
    await user.press(await screen.findByRole('combobox', { name: 'Client to keep' }));
    await user.press(await screen.findByRole('option', { name: 'Example, Jordan' }));
    await user.press(screen.getByRole('button', { name: 'Merge' }));

    expect(await screen.findByText(message)).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Jordy Example' })).toBeTruthy();
  });

  it('shows a merged client as merged, linked to its survivor, with nothing to do', async () => {
    signedIn(directory({ dupe: MERGED }));
    await screen.findByRole('heading', { name: 'Jordy Example' });

    expect(screen.getByText('Merged')).toBeTruthy();
    expect(await screen.findByText('Jordan Example')).toBeTruthy();
    for (const name of [
      'Edit details',
      'Restore',
      'Archive',
      'Merge into another client…',
      'Start a case for this client',
    ]) {
      expect(screen.queryByRole('button', { name })).toBeNull();
    }
  });

  it('offers no merge at `view_only`', async () => {
    signedIn(directory(), member({ clients: 'view_only' }));
    await screen.findByRole('heading', { name: 'Jordy Example' });

    expect(screen.queryByRole('button', { name: 'Merge into another client…' })).toBeNull();
  });
});
