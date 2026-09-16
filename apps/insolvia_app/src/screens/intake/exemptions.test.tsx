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
const HOUSE_ID = '00000000-0000-4000-8000-00000000a5e1';
const EXEMPTION_ID = '00000000-0000-4000-8000-0000000000e1';
const STAMP = '2026-09-01T10:00:00.000000Z';

const SAVED_HOUSE = {
  id: HOUSE_ID,
  case_id: CASE_ID,
  created_at: STAMP,
  updated_at: STAMP,
  provenance: { category: { source: 'staff_typed' }, description: { source: 'staff_typed' } },
  category: 'real_property',
  description: '12 Byron Court',
  value_entire: '300000.00',
};

/** The literal `analysis_json` for a Florida house behind a first mortgage,
 * with one personal-property claim already on it — the server's answer,
 * which is all the panel ever shows. */
const ANALYSIS = {
  asOf: '2026-12-01',
  asOfSource: 'expected_filing_date',
  election: {
    stored: null,
    effective: 'state_and_federal_nonbankruptcy',
    state: 'FL',
    optedOut: true,
    optOutCitation: 'Fla. Stat. § 222.20',
    options: [
      {
        value: 'state_and_federal_nonbankruptcy',
        schemeId: 'fl',
        name: 'Florida exemptions',
      },
    ],
  },
  entries: [
    {
      entryId: 'fl-homestead',
      category: 'homestead',
      description: 'Homestead: residence of the owner',
      citation: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02',
      unlimited: true,
      limit: null,
      perItemAmount: null,
      carryover: null,
      claimed: '0.00',
      available: null,
      notes: '',
    },
    {
      entryId: 'fl-personal-property',
      category: 'personal_property',
      description: 'Personal property of any kind',
      citation: 'Fla. Const. art. X, § 4(a)(2)',
      unlimited: false,
      limit: '1000.00',
      perItemAmount: null,
      carryover: null,
      claimed: '1000.00',
      available: '0.00',
      notes: '',
    },
  ],
  limits: [
    {
      limitId: 'us-homestead-1215-day-cap',
      citation: '11 U.S.C. § 522(p)',
      description: 'Cap on the homestead interest in property acquired within 1,215 days',
      amount: '214000.00',
    },
  ],
  lookbacks: {
    section522o: '2016-12-01',
    section522p: '2023-08-04',
    section522q: '2021-12-01',
    domicilePeriodStart: '2024-12-01',
  },
  assets: [
    {
      assetId: HOUSE_ID,
      description: '12 Byron Court',
      category: 'real_property',
      currentValue: '300000.00',
      liens: '250000.00',
      netEquity: '50000.00',
      claimed: '1000.00',
      unexempt: '49000.00',
      homesteadCap: null,
      capApplied: false,
      claims: [
        {
          exemptionId: EXEMPTION_ID,
          statuteCitation: 'Fla. Const. art. X, § 4(a)(2)',
          amount: '1000.00',
          claimsFullFmv: false,
          acquiredWithin1215Days: null,
          claimed: '1000.00',
          knownStatute: true,
        },
      ],
      suggestions: { 'fl-homestead': '49000.00', 'fl-personal-property': '0.00' },
    },
  ],
  warnings: [],
  problems: [],
};

/** The same house, Texas, before the debtor has elected a scheme. */
const UNDECIDED_TEXAS = {
  ...ANALYSIS,
  election: {
    stored: null,
    effective: null,
    state: 'TX',
    optedOut: false,
    optOutCitation: null,
    options: [
      { value: 'state_and_federal_nonbankruptcy', schemeId: 'tx', name: 'Texas exemptions' },
      { value: 'federal', schemeId: 'us-522d', name: 'Federal bankruptcy exemptions' },
    ],
  },
  entries: [],
  problems: [
    'Choose the exemption scheme: TX lets the debtor claim the state scheme or the federal § 522(d) list (106C line 1).',
  ],
};

interface Route {
  readonly method: string;
  readonly fragment: string;
  readonly respond: () => Response;
}

/**
 * The Schedule C workbench (issue #346), through the real intake route.
 * What is asserted is that every figure on screen is the SERVER's — nothing
 * here is computed — and that the two writes carry exactly what the API
 * expects: an election PATCHed to the case, and a claim POSTed with the
 * registry's citation verbatim and `staff_typed` provenance on every field.
 */
