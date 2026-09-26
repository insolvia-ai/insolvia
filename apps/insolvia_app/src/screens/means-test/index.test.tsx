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
const INPUT_ID = '00000000-0000-4000-8000-0000000000e1';
const STAMP = '2026-09-01T10:00:00.000000Z';

/** `means_test_json` for a bare case: the engine refused, the window landed. */
const UNDETERMINED = {
  asOf: '2026-08-01',
  asOfSource: 'case.created_at',
  chapter: 7,
  releaseIds: {},
  jurisdiction: { state: null, county: null, district: 'NDCA' },
  maritalFilingStatus: {
    value: 'not_married',
    source: "the case's debtor records (no spouse recorded)",
  },
  household: {
    peopleUnder65: null,
    people65OrOlder: null,
    medianHouseholdSize: { value: null, source: null },
    irsFamilySize: { value: null, source: null },
    irsHousingFamilySize: { value: null, source: null },
    childrenUnder18: 0,
  },
  exemptions: {
    nonConsumerDebts: false,
    disabledVeteran: false,
    reservistNationalGuard: false,
    applied: null,
    rule: null,
    available: ['non_consumer_debts', 'disabled_veteran', 'reservist_national_guard'],
  },
  cmi: {
    window: {
      filingDate: '2026-08-01',
      start: '2026-02-01',
      end: '2026-07-31',
      months: ['2026-02', '2026-03', '2026-04', '2026-05', '2026-06', '2026-07'],
    },
    columns: [],
    combinedMonthlyTotal: '0.00',
    annualized: '0.00',
    gaps: [],
    problems: [],
  },
  debt: { priorityTotal: '0.00', nonpriorityUnsecuredTotal: '0.00' },
  comparison: null,
  outcome: 'undetermined',
  determinedBy: null,
  lines: [],
  chapter13: null,
  problems: ['the household composition (people under 65 / 65 and older) has not been entered'],
};

/** An above-median debtor whose deductions leave no presumption. */
const ABOVE_MEDIAN = {
  ...UNDETERMINED,
  releaseIds: { 'ust/census-median-family-income': 'ust/census-median-family-income@2026-04-01' },
  household: {
    ...UNDETERMINED.household,
    peopleUnder65: 3,
    people65OrOlder: 0,
    medianHouseholdSize: {
      value: 3,
      source: 'entered (means_test_input.people_under_65 + people_65_or_older)',
    },
  },
  cmi: {
    ...UNDETERMINED.cmi,
    columns: [
      {
        column: 'A',
        lines: [
          {
            category: 'wages',
            label: 'Gross wages, salary, tips, bonuses, overtime, commissions',
            totalReceived: '44400.00',
            monthlyAverage: '7400.00',
            citation: '',
            note: '',
            entries: [],
          },
        ],
        excluded: [],
        monthlyTotal: '7400.00',
      },
    ],
    combinedMonthlyTotal: '8700.00',
    annualized: '104400.00',
  },
  comparison: {
    state: 'FL',
    householdSize: 3,
    monthlyCmi: '8700.00',
    annualizedCmi: '104400.00',
    annualMedian: '97540.00',
    aboveMedian: true,
    source:
      'Census median family income, FL household of 3 — ust/census-median-family-income@2026-04-01',
  },
  outcome: 'no_presumption',
  determinedBy: 'threshold_floor',
  lines: [
    {
      form: '122A-2',
      line: '6',
      label: 'Food, clothing, and other items',
      amount: '1857.00',
      source: 'IRS National Standards, household of 3 — ust/irs-national-standards@2026-07-15',
    },
    {
      form: '122A-2',
      line: '39d',
      label: 'Total over 60 months',
      amount: '-23359.80',
      source: 'line 39c x 60',
    },
  ],
  problems: [],
};

