import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  caseBody,
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
const ALICE = '00000000-0000-4000-8000-00000000a11c';

function me(extractionReview: string = 'add_edit') {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions: {
        cases: 'add_edit',
        intake: 'add_edit',
        documents: 'add_edit',
        extraction_review: extractionReview,
        firm_administration: 'hidden',
      },
    },
  };
}

const DIRECTORY = {
  people: [
    {
      subject: '00000000-0000-4000-8000-000000000001',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      role: 'attorney',
    },
  ],
};

/** A debtor carrying only the parts of the record this screen reads. */
function debtor(given: string, surname: string) {
  return {
    id: 'debtor_1',
    case_id: CASE_ID,
    filing_role: 'debtor_1',
    created_at: '2026-08-04T10:00:00.000000Z',
    updated_at: '2026-08-04T10:00:00.000000Z',
    provenance: {},
    name: { given, surname },
  };
}

/** A `GET /v1/cases/{id}/summary` body, in the route's exact wire shape. */
function summaryBody(
  over: { readonly readyToFile?: boolean; readonly problems?: unknown[] } = {},
  totals: Readonly<Record<string, string>> = {},
) {
  return {
    readyToFile: over.readyToFile ?? false,
    problems: over.problems ?? [
      {
        source: 'debtors',
        message: "The case has no Debtor 1 record — every form prints the debtor's name.",
      },
    ],
    totals: {
      realEstate: '0',
      personalProperty: '0',
      assets: '0',
      secured: '0',
      priorityUnsecured: '0',
      nonpriorityUnsecured: '0',
      liabilities: '0',
      ...totals,
    },
  };
}

/**
 * Everything the layout and the overview read, each empty by default, so a test
 * states only the endpoint it is about.
 *
 * `over` is spread LAST so a test's own answer wins. The bare `/v1/cases/<id>`
 * is deliberately not here — it is a prefix of every URL below and `routeFetch`
 * takes the first fragment that matches, so it has to be declared after these.
 */
function caseReads(
  over: Readonly<Record<string, () => Response>> = {},
): Readonly<Record<string, () => Response>> {
  return {
    [`/v1/cases/${CASE_ID}/debtors`]: () => jsonResponse(200, { debtors: [] }),
    [`/v1/cases/${CASE_ID}/documents`]: () => jsonResponse(200, { documents: [] }),
    [`/v1/cases/${CASE_ID}/creditors`]: () => jsonResponse(200, { creditors: [] }),
    [`/v1/cases/${CASE_ID}/packets`]: () => jsonResponse(200, { packets: [] }),
    [`/v1/cases/${CASE_ID}/assignees`]: () => jsonResponse(200, { assignees: [] }),
    [`/v1/cases/${CASE_ID}/extraction/candidates`]: () => jsonResponse(200, { candidates: [] }),
    [`/v1/cases/${CASE_ID}/summary`]: () => jsonResponse(200, summaryBody()),
    ...over,
  };
}

/**
 * `/cases/<id>` — the case's own page, and with it the case LAYOUT that every
 * screen under `/cases/[caseId]` now renders inside.
 *
 * Both are exercised together and through the real router on purpose: the
 * layout's whole job is to load the case and publish it, and the thing worth
 * pinning is that a screen underneath actually receives it. Rendering
 * `CaseOverview` on its own would need `useCase()` stubbed, which would test the
 * stub.
 *
 * What is asserted is what the server cannot check for us: that a case with no
 * debtors is still called something, that a count which failed to load is not
 * reported as zero, that a hidden feature stays out of the rail, and that a case
 * the caller may not have refuses without saying which of the two reasons applies.
 */
describe('the case overview', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(handlers: Readonly<Record<string, () => Response>>) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, _init?: RequestInit) => route(url));
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
    return fetchMock;
  }

  /** The common case: signed in, permitted, every count empty. */
  function ready(over: Readonly<Record<string, () => Response>> = {}) {
    return signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      ...caseReads(over),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(200, caseBody(CASE_ID)),
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
    jest.clearAllMocks();
  });

  /**
   * The page's own `<h1>`.
   *
   * Queried by role and level rather than by its text, because the rail shows
   * the same case name a few pixels away — deliberately, since that is what
   * tells you which case you are in on the other six screens. Asking for the
   * text alone matches both and says "found multiple elements".
   */
  async function pageTitle(): Promise<string> {
    // Waits for the overview's own section heading before reading anything.
    // The shell shows a "Opening case" status screen while the case loads, and
    // that screen has an `<h1>` too — querying headings straight away resolves
    // against it and then reads props off a node that has since unmounted.
    await screen.findByText('Where this case stands');
    const h1 = screen.getAllByRole('heading').find((node) => node.props['aria-level'] === 1);
    expect(h1).toBeDefined();
    return String(h1?.props.children);
  }

  it('names a case by its debtors once intake has supplied one', async () => {
    ready({
      [`/v1/cases/${CASE_ID}/debtors`]: () =>
        jsonResponse(200, { debtors: [debtor('Marisol', 'Reyes')] }),
    });

    expect(await pageTitle()).toBe('Marisol Reyes');
  });

  it('falls back to the chapter and district, never the id', async () => {
    // The id is a server-generated uuid that identifies nothing about a person.
    // A case with no debtor yet still has to be called something.
    ready();

    expect(await pageTitle()).toBe('Chapter 7 · NDCA');
    expect(screen.queryByText(CASE_ID)).toBeNull();
  });

  it('gives the page exactly one level-1 heading', async () => {
    ready();
    await pageTitle();

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });

  it('reports what each section actually holds', async () => {
    ready({
      [`/v1/cases/${CASE_ID}/assignees`]: () =>
        jsonResponse(200, {
          assignees: [
            { subject: ALICE, assignedAt: '2026-08-04T10:00:00.000Z', assignedBy: ALICE },
          ],
        }),
    });

    expect(await screen.findByText('1 person')).toBeTruthy();
    // Zero is a real answer and reads as one, rather than as a bare "0".
    expect(screen.getByText('No creditors yet')).toBeTruthy();
  });

  it('does not report a count it failed to read as zero', async () => {
    // "0 creditors" and "we could not ask" look identical if both say zero, and
    // only one of them means "go and add some".
    ready({ [`/v1/cases/${CASE_ID}/creditors`]: () => jsonResponse(500, { message: 'nope' }) });
    await screen.findByText('Nobody assigned');

    // Exactly one: the creditors row. Everything else answered.
    expect(screen.getAllByText('—')).toHaveLength(1);
  });

  it('keeps extraction review out of the page when the firm cannot see it', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, me('hidden')),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      ...caseReads(),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(200, caseBody(CASE_ID)),
    });
    await screen.findByText('Nobody assigned');

    // Neither in the rail nor in the standing list.
    expect(screen.queryByText('Extraction review')).toBeNull();
  });

  it('refuses a case it cannot open without saying which reason applies', async () => {
    // The API answers 404 for unknown AND for not-yours, deliberately, so that a
    // caller cannot use it to discover that a case exists. The screen keeps that.
    signedIn({
      '/v1/me': () => jsonResponse(200, me()),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(404, { message: 'not found' }),
    });

    expect(await screen.findByText(/could not be opened/)).toBeTruthy();
  });
});

