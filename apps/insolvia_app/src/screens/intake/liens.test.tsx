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

import { formatMoney } from './liens';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';
const ASSET_ID = '00000000-0000-4000-8000-00000000a5e1';
const CLAIM_ID = '00000000-0000-4000-8000-0000000000f1';
const STAMP = '2026-09-01T10:00:00.000000Z';

const SAVED_ASSET = {
  id: ASSET_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: { category: { source: 'staff_typed' }, description: { source: 'staff_typed' } },
  category: 'vehicle',
  description: '2016 sedan',
  value_entire: '9000.00',
};

/** A secured claim already linked to the sedan. */
const LINKED_CLAIM = {
  id: CLAIM_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: {
    claim_class: { source: 'staff_typed' },
    amount: { source: 'staff_typed' },
    asset_id: { source: 'staff_typed' },
    lien_position: { source: 'staff_typed' },
  },
  claim_class: 'secured',
  amount: '12000.00',
  asset_id: ASSET_ID,
  lien_position: 1,
};

/** The same claim before anyone linked it — a confirmed extraction, so its
 * provenance is NOT the plain staff_typed map. */
const UNLINKED_CLAIM = {
  id: CLAIM_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: {
    claim_class: { source: 'staff_typed' },
    amount: {
      source: 'ai_extracted',
      confirmed_by: 'subject-0001',
      confirmed_at: '2026-09-01T10:00:00Z',
    },
  },
  claim_class: 'secured',
  amount: '12000.00',
};

/** `liens_json` for the sedan under the linked claim. */
const LIENS = {
  claims: [
    {
      claimId: CLAIM_ID,
      assetId: ASSET_ID,
      lienPosition: 1,
      amount: '12000.00',
      collateralDescription: '2016 sedan',
      collateralValue: '9000.00',
      seniorLiens: '0.00',
      securedAmount: '9000.00',
      unsecuredAmount: '3000.00',
      unsecuredSource: 'derived',
    },
  ],
  assets: [{ assetId: ASSET_ID, claimIds: [CLAIM_ID], securedTotal: '12000.00' }],
};

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The collateral panels (issue #345), through the real intake route. What is
 * asserted is that the figures on screen are the SERVER's — nothing here is
 * computed — and that the two actions on a property write exactly what the
 * API expects: a new claim born pointed at the property, and an existing
 * claim linked without its provenance being rewritten.
 */
describe('the collateral panels', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[]) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = routes.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
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

  const get = (fragment: string, body: unknown): Route => ({
    method: 'GET',
    fragment,
    respond: () => jsonResponse(200, body),
  });

  const common = [
    get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
    get(`/v1/cases/${CASE_ID}/creditors`, { creditors: [] }),
    get(`/v1/cases/${CASE_ID}/liens`, LIENS),
  ];

  async function openSection(label: string) {
    const user = userEvent.setup();
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

  it('formats a wire amount without parsing it as a number', () => {
    expect(formatMoney('3000.00')).toBe('$3,000.00');
    expect(formatMoney('1234567.5')).toBe('$1,234,567.50');
    expect(formatMoney('0.00')).toBe('$0.00');
  });

  it('shows the server’s secured and unsecured portions beside a saved claim', async () => {
    signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [LINKED_CLAIM] }),
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_ASSET] }),
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Edit claim 1' }));

    expect(await screen.findByText('$3,000.00')).toBeTruthy();
    // Twice: the collateral's value, and the secured portion it covers.
    expect(screen.getAllByText('$9,000.00')).toHaveLength(2);
    expect(screen.getByText('Calculated')).toBeTruthy();
    expect(screen.getByText('2016 sedan')).toBeTruthy();
  });

  it('says so when the unsecured portion was entered manually', async () => {
    signedIn([
      get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
      get(`/v1/cases/${CASE_ID}/creditors`, { creditors: [] }),
      get(`/v1/cases/${CASE_ID}/liens`, {
        claims: [
          {
            ...LIENS.claims[0],
            securedAmount: '7000.00',
            unsecuredAmount: '5000.00',
            unsecuredSource: 'manual',
          },
        ],
        assets: LIENS.assets,
      }),
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [LINKED_CLAIM] }),
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_ASSET] }),
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Edit claim 1' }));

    expect(await screen.findByText('$5,000.00')).toBeTruthy();
    expect(screen.getByText('Entered manually')).toBeTruthy();
  });

  it('asks for a save before showing figures for a new claim', async () => {
    signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [] }),
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [] }),
    ]);

    const user = await openSection('Claims');
    await user.press(await screen.findByRole('button', { name: 'Add claim' }));

    expect(await screen.findByText(/Save the claim to see/u)).toBeTruthy();
  });

  it('lists the liens on a saved property with the server’s total', async () => {
    signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_ASSET] }),
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [LINKED_CLAIM] }),
    ]);

    const user = await openSection('Property');
    await user.press(await screen.findByRole('button', { name: 'Edit asset 1' }));

    expect(await screen.findByText('$12,000.00')).toBeTruthy();
    expect(
      screen.getByText(
        'Claim 1 — Secured — $12000.00 — position 1; $9,000.00 secured, $3,000.00 unsecured',
      ),
    ).toBeTruthy();
  });

  it('hands off to a new claim already pointed at the property', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_ASSET] }),
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [] }),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/claims`,
        respond: () =>
          jsonResponse(201, {
            ...LINKED_CLAIM,
            provenance: {},
            account_last4: '4471',
          }),
      },
    ]);

    const user = await openSection('Property');
    await user.press(await screen.findByRole('button', { name: 'Edit asset 1' }));
    await user.press(await screen.findByRole('button', { name: 'Add a secured claim' }));

    // Now on the claims section, on a new claim's form, not its list.
    await user.type(await screen.findByLabelText('Account number — last four digits'), '4471');
    await user.press(screen.getByRole('button', { name: 'Save claim' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/claims')).toEqual({
        claim_class: 'secured',
        asset_id: ASSET_ID,
        account_last4: '4471',
        provenance: {
          claim_class: { source: 'staff_typed' },
          asset_id: { source: 'staff_typed' },
          account_last4: { source: 'staff_typed' },
        },
      }),
    );
  });

  it('links an existing secured claim without rewriting its provenance', async () => {
    const fetchMock = signedIn([
      get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
      get(`/v1/cases/${CASE_ID}/creditors`, { creditors: [] }),
      get(`/v1/cases/${CASE_ID}/liens`, { claims: [], assets: [] }),
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_ASSET] }),
      {
        method: 'PUT',
        fragment: `/v1/cases/${CASE_ID}/claims/${CLAIM_ID}`,
        respond: () => jsonResponse(200, LINKED_CLAIM),
      },
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [UNLINKED_CLAIM] }),
    ]);

    const user = await openSection('Property');
    await user.press(await screen.findByRole('button', { name: 'Edit asset 1' }));
    await user.press(await screen.findByRole('combobox', { name: 'Link an existing claim' }));
    await user.press(await screen.findByRole('option', { name: 'Claim 1 — Secured — $12000.00' }));
    await user.press(screen.getByRole('button', { name: 'Link claim' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'PUT', `/claims/${CLAIM_ID}`)).toEqual({
        claim_class: 'secured',
        amount: '12000.00',
        asset_id: ASSET_ID,
        provenance: {
          ...UNLINKED_CLAIM.provenance,
          asset_id: { source: 'staff_typed' },
        },
      }),
    );
    expect(await screen.findByText('Linked')).toBeTruthy();
  });
});