/** The same family under Chapter 13 (issue #365): 60 months, Form 122C-2 required. */
const CHAPTER_13 = {
  ...ABOVE_MEDIAN,
  chapter: 13,
  outcome: 'above_median',
  determinedBy: 'median',
  lines: [
    {
      form: '122C-1',
      line: '21',
      label: 'Applicable commitment period (months)',
      amount: '60.00',
      source:
        'line 20b is more than or equal to line 20c: the commitment period is 5 years — 11 U.S.C. § 1325(b)(4)(A)(ii)',
    },
    {
      form: '122C-2',
      line: '45',
      label: 'Monthly disposable income under 11 U.S.C. § 1325(b)(2)',
      amount: '-1079.33',
      source: 'line 39 minus line 44',
    },
  ],
  chapter13: {
    commitmentPeriodMonths: 60,
    commitmentSource:
      'line 20b is more than or equal to line 20c: the commitment period is 5 years — 11 U.S.C. § 1325(b)(4)(A)(ii)',
    commitmentMaritalAdjustment: '0.00',
    commitmentMonthlyIncome: '8700.00',
    commitmentAnnualizedIncome: '104400.00',
    commitmentAnnualMedian: '97540.00',
    disposableIncomeRequired: true,
    monthlyDisposableIncome: '-1079.33',
  },
};

