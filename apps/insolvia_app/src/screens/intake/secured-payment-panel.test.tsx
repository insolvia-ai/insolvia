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
  caseId: CASE_ID,
  createdAt: STAMP,
  updatedAt: STAMP,
  body: {
    claimClass: 'secured',
    amount: 12000,
    creditorId: CREDITOR_ID,
    collateralDescription: '2016 sedan',
  },
  provenance: {
    claimClass: { source: 'staff_typed' },
    amount: { source: 'staff_typed' },
    creditorId: { source: 'staff_typed' },
    collateralDescription: { source: 'staff_typed' },
  },
  amended: false,
};

const CREDITOR = {
  id: CREDITOR_ID,
  caseId: CASE_ID,
  createdAt: STAMP,
  updatedAt: STAMP,
  body: {
    name: 'Example Auto Finance',
  },
  provenance: { name: { source: 'staff_typed' } },
  amended: false,
};

/** A means-test record with a row already entered beside this claim. */
const INPUT_WITH_ROW = {
  id: INPUT_ID,
  caseId: CASE_ID,
  createdAt: STAMP,
  updatedAt: STAMP,
  body: {
    taxes: 1620,
    otherSecuredPayments: [
      {
        id: 'row-1',
        claimId: CLAIM_ID,
        bucket: 'vehicle_1',
        monthlyPayment: 415,
        cureTotal: 900,
        creditorName: 'Example Auto Finance',
        propertyDescription: '2016 sedan',
      },
    ],
  },
  provenance: { taxes: { source: 'staff_typed' } },
  amended: false,
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
 * this claim's row keyed by `claimId` and bucketed for the engine, it
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
    // The request's body, with its provenance beside it, read flat.
    const sent = JSON.parse(String(calls[calls.length - 1]?.[1]?.body ?? '{}')) as {
      body?: Record<string, unknown>;
      provenance?: unknown;
    };
    return { ...sent.body, provenance: sent.provenance };
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
      const rows = sent.otherSecuredPayments as Record<string, unknown>[];
      expect(rows).toHaveLength(1);
      expect(rows[0]).toEqual(
        expect.objectContaining({
          claimId: CLAIM_ID,
          bucket: 'vehicle_1',
          monthlyPayment: 415,
          cureTotal: 900,
          creditorName: 'Example Auto Finance',
          propertyDescription: '2016 sedan',
        }),
      );
      const provenance = sent.provenance as Record<string, { source: string }>;
      const rowId = String(rows[0]!.id);
      expect(provenance[`otherSecuredPayments[${rowId}].bucket`]).toEqual({
        source: 'staff_typed',
      });
      expect(provenance[`otherSecuredPayments[${rowId}].claimId`]).toEqual({
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

    expect(await screen.findByDisplayValue('415')).toBeTruthy();
    await user.clear(screen.getByLabelText('Average monthly payment over the next 60 months'));
    await user.type(
      screen.getByLabelText('Average monthly payment over the next 60 months'),
      '450.00',
    );
    await user.press(screen.getByRole('button', { name: 'Save means-test payment' }));

    await waitFor(() => {
      const sent = lastBody(fetchMock, 'PUT', `/means_test_inputs/${INPUT_ID}`);
      const rows = sent.otherSecuredPayments as Record<string, unknown>[];
      expect(rows).toHaveLength(1);
      expect(rows[0]).toEqual(expect.objectContaining({ id: 'row-1', monthlyPayment: 450 }));
      // The rest of the record rides along untouched.
      expect(sent.taxes).toBe(1620);
    });
  });

  it('is absent under a claim that is not secured', async () => {
    // Before `common`: the first matching route wins.
    signedIn([
      get(`/v1/cases/${CASE_ID}/claims`, {
        claims: [
          {
            ...SECURED_CLAIM,
            body: { ...SECURED_CLAIM.body, claimClass: 'nonpriority_unsecured' },
          },
        ],
      }),
      ...common,
      get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [] }),
    ]);
    await openClaim();

    expect(await screen.findByText('Save changes')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Means test — this claim’s payment' })).toBeNull();
  });
});
