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
const CLAIM_ID = '00000000-0000-4000-8000-0000000000f1';
const CREDITOR_ID = '00000000-0000-4000-8000-0000000000d1';
const INPUT_ID = '00000000-0000-4000-8000-0000000000e1';
const STAMP = '2026-09-01T10:00:00.000000Z';

const SECURED_CLAIM = {
  id: CLAIM_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: {
    claim_class: { source: 'staff_typed' },
    amount: { source: 'staff_typed' },
    creditor_id: { source: 'staff_typed' },
    collateral_description: { source: 'staff_typed' },
  },
  claim_class: 'secured',
  amount: '12000.00',
  creditor_id: CREDITOR_ID,
  collateral_description: '2016 sedan',
};

const CREDITOR = {
  id: CREDITOR_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: { name: { source: 'staff_typed' } },
  name: 'Example Auto Finance',
};

/** A means-test record with a row already entered beside this claim. */
const INPUT_WITH_ROW = {
  id: INPUT_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: { taxes: { source: 'staff_typed' } },
  taxes: '1620.00',
  other_secured_payments: [
    {
      id: 'row-1',
      claim_id: CLAIM_ID,
      bucket: 'vehicle_1',
      monthly_payment: '415.00',
      cure_total: '900.00',
      creditor_name: 'Example Auto Finance',
      property_description: '2016 sedan',
    },
  ],
};

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The per-claim means-test panel (issue #349), through the real intake
 * route's claim form. What is asserted: the panel appears only under a
 * SECURED claim, it writes the case's one means_test_input record whole with
 * this claim's row keyed by `claim_id` and bucketed for the engine, it
 * reloads an existing row, and it never touches the claim itself.
 */
describe('the secured-payment panel', () => {
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

  function requests(fetchMock: ReturnType<typeof signedIn>, method: string, fragment: string) {
    return fetchMock.mock.calls.filter(
      ([url, init]) => url.includes(fragment) && (init?.method ?? 'GET') === method,
    );
  }

  function lastBody(
    fetchMock: ReturnType<typeof signedIn>,
    method: string,
    fragment: string,
  ): Record<string, unknown> {
    const calls = requests(fetchMock, method, fragment);
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
    get(`/v1/cases/${CASE_ID}/creditors`, { creditors: [CREDITOR] }),
    get(`/v1/cases/${CASE_ID}/liens`, { claims: [], assets: [] }),
    get(`/v1/cases/${CASE_ID}/codebtors`, { codebtors: [] }),
    get(`/v1/cases/${CASE_ID}/assets`, { assets: [] }),
    get(`/v1/cases/${CASE_ID}/claims`, { claims: [SECURED_CLAIM] }),
  ];

  async function openClaim() {
    const user = userEvent.setup();
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    await user.press(await screen.findByRole('option', { name: 'Claims' }));
    await user.press(await screen.findByRole('button', { name: 'Edit claim 1' }));
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

  it('writes a new means-test record with this claim’s row, keyed and bucketed', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [] }),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/means_test_inputs`,
        respond: () => jsonResponse(201, INPUT_WITH_ROW),
      },
    ]);
    const user = await openClaim();
    await screen.findByRole('heading', { name: 'Means test — this claim’s payment' });

    await user.press(
      screen.getByRole('combobox', { name: 'Which IRS allowance does this payment offset?' }),
    );
    await user.press(await screen.findByRole('option', { name: 'Vehicle 1 (line 13b)' }));
    await user.type(
      screen.getByLabelText('Average monthly payment over the next 60 months'),
      '415.00',
    );
    await user.type(
      screen.getByLabelText('Total past due (the cure amount; line 34 divides it by 60)'),
      '900.00',
    );
    await user.press(screen.getByRole('button', { name: 'Save means-test payment' }));

    await waitFor(() => {
      const sent = lastBody(fetchMock, 'POST', '/means_test_inputs');
      const rows = sent.other_secured_payments as Record<string, unknown>[];
      expect(rows).toHaveLength(1);
      expect(rows[0]).toEqual(
        expect.objectContaining({
          claim_id: CLAIM_ID,
          bucket: 'vehicle_1',
          monthly_payment: '415.00',
          cure_total: '900.00',
          creditor_name: 'Example Auto Finance',
          property_description: '2016 sedan',
        }),
      );
      const provenance = sent.provenance as Record<string, { source: string }>;
      const rowId = String(rows[0]!.id);
      expect(provenance[`other_secured_payments[${rowId}].bucket`]).toEqual({
        source: 'staff_typed',
      });
      expect(provenance[`other_secured_payments[${rowId}].claim_id`]).toEqual({
        source: 'staff_typed',
      });
    });
    expect(await screen.findByText(/Saved — the means test will use it/u)).toBeTruthy();
    // The claim itself was never written.
    expect(requests(fetchMock, 'PUT', '/claims/')).toHaveLength(0);
  });

  it('reloads an existing row and replaces it in place on the existing record', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [INPUT_WITH_ROW] }),
      {
        method: 'PUT',
        fragment: `/v1/cases/${CASE_ID}/means_test_inputs/${INPUT_ID}`,
        respond: () => jsonResponse(200, INPUT_WITH_ROW),
      },
    ]);
    const user = await openClaim();

    expect(await screen.findByDisplayValue('415.00')).toBeTruthy();
    await user.clear(screen.getByLabelText('Average monthly payment over the next 60 months'));
    await user.type(
      screen.getByLabelText('Average monthly payment over the next 60 months'),
      '450.00',
    );
    await user.press(screen.getByRole('button', { name: 'Save means-test payment' }));

    await waitFor(() => {
      const sent = lastBody(fetchMock, 'PUT', `/means_test_inputs/${INPUT_ID}`);
      const rows = sent.other_secured_payments as Record<string, unknown>[];
      expect(rows).toHaveLength(1);
      expect(rows[0]).toEqual(expect.objectContaining({ id: 'row-1', monthly_payment: '450.00' }));
      // The rest of the record rides along untouched.
      expect(sent.taxes).toBe('1620.00');
    });
  });

  it('is absent under a claim that is not secured', async () => {
    // Before `common`: the first matching route wins.
    signedIn([
      get(`/v1/cases/${CASE_ID}/claims`, {
        claims: [{ ...SECURED_CLAIM, claim_class: 'nonpriority_unsecured' }],
      }),
      ...common,
      get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [] }),
    ]);
    await openClaim();

    expect(await screen.findByText('Save changes')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Means test — this claim’s payment' })).toBeNull();
  });
});
