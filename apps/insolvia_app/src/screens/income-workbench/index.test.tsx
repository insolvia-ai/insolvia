import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  TEST_AUTH_CONFIG,
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  tokenEndpointResponse,
  withCaseShell,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const CASE_ID = '00000000-0000-4000-8000-0000000000c1';

const EMPTY_LISTS: Readonly<Record<string, () => Response>> = {
  [`/v1/cases/${CASE_ID}/employments`]: () => jsonResponse(200, { employments: [] }),
  [`/v1/cases/${CASE_ID}/pay_period_records`]: () => jsonResponse(200, { pay_period_records: [] }),
  [`/v1/cases/${CASE_ID}/other_income_records`]: () =>
    jsonResponse(200, { other_income_records: [] }),
  [`/v1/cases/${CASE_ID}/income_summaries`]: () => jsonResponse(200, { income_summaries: [] }),
  [`/v1/cases/${CASE_ID}/households`]: () => jsonResponse(200, { households: [] }),
  [`/v1/cases/${CASE_ID}/expenses`]: () => jsonResponse(200, { expenses: [] }),
  [`/v1/cases/${CASE_ID}/dependents`]: () => jsonResponse(200, { dependents: [] }),
};

const ZERO_TOTALS = {
  realEstate: '0',
  personalProperty: '0',
  assets: '0',
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
    // Every summary body carries this block (issue #345); the income
    // workbench itself never reads it, but the decoder requires it.
    liens: { claims: [], assets: [] },
  };
}

const NO_JURISDICTION_STANDARDS = {
  asOf: '2026-09-04',
  state: null,
  county: null,
  householdSize: null,
  jurisdictionSource: null,
  nationalStandards: {
    releaseId: 'ust/irs-national-standards@2026-07-15',
    allowance: null,
    oopHealthcareUnder65: null,
    oopHealthcare65AndOlder: null,
  },
  localStandards: {
    releaseId: 'ust/irs-local-standards@2026-07-15',
    housingNonMortgage: null,
    housingMortgageRent: null,
    transportationPublicNational: null,
    transportationOwnershipOneCar: null,
    transportationOwnershipTwoCars: null,
    transportationOperatingOneCar: null,
    transportationOperatingTwoCars: null,
  },
  problems: ['the case has no Debtor 1 record yet'],
};

/**
 * `/cases/<id>/income` — the income-and-expenses workbench (issue #348).
 *
 * Rendered through the real router, like the other case screens' suites, so
 * the route file and the case layout are exercised along with the screen.
 * What is asserted: the excess banner reads `/summary` and never invents its
 * own arithmetic, the tab strip separates income from expenses, the raw
 * records reuse the same collection editors intake does, and the standards
 * panel degrades to its `problems` rather than showing a false zero.
 */
describe('the income workbench', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch(
      withCaseShell(CASE_ID, { '/oauth2/token': tokenEndpointResponse, ...handlers }),
    );
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/income` });
    return fetchMock;
  }

  function ready(over: Readonly<Record<string, () => Response>> = {}) {
    return signedIn({
      ...EMPTY_LISTS,
      [`/v1/cases/${CASE_ID}/summary`]: () => jsonResponse(200, summaryBody()),
      [`/v1/cases/${CASE_ID}/standards`]: () => jsonResponse(200, NO_JURISDICTION_STANDARDS),
      ...over,
    });
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

  it('gives the page one level-1 heading', async () => {
    ready();

    await screen.findByText('Income & expenses');
    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });

  it('shows the income, expenses and excess figures from /summary, never derived locally', async () => {
    ready({
      [`/v1/cases/${CASE_ID}/summary`]: () =>
        jsonResponse(
          200,
          summaryBody({
            monthlyIncome: '1000.00',
            monthlyExpenses: '1500.00',
            monthlyExcess: '-500.00',
          }),
        ),
    });

    expect(await screen.findByText('1000.00')).toBeTruthy();
    expect(screen.getByText('1500.00')).toBeTruthy();
    expect(screen.getByText('-500.00')).toBeTruthy();
  });

  it('opens on the Income tab, with the pay-record collection editor mounted', async () => {
    ready();

    await screen.findByText('Income & expenses');
    expect(await screen.findByText('Add pay period')).toBeTruthy();
  });

  it('switches to the Expenses tab and shows Schedule J grouped by category', async () => {
    const user = userEvent.setup();
    ready();
    await screen.findByText('Income & expenses');

    await user.press(screen.getByRole('tab', { name: 'Expenses' }));

    expect(await screen.findByText('Housing')).toBeTruthy();
    expect(screen.getByText('Health care')).toBeTruthy();
    expect(screen.getByText('Transportation')).toBeTruthy();
  });

  it('reports why the IRS Standards did not resolve rather than showing zero', async () => {
    const user = userEvent.setup();
    ready();
    await screen.findByText('Income & expenses');

    await user.press(screen.getByRole('tab', { name: 'Expenses' }));

    // Appears twice by design — beside the Transportation row it annotates,
    // and again in the standards panel's own problem list.
    expect(await screen.findAllByText('the case has no Debtor 1 record yet')).not.toHaveLength(0);
  });

  it('shows a per-employer average once pay records are on file', async () => {
    ready({
      [`/v1/cases/${CASE_ID}/employments`]: () =>
        jsonResponse(200, {
          employments: [
            {
              id: 'em-1',
              case_id: CASE_ID,
              created_at: '2026-08-05T10:00:00.000000Z',
              updated_at: '2026-08-05T10:00:00.000000Z',
              provenance: {},
              employer_name: 'Acme Staffing',
            },
          ],
        }),
      [`/v1/cases/${CASE_ID}/pay_period_records`]: () =>
        jsonResponse(200, {
          pay_period_records: [
            {
              id: 'pp-1',
              case_id: CASE_ID,
              created_at: '2026-08-05T10:00:00.000000Z',
              updated_at: '2026-08-05T10:00:00.000000Z',
              provenance: {},
              employment_id: 'em-1',
              pay_date: '2026-08-01',
              gross: '1000.00',
              net: '800.00',
            },
            {
              id: 'pp-2',
              case_id: CASE_ID,
              created_at: '2026-08-05T10:00:00.000000Z',
              updated_at: '2026-08-05T10:00:00.000000Z',
              provenance: {},
              employment_id: 'em-1',
              pay_date: '2026-09-01',
              gross: '1200.00',
              net: '900.00',
            },
          ],
        }),
    });

    // "Acme Staffing" legitimately appears twice — once as the employer
    // collection's own row, once as the summary table's row label — so this
    // waits for at least one rather than assuming uniqueness.
    await screen.findAllByText('Acme Staffing');
    expect(screen.getByText('$1100.00')).toBeTruthy();
    expect(screen.getByText('$850.00')).toBeTruthy();
  });
});
