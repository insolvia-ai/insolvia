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
const PLAN_ID = '00000000-0000-4000-8000-0000000000f1';
const STAMP = '2026-09-01T10:00:00.000000Z';

function row(key: string, label: string, over: Record<string, unknown> = {}) {
  return {
    key,
    label,
    claimId: null,
    treatment: null,
    allowed: '3000.00',
    monthlyPayment: null,
    rate: null,
    principal: '3000.00',
    interest: '0.00',
    payout: '3000.00',
    firstMonth: 1,
    lastMonth: 11,
    monthsPaid: 11,
    unpaid: '0.00',
    source: 'plan.attorney_fees (§ 503(b), § 507(a)(2))',
    ...over,
  };
}

function klass(key: string, label: string, rows: readonly Record<string, unknown>[]) {
  return {
    key,
    label,
    allowed: '3000.00',
    principal: '3000.00',
    interest: '0.00',
    payout: '3000.00',
    unpaid: '0.00',
    firstMonth: 1,
    lastMonth: 11,
    rows,
  };
}

/** `plan_calculation_json` for a feasible plan that clears the floor. */
const FEASIBLE = {
  planPresent: true,
  chapter: 13,
  commitmentPeriod: { months: 60, source: 'line 20b is more than or equal to line 20c' },
  funding: {
    termMonths: 60,
    termSource: 'plan.term_months',
    basePayment: '600.00',
    baseSource: 'plan.monthly_payment',
    schedule: [],
    lumpSums: [],
    total: '36000.00',
  },
  trusteePercentage: '10.00',
  classes: [
    klass('attorney_fees', "Attorney's fees", [row('attorney', "Attorney's fees")]),
    klass('secured', 'Secured claims and arrears', [
      row('secured:claim-car', 'Example Auto Finance — secured value', {
        claimId: 'claim-car',
        treatment: 'cramdown',
        source:
          'plan.secured_treatments[t-car].cramdown_value (§ 506(a), § 1325(a)(5)(B)), 7.00% a year; amortised over 60 months',
      }),
    ]),
  ],
  unsecured: {
    pool: [],
    poolTotal: '24000.00',
    target: '24000.00',
    targetSource: 'whatever remains, up to the full unsecured pool (a pot plan)',
    percentage: '47.50',
  },
  feasibility: {
    feasible: true,
    totalFunding: '36000.00',
    totalDistributed: '36000.00',
    surplus: '0.00',
    shortfall: '0.00',
    reasons: [],
    scheduleJExcess: { amount: '700.00', source: 'Schedule J line 23c' },
    exceedsScheduleJ: false,
  },
  liquidation: {
    assets: [
      {
        assetId: 'asset-savings',
        description: 'Savings account',
        value: '10000.00',
        liens: '0.00',
        exempt: '2000.00',
        unexempt: '8000.00',
      },
    ],
    propertyTotal: '10000.00',
    liensTotal: '0.00',
    exemptionsTotal: '2000.00',
    unexemptTotal: '8000.00',
    trusteeCommission: '1550.00',
    trusteeCommissionRule: '11 U.S.C. § 326(a)',
    otherCosts: '0.00',
    otherCostsSource: 'none typed',
    priorityTotal: '3000.00',
    available: '3450.00',
    pool: [],
    poolTotal: '24000.00',
    percentage: '14.38',
  },
  bestInterests: {
    planPercentage: '47.50',
    liquidationPercentage: '14.38',
    planUnsecuredValue: '11400.00',
    presentValueRate: null,
    passes: true,
    rule: '11 U.S.C. § 1325(a)(4)',
  },
  warnings: [],
  problems: [],
};

/** The same case with no plan stored yet. */
const NO_PLAN = {
  ...FEASIBLE,
  planPresent: false,
  funding: { ...FEASIBLE.funding, basePayment: null, baseSource: null, total: '0.00' },
  classes: [],
  unsecured: { ...FEASIBLE.unsecured, percentage: null },
  feasibility: { ...FEASIBLE.feasibility, feasible: null },
  bestInterests: { ...FEASIBLE.bestInterests, passes: null, planPercentage: null },
  problems: ['There is no plan yet: choose a monthly payment.'],
};

