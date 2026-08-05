import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
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

const SAVED = {
  id: '00000000-0000-4000-8000-0000000000d1',
  case_id: CASE_ID,
  filing_role: 'debtor_1',
  created_at: '2026-08-05T10:00:00.000000Z',
  updated_at: '2026-08-05T10:00:00.000000Z',
  name: { given: 'Ada', surname: 'Lovelace' },
  provenance: {
    'name.given': { source: 'staff_typed' },
    'name.surname': { source: 'staff_typed' },
  },
};

/**
 * `/cases/<id>/intake` — the structured intake (issue 8.5).
 *
 * Rendered through the **real router**, like the cases screen's suite, so a
 * route file that moved or stopped compiling fails here.
 *
 * What is asserted is nearly all about not losing work, because that is the one
 * thing this screen must never do: that a half-finished record comes back when
 * you return to it, that every populated field leaves with provenance attached
 * (the API rejects the request otherwise), and that a server's per-field
 * message reaches the field it belongs to.
 */
describe('the intake screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  /** Signs in and renders the intake, returning the fetch mock so a test can
   * read the request bodies — which is what most of these need. */
  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/intake` });
    return fetchMock;
  }

  /** The body of the last PUT to a debtor, parsed. */
  function lastSave(fetchMock: ReturnType<typeof signedIn>): Record<string, unknown> {
    const puts = fetchMock.mock.calls.filter(
      ([url, init]) => url.includes('/debtors/') && init?.method === 'PUT',
    );
    return JSON.parse(String(puts[puts.length - 1]?.[1]?.body ?? '{}'));
  }

  const noDebtors = () => jsonResponse(200, { debtors: [] });
  const savedOk = () => jsonResponse(201, SAVED);

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
  });

  it('shows a half-finished record when you come back to it', async () => {
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [SAVED] }) });

    expect(await screen.findByDisplayValue('Ada')).toBeTruthy();
    expect(screen.getByDisplayValue('Lovelace')).toBeTruthy();
  });

  it('sends provenance for every populated field', async () => {
    // The API rejects a body where a populated field has no provenance entry,
    // so a save that forgot this would 400 on every keystroke.
    const fetchMock = signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
      [`/v1/cases/${CASE_ID}/debtors`]: noDebtors,
    });

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('First name'), 'Ada');

    await waitFor(() => expect(lastSave(fetchMock).name).toEqual({ given: 'Ada' }));
    expect(lastSave(fetchMock).provenance).toEqual({
      'name.given': { source: 'staff_typed' },
    });
  });

  it('puts a server field message on the field it belongs to', async () => {
    signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: () =>
        jsonResponse(400, {
          error: 'validation failed',
          fields: { 'name.given': 'Must be a single line.' },
        }),
      [`/v1/cases/${CASE_ID}/debtors`]: noDebtors,
    });

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('First name'), 'Ada');

    expect(await screen.findByText('Must be a single line.')).toBeTruthy();
  });

  it('offers each debtor as a separate record, not a second column', async () => {
    // A joint filing is two debtor records, and a non-filing spouse may appear
    // on 106I without filing at all.
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: noDebtors });

    expect(await screen.findByRole('tab', { name: 'Debtor 1' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Debtor 2' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Non-filing spouse' })).toBeTruthy();
  });

  it('gives a new alias row an id the API will accept', async () => {
    // Without a client-minted id the API refuses the row outright: it cannot be
    // given provenance otherwise, and the request becomes unsatisfiable.
    const fetchMock = signedIn({
      [`/v1/cases/${CASE_ID}/debtors/debtor_1`]: savedOk,
      [`/v1/cases/${CASE_ID}/debtors`]: noDebtors,
    });

    const user = userEvent.setup();
    await user.press(await screen.findByText('Add another name'));
    await user.type(await screen.findByLabelText('Business name'), 'Byron and Co');

    await waitFor(() => expect(lastSave(fetchMock).other_names_used).toBeDefined());
    const aliases = lastSave(fetchMock).other_names_used as { id: string }[];
    expect(aliases[0]?.id).toMatch(/^[A-Za-z0-9_-]+$/);
  });

  it('says that changes save themselves, because there is no save button', async () => {
    signedIn({ [`/v1/cases/${CASE_ID}/debtors`]: noDebtors });

    expect(await screen.findByText('Changes save automatically')).toBeTruthy();
  });
});