describe('the exemptions panel', () => {
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
    get(`/v1/cases/${CASE_ID}/assets`, { assets: [SAVED_HOUSE] }),
    // The liens panel beside this one reads these; nothing here asserts on them.
    get(`/v1/cases/${CASE_ID}/liens`, { claims: [], assets: [] }),
    get(`/v1/cases/${CASE_ID}/claims`, { claims: [] }),
    get(`/v1/cases/${CASE_ID}/codebtors`, { codebtors: [] }),
  ];

  async function openHouse() {
    const user = userEvent.setup();
    await user.press(await screen.findByRole('combobox', { name: 'Section' }));
    await user.press(await screen.findByRole('option', { name: 'Property' }));
    await user.press(await screen.findByRole('button', { name: 'Edit asset 1' }));
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

  it('shows the server’s equity, claimed and unexempt figures and the lookback dates', async () => {
    signedIn([...common, get(`/v1/cases/${CASE_ID}/exemption-analysis`, ANALYSIS)]);

    await openHouse();

    expect(await screen.findByText('$49,000.00')).toBeTruthy();
    expect(screen.getByText('$50,000.00')).toBeTruthy();
    expect(screen.getByText('$250,000.00')).toBeTruthy();
    // The claim already on it, as the server counted it.
    expect(screen.getByText('Fla. Const. art. X, § 4(a)(2) — $1,000.00')).toBeTruthy();
    // The § 522(p) window as a calendar date, from the expected filing date.
    expect(screen.getByText(/on or after August 4, 2023 \(1,215 days\)/u)).toBeTruthy();
    expect(screen.getByText(/on or after December 1, 2016 \(10 years\)/u)).toBeTruthy();
  });

  it('claims an exemption with the registry’s citation and staff_typed provenance', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/exemption-analysis`, ANALYSIS),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/exemptions`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-0000000000e2',
            case_id: CASE_ID,
            created_at: STAMP,
            updated_at: STAMP,
            provenance: {},
            asset_id: HOUSE_ID,
            statute_citation: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02',
            amount: '49000.00',
            claims_full_fmv: false,
            acquired_within_1215_days: false,
          }),
      },
    ]);

    const user = await openHouse();
    await user.press(await screen.findByRole('combobox', { name: 'Claim an exemption under' }));
    await user.press(
      await screen.findByRole('option', {
        name: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02 — no dollar limit',
      }),
    );
    // The amount defaulted to the server's suggestion for this statute.
    expect(screen.getByDisplayValue('49000.00')).toBeTruthy();
    await user.press(
      screen.getByRole('combobox', {
        name: /Was this homestead acquired within 1,215 days before filing/u,
      }),
    );
    await user.press(await screen.findByRole('option', { name: 'No' }));
    await user.press(screen.getByRole('button', { name: 'Claim exemption' }));

    await waitFor(() =>
      expect(lastBody(fetchMock, 'POST', '/exemptions')).toEqual({
        asset_id: HOUSE_ID,
        statute_citation: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02',
        claims_full_fmv: false,
        amount: '49000.00',
        acquired_within_1215_days: false,
        provenance: {
          asset_id: { source: 'staff_typed' },
          statute_citation: { source: 'staff_typed' },
          claims_full_fmv: { source: 'staff_typed' },
          amount: { source: 'staff_typed' },
          acquired_within_1215_days: { source: 'staff_typed' },
        },
      }),
    );
    expect(await screen.findByText('Claimed')).toBeTruthy();
  });

  it('a 100%-of-fair-market-value claim sends the flag and no amount', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/exemption-analysis`, ANALYSIS),
      {
        method: 'POST',
        fragment: `/v1/cases/${CASE_ID}/exemptions`,
        respond: () =>
          jsonResponse(201, {
            id: '00000000-0000-4000-8000-0000000000e2',
            case_id: CASE_ID,
            created_at: STAMP,
            updated_at: STAMP,
            provenance: {},
            asset_id: HOUSE_ID,
            statute_citation: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02',
            claims_full_fmv: true,
          }),
      },
    ]);

    const user = await openHouse();
    await user.press(await screen.findByRole('combobox', { name: 'Claim an exemption under' }));
    await user.press(
      await screen.findByRole('option', {
        name: 'Fla. Const. art. X, § 4(a)(1); Fla. Stat. §§ 222.01-222.02 — no dollar limit',
      }),
    );
    await user.press(
      screen.getByRole('checkbox', {
        name: 'Claim 100% of fair market value, up to the statutory limit',
      }),
    );
    await user.press(screen.getByRole('button', { name: 'Claim exemption' }));

    await waitFor(() => {
      const body = lastBody(fetchMock, 'POST', '/exemptions');
      expect(body.claims_full_fmv).toBe(true);
      expect('amount' in body).toBe(false);
    });
  });

  it('sets the case’s election where the state allows one, and re-reads the table', async () => {
    let elected = false;
    const fetchMock = signedIn([
      ...common,
      {
        method: 'GET',
        fragment: `/v1/cases/${CASE_ID}/exemption-analysis`,
        respond: () =>
          jsonResponse(
            200,
            elected
              ? {
                  ...ANALYSIS,
                  election: {
                    ...UNDECIDED_TEXAS.election,
                    stored: 'federal',
                    effective: 'federal',
                  },
                  entries: [
                    {
                      ...ANALYSIS.entries[1],
                      entryId: 'us-vehicle',
                      citation: '11 U.S.C. § 522(d)(2)',
                      limit: '5025.00',
                      claimed: '0.00',
                      available: '5025.00',
                    },
                  ],
                  problems: [],
                }
              : UNDECIDED_TEXAS,
          ),
      },
      {
        method: 'PATCH',
        fragment: `/v1/cases/${CASE_ID}`,
        respond: () => {
          elected = true;
          return jsonResponse(200, { ...caseBody(CASE_ID), exemptionSet: 'federal' });
        },
      },
    ]);

    const user = await openHouse();
    expect(await screen.findByText(/Choose the exemption scheme/u)).toBeTruthy();
    await user.press(
      screen.getByRole('combobox', { name: 'Exemption scheme for this case (Schedule C, line 1)' }),
    );
    await user.press(
      await screen.findByRole('option', {
        name: 'Federal § 522(d) — Federal bankruptcy exemptions',
      }),
    );

    await waitFor(() =>
      expect(lastBody(fetchMock, 'PATCH', `/v1/cases/${CASE_ID}`)).toEqual({
        exemption_set: 'federal',
      }),
    );
    // The table is now the federal list, read back from the server.
    await user.press(await screen.findByRole('combobox', { name: 'Claim an exemption under' }));
    expect(
      await screen.findByRole('option', { name: '11 U.S.C. § 522(d)(2) — $5,025.00 available' }),
    ).toBeTruthy();
  });

  it('shows the § 522(p) cap and the domicile warning when the server raises them', async () => {
    signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/exemption-analysis`, {
        ...ANALYSIS,
        assets: [{ ...ANALYSIS.assets[0], homesteadCap: '214000.00', capApplied: true }],
        warnings: [
          'Debtor 1 left a prior address on 2025-03-01, inside the 730 days before 2026-12-01. Under § 522(b)(3)(A) the exemptions of the state where the debtor was domiciled for the 180 days before 2024-12-01 may govern, not the current residence in FL. Confirm the domicile before claiming.',
        ],
      }),
    ]);

    await openHouse();

    expect(
      await screen.findByText(/§ 522\(p\) limits the homestead claim to \$214,000\.00/u),
    ).toBeTruthy();
    // The warning is the design system's Alert, titled as such, not a bare line.
    expect(screen.getByText('Check before claiming')).toBeTruthy();
    expect(screen.getByText(/§ 522\(b\)\(3\)\(A\)/u)).toBeTruthy();
  });

  it('removes a claim and re-reads the figures', async () => {
    const fetchMock = signedIn([
      ...common,
      get(`/v1/cases/${CASE_ID}/exemption-analysis`, ANALYSIS),
      {
        method: 'DELETE',
        fragment: `/v1/cases/${CASE_ID}/exemptions/${EXEMPTION_ID}`,
        respond: () => new Response(null, { status: 204 }),
      },
    ]);

    const user = await openHouse();
    await user.press(await screen.findByRole('button', { name: 'Remove exemption 1' }));

    expect(await screen.findByText('Removed')).toBeTruthy();
    expect(
      fetchMock.mock.calls.some(
        ([url, init]) => init?.method === 'DELETE' && url.endsWith(`/exemptions/${EXEMPTION_ID}`),
      ),
    ).toBe(true);
  });
});
