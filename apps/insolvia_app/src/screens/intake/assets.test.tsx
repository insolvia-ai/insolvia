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
const STAMP = '2026-09-01T10:00:00.000000Z';

const ZERO_TOTALS = {
  realEstate: '0',
  personalProperty: '0',
  assets: '0',
  totalExempt: '0',
  totalNonExempt: '0',
  secured: '0',
  priorityUnsecured: '0',
  nonpriorityUnsecured: '0',
  liabilities: '0',
  monthlyIncome: '0',
  monthlyExpenses: '0',
  monthlyExcess: '0',
};

function summaryBody(totals: Readonly<Record<string, string>> = {}) {
  return {
    readyToFile: false,
    problems: [],
    totals: { ...ZERO_TOTALS, ...totals },
    liens: { claims: [], assets: [] },
  };
}

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The category-driven Schedule A/B screen (issue 13.3 / #344), through the
 * real intake route — the same harness `liens.test.tsx` uses for the
 * property section's collateral panel, which this screen still renders.
 */
describe('the assets screen', () => {
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

  const common = (assets: readonly unknown[] = []) => [
    get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
    get(`/v1/cases/${CASE_ID}/assets`, { assets }),
    get(`/v1/cases/${CASE_ID}/summary`, summaryBody()),
  ];

  async function openProperty() {
    const user = userEvent.setup();
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    await user.press(await screen.findByRole('option', { name: 'Property' }));
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

  it('shows the four totals from the summary endpoint', async () => {
    signedIn([
      get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
      get(`/v1/cases/${CASE_ID}/assets`, { assets: [] }),
      get(
        `/v1/cases/${CASE_ID}/summary`,
        summaryBody({
          assets: '312550.00',
          secured: '9000.00',
          totalExempt: '6000.00',
          totalNonExempt: '306550.00',
        }),
      ),
    ]);

    await openProperty();

    expect(await screen.findByText('$312,550.00')).toBeTruthy();
    expect(screen.getByText('$9,000.00')).toBeTruthy();
    expect(screen.getByText('$6,000.00')).toBeTruthy();
    expect(screen.getByText('$306,550.00')).toBeTruthy();
  });

  it('changes the field set when the category changes', async () => {
    const user = signedIn(common()) && (await openProperty());
    await user.press(await screen.findByRole('button', { name: 'Add asset' }));

    await user.press(await screen.findByRole('combobox', { name: 'Category' }));
    await user.press(await screen.findByRole('option', { name: /Real property/u }));

    expect(await screen.findByLabelText('County')).toBeTruthy();
    expect(screen.getByText('What is the property?')).toBeTruthy();
    expect(screen.getByLabelText('Single family home')).toBeTruthy();
    expect(screen.queryByLabelText('Make (required)')).toBeNull();

    await user.press(screen.getByRole('combobox', { name: 'Category' }));
    await user.press(await screen.findByRole('option', { name: /Vehicles — Vehicle/u }));

    expect(await screen.findByLabelText('Make (required)')).toBeTruthy();
    expect(screen.getByLabelText('Model (required)')).toBeTruthy();
    expect(screen.getByLabelText('Mileage')).toBeTruthy();
    expect(screen.queryByLabelText('County')).toBeNull();
  });

  it('blocks an empty save with a message naming the missing field', async () => {
    const fetchMock = signedIn(common());
    const user = await openProperty();
    await user.press(await screen.findByRole('button', { name: 'Add asset' }));

    await user.press(await screen.findByRole('button', { name: 'Save asset' }));

    expect(await screen.findByText('Choose a category before saving.')).toBeTruthy();
    expect(
      fetchMock.mock.calls.some(
        ([url, init]) =>
          String(url).endsWith('/assets') && (init as RequestInit | undefined)?.method === 'POST',
      ),
    ).toBe(false);

    await user.press(screen.getByRole('combobox', { name: 'Category' }));
    await user.press(await screen.findByRole('option', { name: /Real property/u }));
    await user.press(screen.getByRole('button', { name: 'Save asset' }));

    expect(
      await screen.findByText('Enter street address (or other description) before saving.'),
    ).toBeTruthy();
  });

  it('saves a vehicle with its structured fields and staff_typed provenance', async () => {
    const fetchMock = signedIn([
      ...common(),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/assets`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-00000000a5e2',
            case_id: CASE_ID,
            created_at: STAMP,
            updated_at: STAMP,
            provenance: {},
            category: 'vehicle',
            make: 'Honda',
            model: 'Civic LX',
          }),
      },
    ]);

    const user = await openProperty();
    await user.press(await screen.findByRole('button', { name: 'Add asset' }));
    await user.press(await screen.findByRole('combobox', { name: 'Category' }));
    await user.press(await screen.findByRole('option', { name: /Vehicles — Vehicle/u }));
    await user.type(await screen.findByLabelText('Make (required)'), 'Honda');
    await user.type(screen.getByLabelText('Model (required)'), 'Civic LX');
    await user.press(screen.getByRole('button', { name: 'Save asset' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/assets')).toEqual({
        category: 'vehicle',
        make: 'Honda',
        model: 'Civic LX',
        provenance: {
          category: { source: 'staff_typed' },
          make: { source: 'staff_typed' },
          model: { source: 'staff_typed' },
        },
      }),
    );
    expect(await screen.findByText('Saved')).toBeTruthy();
  });
});