const SHORT = {
  ...FEASIBLE,
  funding: { ...FEASIBLE.funding, basePayment: '300.00', total: '18000.00' },
  unsecured: { ...FEASIBLE.unsecured, percentage: '0.00' },
  feasibility: { ...FEASIBLE.feasibility, feasible: false },
  bestInterests: { ...FEASIBLE.bestInterests, passes: false },
};

const STORED_PLAN = {
  id: PLAN_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  amended: false,
  provenance: {
    monthly_payment: { source: 'staff_typed' },
    'secured_treatments[t-car].claim_id': { source: 'staff_typed' },
    'secured_treatments[t-car].treatment': { source: 'staff_typed' },
  },
  monthly_payment: '600.00',
  secured_treatments: [{ id: 't-car', claim_id: 'claim-car', treatment: 'cramdown' }],
};

const CLAIM = {
  id: 'claim-car',
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: {},
  amended: false,
  creditor_id: 'cr-auto',
  claim_class: 'secured',
  amount: '12000.00',
  account_last4: '8890',
};

const CREDITOR = {
  id: 'cr-auto',
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: {},
  amended: false,
  name: 'Example Auto Finance',
};

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * `/cases/<id>/plan` (issue #366), through the real route. What is asserted:
 * every figure and verdict is the SERVER's, a save writes the whole plan
 * record with staff_typed provenance and re-reads the calculation, and an
 * alternative is calculated through the scenario POST without writing the
 * plan.
 */
describe('the plan screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[], chapter = 13) {
    const fetchMock = jest.fn((url: string, init?: RequestInit) => {
      if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
      const method = init?.method ?? 'GET';
      const match = routes.find((route) => route.method === method && url.includes(route.fragment));
      if (match === undefined) {
        if (method === 'GET' && url.endsWith(`/v1/cases/${CASE_ID}`)) {
          return Promise.resolve(jsonResponse(200, caseBody(CASE_ID, { chapter })));
        }
        return Promise.reject(new Error(`unexpected ${method} ${url}`));
      }
      return Promise.resolve(match.respond());
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/plan` });
    return fetchMock;
  }

  const get = (fragment: string, body: unknown): Route => ({
    method: 'GET',
    fragment,
    respond: () => jsonResponse(200, body),
  });

  function baseRoutes(over: readonly Route[] = []): readonly Route[] {
    return [
      ...over,
      get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
      get(`/v1/cases/${CASE_ID}/plan-calculation`, NO_PLAN),
      get(`/v1/cases/${CASE_ID}/plans`, { plans: [] }),
      get(`/v1/cases/${CASE_ID}/claims`, { claims: [CLAIM] }),
      get(`/v1/cases/${CASE_ID}/creditors`, { creditors: [CREDITOR] }),
    ];
  }

  function bodiesSent(fetchMock: jest.Mock, method: string, fragment: string) {
    return fetchMock.mock.calls
      .filter(
        ([url, init]: [string, RequestInit | undefined]) =>
          url.includes(fragment) && (init?.method ?? 'GET') === method,
      )
      .map(
        ([, init]: [string, RequestInit | undefined]) =>
          JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>,
      );
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

  it('gives the page one level-1 heading and lists Plan in a Chapter 13 case strip', async () => {
    signedIn(baseRoutes());

    expect(await screen.findByRole('heading', { name: 'Chapter 13 plan' })).toBeTruthy();
    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
    expect(screen.getByLabelText('Plan')).toBeTruthy();
  });

  it('leaves Plan out of a Chapter 7 case strip and says why on the page', async () => {
    signedIn(baseRoutes(), 7);

    expect(await screen.findByText(/a plan is proposed only under Chapter 13/u)).toBeTruthy();
    expect(screen.queryByLabelText('Plan')).toBeNull();
  });

  it('says what is missing rather than showing a verdict when there is no plan', async () => {
    signedIn(baseRoutes());

    expect(await screen.findByText('There is no plan yet: choose a monthly payment.')).toBeTruthy();
    expect(screen.getAllByText('Not yet determined')).toHaveLength(2);
    // The liquidation floor does not wait for a plan.
    expect(screen.getByText('14.38%')).toBeTruthy();
  });

  it('renders the waterfall and both tests from the calculation', async () => {
    signedIn(
      baseRoutes([
        get(`/v1/cases/${CASE_ID}/plan-calculation`, FEASIBLE),
        get(`/v1/cases/${CASE_ID}/plans`, { plans: [STORED_PLAN] }),
      ]),
    );

    expect(await screen.findByText('Feasible')).toBeTruthy();
    expect(screen.getByText('Meets the liquidation floor')).toBeTruthy();
    expect(screen.getByText('47.50%')).toBeTruthy();
    expect(screen.getByText('Example Auto Finance — secured value')).toBeTruthy();
    // Every row says where its figure came from, verbatim from the server.
    expect(screen.getByText(/plan\.secured_treatments\[t-car\]\.cramdown_value/u)).toBeTruthy();
    expect(screen.getByText('Savings account')).toBeTruthy();
  });

  it('saves the plan whole with staff_typed provenance, then recalculates', async () => {
    const user = userEvent.setup();
    let reads = 0;
    const fetchMock = signedIn(
      baseRoutes([
        {
          method: 'GET',
          fragment: `/v1/cases/${CASE_ID}/plan-calculation`,
          respond: () => {
            reads += 1;
            return jsonResponse(200, reads > 1 ? FEASIBLE : NO_PLAN);
          },
        },
        get(`/v1/cases/${CASE_ID}/plans`, { plans: [STORED_PLAN] }),
        {
          method: 'PUT',
          fragment: `/v1/cases/${CASE_ID}/plans/${PLAN_ID}`,
          respond: () =>
            jsonResponse(200, {
              ...STORED_PLAN,
              secured_treatments: [{ ...STORED_PLAN.secured_treatments[0], interest_rate: '7.00' }],
            }),
        },
      ]),
    );
    const rate = await screen.findByLabelText(
      'Example Auto Finance (…8890): Interest rate (% a year)',
    );

    await user.type(rate, '7');
    await user.press(screen.getAllByRole('button', { name: 'Save and recalculate' })[0]!);

    await waitFor(() => {
      const [sent] = bodiesSent(fetchMock, 'PUT', '/plans/');
      expect(sent?.secured_treatments).toEqual([
        { id: 't-car', claim_id: 'claim-car', treatment: 'cramdown', interest_rate: '7' },
      ]);
      const provenance = sent?.provenance as Record<string, { source: string }>;
      expect(provenance['secured_treatments[t-car].interest_rate']).toEqual({
        source: 'staff_typed',
      });
    });
    expect(await screen.findByText('Feasible')).toBeTruthy();
    expect(screen.getByText('Saved')).toBeTruthy();
  });

  it('compares an alternative without writing the plan', async () => {
    const user = userEvent.setup();
    const fetchMock = signedIn(
      baseRoutes([
        get(`/v1/cases/${CASE_ID}/plan-calculation`, FEASIBLE),
        get(`/v1/cases/${CASE_ID}/plans`, { plans: [STORED_PLAN] }),
        {
          method: 'POST',
          fragment: `/v1/cases/${CASE_ID}/plan-calculation`,
          respond: () =>
            jsonResponse(200, { scenarios: [{ label: 'Alternative', calculation: SHORT }] }),
        },
      ]),
    );
    await screen.findByText('Feasible');

    await user.type(screen.getByLabelText('Alternative monthly payment'), '300');
    await user.press(screen.getByRole('button', { name: 'Compare' }));

    expect(await screen.findByText('Not feasible')).toBeTruthy();
    expect(screen.getByText('Below the liquidation floor')).toBeTruthy();
    const [sent] = bodiesSent(fetchMock, 'POST', '/plan-calculation');
    expect(sent).toEqual({
      scenarios: [
        {
          label: 'Alternative',
          plan: expect.objectContaining({ payment_source: 'fixed', monthly_payment: '300' }),
        },
      ],
    });
    expect(bodiesSent(fetchMock, 'PUT', '/plans/')).toHaveLength(0);
    expect(bodiesSent(fetchMock, 'POST', '/plans')).toHaveLength(0);
  });
});