/**
 * The rail `CaseShell` puts beside every case screen.
 *
 * Exercised from the overview route because that is where it first appears, and
 * because the claim worth pinning is the one the old app could not make at all:
 * that a case screen can reach its siblings without going back to the list.
 */
describe('the case rail', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function ready() {
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      ...caseReads(),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(200, caseBody(CASE_ID)),
    });
    globalThis.fetch = jest.fn((url: string, _init?: RequestInit) =>
      route(url),
    ) as unknown as typeof fetch;
    return renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('moves between a case’s sections without going back to the list', async () => {
    // The whole point of the layout. Before it, intake -> documents meant
    // returning to /cases and finding the row again, because the six links only
    // ever existed there.
    const router = ready();
    await screen.findByText('Where this case stands');

    await userEvent.press(screen.getByLabelText('Documents'));

    // `getPathname()` rather than the `toHavePathname` matcher, for the reason
    // not-found.test.tsx gives: the matcher is registered at runtime but
    // expo-router ships no type declaration for it, so it would not typecheck.
    expect(router.getPathname()).toBe(`/cases/${CASE_ID}/documents`);
  });

  it('offers every section of a case in one place', async () => {
    ready();
    await screen.findByText('Where this case stands');

    for (const label of [
      'Overview',
      'Intake',
      'Documents',
      'Extraction review',
      'Creditor matrix',
      'Filing packet',
      'Team',
    ]) {
      expect(screen.getByLabelText(label)).toBeTruthy();
    }
  });

  it('keeps a way back out to the case list', async () => {
    const router = ready();
    await screen.findByText('Where this case stands');

    await userEvent.press(screen.getByLabelText('All cases'));

    expect(router.getPathname()).toBe('/cases');
  });
});

/**
 * Filing readiness and the money.
 *
 * Both come from `GET /v1/cases/{id}/summary`, and both are things the screen
 * must not compute for itself — the totals because a second sum is a second
 * answer to what a debtor owes, and readiness because an overview that says
 * "ready" over a case the assembler refuses is worse than one that says
 * nothing.
 */
describe('the case overview’s readiness and totals', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function withSummary(body: unknown) {
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      '/v1/me': () => jsonResponse(200, me()),
      '/v1/firm/directory': () => jsonResponse(200, DIRECTORY),
      ...caseReads({ [`/v1/cases/${CASE_ID}/summary`]: () => jsonResponse(200, body) }),
      [`/v1/cases/${CASE_ID}`]: () => jsonResponse(200, caseBody(CASE_ID)),
    });
    globalThis.fetch = jest.fn((url: string, _init?: RequestInit) =>
      route(url),
    ) as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}` });
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('says what blocks filing, naming where each fix belongs', async () => {
    withSummary(
      summaryBody({
        problems: [
          { source: 'form/b106d', field: 'amount', message: 'Schedule D needs an amount.' },
        ],
      }),
    );

    expect(await screen.findByText('Schedule D needs an amount.')).toBeTruthy();
    // `form/b106d` is not what a paralegal calls it.
    expect(screen.getByText('B106D')).toBeTruthy();
  });

  it('says so plainly when nothing blocks filing', async () => {
    withSummary(summaryBody({ readyToFile: true, problems: [] }));

    expect(await screen.findByText(/can assemble its packet/)).toBeTruthy();
  });

  it('renders the totals exactly as the server sent them', async () => {
    // Digit for digit, and never re-derived. `liabilities` is the server's sum;
    // a screen that added the three itself could disagree with the schedules.
    withSummary(
      summaryBody(
        {},
        { secured: '14500.00', nonpriorityUnsecured: '8412.66', liabilities: '22912.66' },
      ),
    );

    expect(await screen.findByText('22912.66')).toBeTruthy();
    expect(screen.getByText('14500.00')).toBeTruthy();
    expect(screen.getByText('8412.66')).toBeTruthy();
  });

  it('claims nothing about readiness when the summary could not be read', async () => {
    // The dangerous failure is the optimistic one: a case that looks filable
    // because the check failed.
    withSummary(undefined);
    await screen.findByText('Nobody assigned');

    expect(screen.queryByText(/can assemble its packet/)).toBeNull();
  });
});
