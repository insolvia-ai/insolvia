import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
  installFakeBrowser,
  jsonResponse,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const CREDITOR_ID = '00000000-0000-4000-8000-0000000000e1';

const SAVED_CREDITOR = {
  id: CREDITOR_ID,
  case_id: CASE_ID,
  created_at: '2026-09-01T10:00:00.000000Z',
  updated_at: '2026-09-01T10:00:00.000000Z',
  provenance: { name: { source: 'staff_typed' } },
  name: 'Example Bank',
};

/** One stubbed API route: a method, a URL fragment, and its answer. */
interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The generic collection editor (issue #249), driven through the real intake
 * route: sign in, switch the section picker off the debtor, and work a
 * collection.
 *
 * `routeFetch` from `@/session/testing` dispatches on the URL alone, and these
 * endpoints answer differently per METHOD on one URL — so this suite carries
 * its own dispatcher rather than teaching the shared helper a concern only
 * this screen has.
 */
describe('the intake collection sections', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[]) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = routes.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        // The case LAYOUT reads the case itself before any screen under it
        // renders. It is checked after the suite's own routes so a test can
        // still answer either URL its own way, and only for GET — the methods
        // this suite cares about are all declared above.
        if (method === 'GET' && url.endsWith(`/v1/cases/${CASE_ID}`)) {
          return Promise.resolve(jsonResponse(200, caseBody(CASE_ID)));
        }
        return Promise.reject(new Error(`unexpected ${method} ${url}`));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/intake` });
    return fetchMock;
  }

  /** The body of the last request matching `method` + `fragment`, parsed. */
  function lastBody(
    fetchMock: ReturnType<typeof signedIn>,
    method: string,
    fragment: string,
  ): Record<string, unknown> {
    const calls = fetchMock.mock.calls.filter(
      ([url, init]) => url.includes(fragment) && (init?.method ?? 'GET') === method,
    );
    return JSON.parse(String(calls[calls.length - 1]?.[1]?.body ?? '{}')) as Record<
      string,
      unknown
    >;
  }

  const noDebtors: Route = {
    method: 'GET',
    fragment: `/v1/cases/${CASE_ID}/debtors`,
    respond: () => jsonResponse(200, { debtors: [] }),
  };

  async function openSection(label: string) {
    const user = userEvent.setup();
    // Named by its Field label — the design system's Select takes its
    // accessible name from the surrounding Field.Root.
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    await user.press(await screen.findByRole('option', { name: label }));
    return user;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
  });

  it('offers every collection as a section, and the debtor stays the default', async () => {
    signedIn([noDebtors]);

    // The debtor tabs render without touching any collection endpoint.
    expect(await screen.findByRole('tab', { name: 'Debtor 1' })).toBeTruthy();

    const user = userEvent.setup();
    await user.press(screen.getByRole('combobox', { name: 'Section' }));
    for (const section of [
      'Creditors',
      'Claims',
      'Property',
      'Employment',
      'Monthly income',
      'Households',
      'Monthly expenses',
      'Dependents',
      'Codebtors',
      'Financial affairs',
    ]) {
      expect(screen.getByRole('option', { name: section })).toBeTruthy();
    }
  });

  it('lists what is already recorded when a section opens', async () => {
    signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [SAVED_CREDITOR] }),
      },
    ]);

    await openSection('Creditors');

    expect(await screen.findByText('Example Bank')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Edit creditor 1' })).toBeTruthy();
  });

  it('saves a new record whole, with staff_typed provenance for what is filled in', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(201, SAVED_CREDITOR),
      },
    ]);

    const user = await openSection('Creditors');
    await user.press(await screen.findByRole('button', { name: 'Add creditor' }));
    await user.type(await screen.findByLabelText('Creditor name'), 'Example Bank');
    await user.press(screen.getByRole('button', { name: 'Save creditor' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/creditors')).toEqual({
        name: 'Example Bank',
        provenance: { name: { source: 'staff_typed' } },
      }),
    );
    // Back on the list, showing what the server stored.
    expect(await screen.findByText('Example Bank')).toBeTruthy();
  });

  it('puts a server field message on the field it belongs to', async () => {
    signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () =>
          jsonResponse(400, {
            error: 'validation failed',
            fields: { name: 'Must be 200 characters or fewer.' },
          }),
      },
    ]);

    const user = await openSection('Creditors');
    await user.press(await screen.findByRole('button', { name: 'Add creditor' }));
    await user.type(await screen.findByLabelText('Creditor name'), 'Example Bank');
    await user.press(screen.getByRole('button', { name: 'Save creditor' }));

    expect(await screen.findByText('Must be 200 characters or fewer.')).toBeTruthy();
  });

  it('edits a record by replacing it whole', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [SAVED_CREDITOR] }),
      },
      {
        method: 'PUT',
        fragment: `/v1/cases/${CASE_ID}/creditors/${CREDITOR_ID}`,
        respond: () => jsonResponse(200, { ...SAVED_CREDITOR, name: 'Renamed Bank' }),
      },
    ]);

    const user = await openSection('Creditors');
    await user.press(await screen.findByRole('button', { name: 'Edit creditor 1' }));
    const nameField = await screen.findByLabelText('Creditor name');
    await user.clear(nameField);
    await user.type(nameField, 'Renamed Bank');
    await user.press(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'PUT', `/creditors/${CREDITOR_ID}`)).toEqual({
        name: 'Renamed Bank',
        provenance: { name: { source: 'staff_typed' } },
      }),
    );
    expect(await screen.findByText('Renamed Bank')).toBeTruthy();
  });

  it('removes a record and takes it off the list', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [SAVED_CREDITOR] }),
      },
      {
        method: 'DELETE',
        fragment: `/v1/cases/${CASE_ID}/creditors/${CREDITOR_ID}`,
        respond: () => jsonResponse(204, ''),
      },
    ]);

    const user = await openSection('Creditors');
    await user.press(await screen.findByRole('button', { name: 'Remove creditor 1' }));

    await waitFor(() => expect(screen.queryByText('Example Bank')).toBeNull());
    expect(
      fetchMock.mock.calls.some(
        ([url, init]) => init?.method === 'DELETE' && url.includes(`/creditors/${CREDITOR_ID}`),
      ),
    ).toBe(true);
  });

  it('saves a claim’s notice parties with client-minted ids, and provenance addressed by them', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () => jsonResponse(200, { claims: [] }),
      },
      // The claims spec references creditors, so opening the section also
      // loads them for the picker.
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-0000000000f2',
            case_id: CASE_ID,
            created_at: '2026-09-01T10:00:00.000000Z',
            updated_at: '2026-09-01T10:00:00.000000Z',
            provenance: {},
            notice_parties: [],
          }),
      },
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Add claim' }));
    await user.press(await screen.findByRole('button', { name: 'Add notice party' }));
    await user.type(await screen.findByLabelText('Notice party 1 — name'), 'Midland Credit');
    await user.type(
      screen.getByLabelText('Notice party 1 — account number, last four digits'),
      '4471',
    );
    await user.press(screen.getByRole('button', { name: 'Save claim' }));

    await waitFor(() => {
      const body = lastBody(fetchMock, 'POST', '/claims');
      // The row's id is minted by the client (the API requires one so
      // provenance can address the row), so assert its shape and then use it.
      const parties = body.notice_parties as readonly Record<string, unknown>[];
      expect(parties).toHaveLength(1);
      const party = parties[0];
      if (party === undefined) throw new Error('expected one notice party');
      expect(typeof party.id).toBe('string');
      expect(party.name).toBe('Midland Credit');
      expect(party.account_last4).toBe('4471');
      expect(body.provenance).toEqual({
        [`notice_parties[${String(party.id)}].name`]: { source: 'staff_typed' },
        [`notice_parties[${String(party.id)}].account_last4`]: { source: 'staff_typed' },
      });
    });
  });

  it('puts a notice-party server message on the row field it names', async () => {
    signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () => jsonResponse(200, { claims: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () =>
          jsonResponse(400, {
            error: 'validation failed',
            // Positional, exactly as core/claims.py keys them.
            fields: { 'notice_parties[0].account_last4': 'Must be up to four digits.' },
          }),
      },
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Add claim' }));
    await user.press(await screen.findByRole('button', { name: 'Add notice party' }));
    await user.type(
      await screen.findByLabelText('Notice party 1 — account number, last four digits'),
      'ABCDE',
    );
    await user.press(screen.getByRole('button', { name: 'Save claim' }));

    expect(await screen.findByText('Must be up to four digits.')).toBeTruthy();
  });

  it('removes a notice-party row, and an emptied list leaves the body', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () => jsonResponse(200, { claims: [] }),
      },
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/creditors`,
        respond: () => jsonResponse(200, { creditors: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-0000000000f3',
            case_id: CASE_ID,
            created_at: '2026-09-01T10:00:00.000000Z',
            updated_at: '2026-09-01T10:00:00.000000Z',
            provenance: { account_last4: { source: 'staff_typed' } },
            account_last4: '4471',
          }),
      },
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Add claim' }));
    await user.press(await screen.findByRole('button', { name: 'Add notice party' }));
    await user.type(await screen.findByLabelText('Notice party 1 — name'), 'Midland Credit');
    await user.press(screen.getByRole('button', { name: 'Remove notice party 1' }));
    // Something else populated, so the save has a body to send.
    await user.type(screen.getByLabelText('Account number — last four digits'), '4471');
    await user.press(screen.getByRole('button', { name: 'Save claim' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/claims')).toEqual({
        account_last4: '4471',
        provenance: { account_last4: { source: 'staff_typed' } },
      }),
    );
  });

  it('types a SOFA entry by its question, and nests its answers under payload', async () => {
    const fetchMock = signedIn([
      noDebtors,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/sofa_entries`,
        respond: () => jsonResponse(200, { sofa_entries: [] }),
      },
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/sofa_entries`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-0000000000f1',
            case_id: CASE_ID,
            created_at: '2026-09-01T10:00:00.000000Z',
            updated_at: '2026-09-01T10:00:00.000000Z',
            provenance: {
              entry_type: { source: 'staff_typed' },
              'payload.value': { source: 'staff_typed' },
            },
            entry_type: 'gift',
            payload: { value: '700.00' },
          }),
      },
    ]);

    const user = await openSection('Financial affairs');
    await user.press(await screen.findByRole('button', { name: 'Add entry' }));
    await user.press(await screen.findByRole('combobox', { name: 'What is this entry about?' }));
    await user.press(await screen.findByRole('option', { name: 'Gift' }));
    await user.type(await screen.findByLabelText('Value'), '700.00');
    await user.press(screen.getByRole('button', { name: 'Save entry' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/sofa_entries')).toEqual({
        entry_type: 'gift',
        payload: { value: '700.00' },
        provenance: {
          entry_type: { source: 'staff_typed' },
          'payload.value': { source: 'staff_typed' },
        },
      }),
    );
  });
});