const SAVED_INPUT = {
  id: INPUT_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: { taxes: { source: 'staff_typed' } },
  taxes: '1620.00',
};

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * `/cases/<id>/means-test` (issue #349), through the real route. What is
 * asserted: the verdict and every figure on the page are the SERVER's, a
 * save writes the whole input record with staff_typed provenance and then
 * re-reads the trace, the expected filing date is shown from the petition
 * with a link rather than a second field, and an undetermined test says
 * why.
 */
describe('the means-test screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(routes: readonly Route[], chapter = 7) {
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
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/means-test` });
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

  function baseRoutes(over: readonly Route[] = []): readonly Route[] {
    const defaults: readonly Route[] = [
      get(`/v1/cases/${CASE_ID}/debtors`, { debtors: [] }),
      get(`/v1/cases/${CASE_ID}/means-test`, UNDETERMINED),
      get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [] }),
      get(`/v1/cases/${CASE_ID}/petitions`, { petitions: [] }),
      get(`/v1/cases/${CASE_ID}/dependents`, { dependents: [] }),
      get(`/v1/cases/${CASE_ID}/households`, { households: [] }),
    ];
    // The first matching fragment wins, so overrides go first.
    return [...over, ...defaults];
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

  it('gives the page one level-1 heading and lists the section in the case strip', async () => {
    signedIn(baseRoutes());

    expect(await screen.findByRole('heading', { name: 'Means test' })).toBeTruthy();
    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
    expect(screen.getByLabelText('Means test')).toBeTruthy();
  });

  it('says why the test is undetermined rather than showing a false verdict', async () => {
    signedIn(baseRoutes());

    expect(await screen.findByText('Not yet determined')).toBeTruthy();
    expect(screen.getByText('Comparison not yet made')).toBeTruthy();
    expect(
      screen.getAllByText(/household composition .* has not been entered/u).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText(/opening date stands in for it/u)).toBeTruthy();
  });

  it('renders the verdict, the comparison and the 122A-2 lines from the trace', async () => {
    signedIn(baseRoutes([get(`/v1/cases/${CASE_ID}/means-test`, ABOVE_MEDIAN)]));

    expect(await screen.findByText('No presumption of abuse')).toBeTruthy();
    expect(screen.getByText('$8700.00')).toBeTruthy();
    expect(screen.getByText('$97540.00')).toBeTruthy();
    expect(screen.getByText('Above the median')).toBeTruthy();
    // A 122A-2 line with its amount and its dataset source, verbatim.
    expect(screen.getByText('Food, clothing, and other items')).toBeTruthy();
    expect(screen.getByText('$1857.00')).toBeTruthy();
    expect(screen.getByText(/ust\/irs-national-standards@2026-07-15/u)).toBeTruthy();
    // The income grid shows what the records derived for Column A's wages.
    expect(screen.getByText('From the records: $7400.00')).toBeTruthy();
  });

  it('shows the Chapter 13 verdict — commitment period and disposable income — on a Chapter 13 case', async () => {
    signedIn(baseRoutes([get(`/v1/cases/${CASE_ID}/means-test`, CHAPTER_13)]), 13);

    expect(await screen.findByText('60 months')).toBeTruthy();
    // The figure prints in the banner and again on the trace's line 45 —
    // the same server string in both places.
    expect(screen.getAllByText('$-1079.33')).toHaveLength(2);
    expect(screen.getByText('Determined by the standards — Form 122C-2 is required')).toBeTruthy();
    expect(screen.getByText('Disposable income under § 1325(b)(3)')).toBeTruthy();
    // The trace table names the form each line prints on.
    expect(screen.getByText('122C-1')).toBeTruthy();
    expect(screen.getByText('Monthly disposable income under 11 U.S.C. § 1325(b)(2)')).toBeTruthy();
    // The Chapter 13 questions are on the page; B122A-2's eligibility box is not.
    expect(screen.getByLabelText('Line 40 — support income for dependent children')).toBeTruthy();
    expect(screen.queryByLabelText('Line 36 — eligible to file under Chapter 13?')).toBeNull();
  });

  it('asks no Chapter 13 question on a Chapter 7 case', async () => {
    signedIn(baseRoutes([get(`/v1/cases/${CASE_ID}/means-test`, ABOVE_MEDIAN)]));

    expect(await screen.findByText('No presumption of abuse')).toBeTruthy();
    expect(screen.queryByLabelText('Line 40 — support income for dependent children')).toBeNull();
    expect(screen.queryByText('Commitment period (§ 1325(b)(4))')).toBeNull();
    expect(screen.getByLabelText('Line 36 — eligible to file under Chapter 13?')).toBeTruthy();
  });

  it('saves the Chapter 13 answers on the same record and re-reads the trace', async () => {
    const user = userEvent.setup();
    const fetchMock = signedIn(
      baseRoutes([
        get(`/v1/cases/${CASE_ID}/means-test`, CHAPTER_13),
        {
          method: 'POST',
          fragment: `/v1/cases/${CASE_ID}/means_test_inputs`,
          respond: () =>
            jsonResponse(201, {
              ...SAVED_INPUT,
              child_support_for_dependents: '300.00',
              qualified_retirement_deductions: '210.00',
              special_circumstances: [
                { id: 'row', description: 'Dialysis travel', amount: '180.00' },
              ],
            }),
        },
      ]),
      13,
    );
    await screen.findByText('60 months');

    await user.type(
      screen.getByLabelText('Line 40 — support income for dependent children'),
      '300.00',
    );
    await user.type(screen.getByLabelText('Line 41 — qualified retirement deductions'), '210.00');
    await user.press(screen.getByRole('button', { name: 'Add a special circumstance' }));
    await user.type(screen.getByLabelText('Special circumstance'), 'Dialysis travel');
    await user.type(screen.getByLabelText('Monthly expense'), '180.00');
    await user.press(screen.getAllByRole('button', { name: 'Save and recompute' })[0]!);

    await waitFor(() => {
      const sent = lastBody(fetchMock, 'POST', '/means_test_inputs');
      expect(sent.child_support_for_dependents).toBe('300.00');
      expect(sent.qualified_retirement_deductions).toBe('210.00');
      expect(sent.special_circumstances).toEqual([
        expect.objectContaining({ description: 'Dialysis travel', amount: '180.00' }),
      ]);
      const provenance = sent.provenance as Record<string, { source: string }>;
      expect(provenance.child_support_for_dependents).toEqual({ source: 'staff_typed' });
      const rowId = (sent.special_circumstances as { id: string }[])[0]!.id;
      expect(provenance[`special_circumstances[${rowId}].amount`]).toEqual({
        source: 'staff_typed',
      });
    });
    expect(await screen.findByText('Saved')).toBeTruthy();
  });

  it('shows the expected filing date from the petition, with a link to change it there', async () => {
    signedIn(
      baseRoutes([
        get(`/v1/cases/${CASE_ID}/petitions`, {
          petitions: [
            {
              id: 'p-1',
              case_id: CASE_ID,
              created_at: STAMP,
              updated_at: STAMP,
              provenance: { expected_filing_date: { source: 'staff_typed' } },
              expected_filing_date: '2026-10-15',
            },
          ],
        }),
      ]),
    );

    expect(await screen.findByText(/Planned for October 15, 2026/u)).toBeTruthy();
    expect(
      screen.getByRole('link', { name: 'Change the expected filing date on the petition' }),
    ).toBeTruthy();
  });

  it('saves the inputs whole with staff_typed provenance, then re-reads the trace', async () => {
    const user = userEvent.setup();
    let traceReads = 0;
    const fetchMock = signedIn(
      baseRoutes([
        {
          method: 'GET',
          fragment: `/v1/cases/${CASE_ID}/means-test`,
          respond: () => {
            traceReads += 1;
            return jsonResponse(200, traceReads > 1 ? ABOVE_MEDIAN : UNDETERMINED);
          },
        },
        {
          method: 'POST',
          fragment: `/v1/cases/${CASE_ID}/means_test_inputs`,
          respond: () =>
            jsonResponse(201, {
              ...SAVED_INPUT,
              people_under_65: 3,
              people_65_or_older: 0,
              reservist_national_guard: true,
              income_overrides: [
                { id: 'row', column: 'A', category: 'wages', monthly_amount: '8000.00' },
              ],
            }),
        },
      ]),
    );
    await screen.findByText('Not yet determined');

    await user.type(screen.getByLabelText('People under 65'), '3');
    await user.type(screen.getByLabelText('People 65 or older'), '0');
    await user.press(
      screen.getByRole('checkbox', {
        name: /Reservist or National Guard member/u,
      }),
    );
    await user.type(screen.getByLabelText(/Column A — Debtor 1: Line 2 — Gross wages/u), '8000.00');
    await user.press(screen.getAllByRole('button', { name: 'Save and recompute' })[0]!);

    await waitFor(() => {
      const sent = lastBody(fetchMock, 'POST', '/means_test_inputs');
      expect(sent.people_under_65).toBe(3);
      expect(sent.people_65_or_older).toBe(0);
      expect(sent.reservist_national_guard).toBe(true);
      expect(sent.income_overrides).toEqual([
        expect.objectContaining({ column: 'A', category: 'wages', monthly_amount: '8000.00' }),
      ]);
      const provenance = sent.provenance as Record<string, { source: string }>;
      expect(provenance.people_under_65).toEqual({ source: 'staff_typed' });
      expect(provenance.reservist_national_guard).toEqual({ source: 'staff_typed' });
      const overrideId = (sent.income_overrides as { id: string }[])[0]!.id;
      expect(provenance[`income_overrides[${overrideId}].monthly_amount`]).toEqual({
        source: 'staff_typed',
      });
    });
    // The verdict is the server's re-read, not a local recomputation.
    expect(await screen.findByText('No presumption of abuse')).toBeTruthy();
    expect(traceReads).toBe(2);
  });

  it('puts the record back when one already exists', async () => {
    const user = userEvent.setup();
    const fetchMock = signedIn(
      baseRoutes([
        get(`/v1/cases/${CASE_ID}/means_test_inputs`, { means_test_inputs: [SAVED_INPUT] }),
        {
          method: 'PUT',
          fragment: `/v1/cases/${CASE_ID}/means_test_inputs/${INPUT_ID}`,
          respond: () => jsonResponse(200, { ...SAVED_INPUT, taxes: '1700.00' }),
        },
      ]),
    );
    await screen.findByDisplayValue('1620.00');

    await user.clear(screen.getByLabelText('Line 16 — taxes'));
    await user.type(screen.getByLabelText('Line 16 — taxes'), '1700.00');
    await user.press(screen.getAllByRole('button', { name: 'Save and recompute' })[0]!);

    await waitFor(() => {
      expect(lastBody(fetchMock, 'PUT', `/means_test_inputs/${INPUT_ID}`).taxes).toBe('1700.00');
    });
    expect(await screen.findByText('Saved')).toBeTruthy();
  });
});
