import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { installFakeFileBrowser } from '@/screens/documents/testing';
import type { FakeFileBrowser } from '@/screens/documents/testing';
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
const PACKET_ID = '00000000-0000-4000-8000-0000000000p1';
const JOB_ID = '00000000-0000-4000-8000-0000000000j1';
const REVIEW_JOB_ID = '00000000-0000-4000-8000-0000000000j2';

/** A packet as `packet_json` renders it. `storageRef` is not among the keys. */
function packet(overrides: Record<string, unknown> = {}) {
  return {
    id: PACKET_ID,
    caseId: CASE_ID,
    jobId: JOB_ID,
    fileName: 'chapter7-packet.zip',
    contentType: 'application/zip',
    byteSize: 1843200,
    sha256: 'f2ca1bb6c7e907d06dafe4687e579fce76b37e4e93b7605022da52e6ccc26fd2',
    formRevisions: { 'form/b101': '2024-06-22' },
    constantsSetId: 'code/dollar-amounts@2025-04-01',
    creditorCount: 6,
    createdBy: '00000000-0000-4000-8000-0000000000a1',
    createdAt: '2026-09-03T10:00:00.123Z',
    options: {
      draftWatermark: false,
      printDate: false,
      signaturePages: 'all',
      signElectronically: false,
      amendedOnly: false,
    },
    ...overrides,
  };
}

/** A job as `job_json` renders it — the accept's 202 and the status polls. */
function job(overrides: Record<string, unknown> = {}) {
  return {
    id: JOB_ID,
    kind: 'packet_assembly',
    status: 'queued',
    createdBy: '00000000-0000-4000-8000-0000000000a1',
    attempts: 0,
    createdAt: '2026-09-03T10:00:00.123Z',
    updatedAt: '2026-09-03T10:00:00.123Z',
    ...overrides,
  };
}

/** A filing set as `filing_set_json` renders it — TXWB, no packet yet. */
function filingSetBody(overrides: Record<string, unknown> = {}) {
  return {
    registryRelease: 'courts/us-bankruptcy@2026-10-06',
    filingMethod: 'hand_off',
    orderBasis: 'default',
    namesBasis: 'default',
    maxBytes: 52428800,
    maxBytesBasis: 'court_unverified',
    court: { code: 'txwb', name: 'Western District of Texas', divisionName: 'Austin Division' },
    documents: [
      {
        key: 'form/b101',
        title: 'Official Form 101 — Voluntary Petition for Individuals Filing for Bankruptcy',
        fileName: '01-b101.pdf',
        source: 'packet',
        handling: 'file',
        checks: [{ check: 'size', outcome: 'unmeasured', message: 'Assemble the packet first.' }],
      },
      {
        key: 'form/b121',
        title: 'Official Form 121 — Statement About Your Social Security Numbers',
        fileName: '02-b121.pdf',
        source: 'packet',
        handling: 'not_filed',
        checks: [{ check: 'size', outcome: 'unmeasured', message: 'Assemble the packet first.' }],
        note: 'This court does not take B121. Do not upload this file.',
      },
    ],
    checklist: [
      {
        id: 'packet',
        status: 'missing',
        title: 'Filing packet',
        detail: 'No filing-set packet has been assembled.',
        link: 'packet',
      },
      {
        id: 'case_data:petitions',
        status: 'missing',
        title: 'Case data: petitions',
        detail: 'The petition has not been entered yet.',
        link: 'petition',
      },
      { id: 'fee', status: 'confirm', title: 'Filing fee', detail: 'Not recorded for this court.' },
    ],
    ...overrides,
  };
}

interface ApiStub {
  /** `GET /v1/cases/<id>/packets`, called again after a successful assembly. */
  list?: Answer;
  /** `POST .../jobs` — the trigger's 202. */
  accept?: Answer;
  /** `GET .../jobs/<id>` — the poll. Called until the job settles. */
  status?: Answer;
  /** `GET .../packets/<id>/url`. */
  url?: Answer;
  /** `GET .../forms` — the output-options panel's forms-subset checklist. */
  forms?: Answer;
  /** `GET .../filing-set` — the filing set and checklist panel. */
  filingSet?: Answer;
  /** The case record's status — `filed` is what offers an amendment. */
  caseStatus?: 'intake' | 'filed';
  /** The case record's chapter — 13 assembles the Chapter 13 set (#367). */
  caseChapter?: 7 | 13;
}

type Answer = () => Response | Promise<Response>;

function respond(stub: ApiStub, url: string, init?: RequestInit): Response | Promise<Response> {
  const method = init?.method ?? 'GET';
  if (url.endsWith('/jobs') && method === 'POST') {
    return (stub.accept ?? (() => jsonResponse(202, job())))();
  }
  if (url.includes('/jobs/')) {
    return (stub.status ?? (() => jsonResponse(200, job())))();
  }
  if (url.endsWith('/url')) {
    return (
      stub.url ??
      (() =>
        jsonResponse(200, {
          url: 'https://bucket.example.test/read-here',
          method: 'GET',
          expiresAt: '2026-09-03T10:05:00.123Z',
        }))
    )();
  }
  if (url.endsWith('/packets')) {
    return (stub.list ?? (() => jsonResponse(200, { packets: [] })))();
  }
  if (url.endsWith('/filing-set')) {
    return (stub.filingSet ?? (() => jsonResponse(200, filingSetBody())))();
  }
  if (url.endsWith('/forms')) {
    return (stub.forms ?? (() => jsonResponse(200, { forms: [] })))();
  }
  // The case LAYOUT's two reads, which every screen under /cases/[caseId] now
  // mounts above it. They come before the throw and after the branches above,
  // so a test that wants a named debtor or a filed case still overrides them by
  // matching earlier. See `caseShellRoutes` in @/session/testing.
  if (url.endsWith('/debtors')) {
    return jsonResponse(200, { debtors: [] });
  }
  if (url.endsWith(CASE_ID)) {
    return jsonResponse(
      200,
      caseBody(CASE_ID, {
        ...(stub.caseStatus === undefined ? {} : { status: stub.caseStatus }),
        ...(stub.caseChapter === undefined ? {} : { chapter: stub.caseChapter }),
      }),
    );
  }
  throw new Error(`unexpected ${method} ${url}`);
}

/**
 * `/cases/<id>/packet` — the app half of issue #96.
 *
 * Rendered through the real router, signed in first, exactly as the documents
 * suite does. What is asserted is what a preparer would lose if it broke: that
 * a blocked assembly shows the whole fix list instead of a toast, that a
 * failed one shows the job record's own words, and that the download mints on
 * press and opens under the packet's file name.
 */
describe('the filing packet screen', () => {
  let browser: FakeBrowser;
  let files: FakeFileBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(stub: ApiStub) {
    const fetchMock = jest.fn(async (url: string, init?: RequestInit) =>
      url.includes('/oauth2/token') ? tokenEndpointResponse() : respond(stub, url, init),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: `/cases/${CASE_ID}/packet` });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    files = installFakeFileBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    files.restore();
    browser.restore();
    jest.clearAllMocks();
  });

  it('says so plainly when nothing has been assembled yet', async () => {
    signedIn({});

    expect(await screen.findByText(/No packet has been assembled yet/)).toBeTruthy();
  });

  it('lists an assembled packet with its date, size and creditor count', async () => {
    signedIn({ list: () => jsonResponse(200, { packets: [packet()] }) });

    expect(await screen.findByText('chapter7-packet.zip')).toBeTruthy();
    expect(
      screen.getByText(/Assembled 2026-09-03 · 1.8 MB · 6 creditors on the matrix/),
    ).toBeTruthy();
  });

  it('downloads through a URL minted on press, under the packet file name', async () => {
    signedIn({ list: () => jsonResponse(200, { packets: [packet()] }) });
    await screen.findByText('chapter7-packet.zip');

    await userEvent.press(
      screen.getByRole('button', { name: 'Download the packet assembled 2026-09-03' }),
    );

    expect(await screen.findByText('Opened chapter7-packet.zip.')).toBeTruthy();
    expect(files.downloads).toEqual([
      { url: 'https://bucket.example.test/read-here', fileName: 'chapter7-packet.zip' },
    ]);
  });

  it('downloads a Chapter 13 packet under the name the server gave it', async () => {
    // The API names the zip by the case's chapter; the screen never builds
    // a name of its own, so a Chapter 13 packet must not arrive as chapter 7.
    signedIn({
      list: () => jsonResponse(200, { packets: [packet({ fileName: 'chapter13-packet.zip' })] }),
    });
    await screen.findByText('chapter13-packet.zip');

    await userEvent.press(
      screen.getByRole('button', { name: 'Download the packet assembled 2026-09-03' }),
    );

    expect(await screen.findByText('Opened chapter13-packet.zip.')).toBeTruthy();
    expect(files.downloads).toEqual([
      { url: 'https://bucket.example.test/read-here', fileName: 'chapter13-packet.zip' },
    ]);
  });

  it('assembles: accepts the job, polls it, and reloads the list on success', async () => {
    const assembled = packet();
    let settled = false;
    const fetchMock = signedIn({
      // Empty before the run, one packet after — the reload is what puts the
      // new packet on screen.
      list: () => jsonResponse(200, { packets: settled ? [assembled] : [] }),
      accept: () => jsonResponse(202, job()),
      status: () => {
        settled = true;
        return jsonResponse(
          200,
          job({
            status: 'succeeded',
            attempts: 1,
            result: { outcome: 'assembled', packet: assembled },
          }),
        );
      },
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(
      await screen.findByText('Packet assembled. It is ready to download below.'),
    ).toBeTruthy();
    expect(await screen.findByText('chapter7-packet.zip')).toBeTruthy();
    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(accepts).toHaveLength(1);
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({ kind: 'packet_assembly' });
  });

  // ── Output options (issue 13.11) ──────────────────────────────

  it('sends only the options actually changed on the accept body', async () => {
    const fetchMock = signedIn({
      list: () => jsonResponse(200, { packets: [] }),
      accept: () => jsonResponse(202, job()),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(screen.getByRole('checkbox', { name: '"Draft" watermark' }));
    await userEvent.press(screen.getByRole('radio', { name: 'Signature pages only' }));
    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({
      kind: 'packet_assembly',
      options: { draftWatermark: true, signaturePages: 'only' },
    });
  });

  it('offers a forms-subset checklist and sends the unchecked-out set', async () => {
    const fetchMock = signedIn({
      list: () => jsonResponse(200, { packets: [] }),
      forms: () =>
        jsonResponse(200, {
          forms: [
            {
              series: 'form/b101',
              form: 'b101',
              title: 'Petition',
              officialNumber: 'B 101',
              problems: [],
              openTaskCount: 0,
            },
            {
              series: 'form/b106ab',
              form: 'b106ab',
              title: 'Schedule A/B',
              officialNumber: 'B 106A/B',
              problems: [],
              openTaskCount: 0,
            },
          ],
        }),
      accept: () => jsonResponse(202, job()),
    });
    await screen.findByText(/No packet has been assembled yet/);
    await screen.findByRole('checkbox', { name: 'B 106A/B' });

    await userEvent.press(screen.getByRole('checkbox', { name: 'B 106A/B' }));
    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({
      kind: 'packet_assembly',
      options: { forms: ['b101'] },
    });
  });

  it('names the Chapter 13 set, with its plan, on a Chapter 13 case', async () => {
    signedIn({ caseChapter: 13 });

    expect(
      await screen.findByRole('button', {
        name: 'Assemble the Chapter 13 filing packet for this case',
      }),
    ).toBeTruthy();
    expect(screen.getByText(/the Chapter 13 plan \(Official Form 113\)/)).toBeTruthy();
  });

  it('never offers an amendment on a case that is not filed', async () => {
    signedIn({});
    await screen.findByText(/No packet has been assembled yet/);

    expect(screen.queryByRole('checkbox', { name: 'Amended items only' })).toBeNull();
  });

  it('assembles an amendment of a filed case, without a forms subset', async () => {
    const fetchMock = signedIn({
      caseStatus: 'filed',
      list: () => jsonResponse(200, { packets: [] }),
      forms: () =>
        jsonResponse(200, {
          forms: [
            {
              series: 'form/b106ef',
              form: 'b106ef',
              title: 'Schedule E/F',
              officialNumber: 'B 106E/F',
              problems: [],
              openTaskCount: 0,
            },
          ],
        }),
      accept: () => jsonResponse(202, job()),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(await screen.findByRole('checkbox', { name: 'Amended items only' }));
    // The API refuses a subset alongside an amendment, so the checklist goes.
    expect(screen.queryByRole('checkbox', { name: 'B 106E/F' })).toBeNull();
    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({
      kind: 'packet_assembly',
      options: { amendedOnly: true },
    });
  });

  it('labels an amendment packet on its row', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          packets: [
            packet({
              options: {
                draftWatermark: false,
                printDate: false,
                signaturePages: 'all',
                signElectronically: false,
                amendedOnly: true,
              },
            }),
          ],
        }),
    });

    await screen.findByText('chapter7-packet.zip');

    expect(screen.getByText('Amendment')).toBeTruthy();
  });

  it('shows a non-default packet’s options as a badge on its row', async () => {
    signedIn({
      list: () =>
        jsonResponse(200, {
          packets: [
            packet({
              options: {
                draftWatermark: true,
                printDate: false,
                signaturePages: 'all',
                signElectronically: false,
                amendedOnly: false,
              },
            }),
          ],
        }),
    });

    await screen.findByText('chapter7-packet.zip');

    expect(screen.getByText('Draft')).toBeTruthy();
  });

  it('renders the whole fix list when the completeness gate refuses', async () => {
    signedIn({
      status: () =>
        jsonResponse(
          200,
          job({
            status: 'succeeded',
            attempts: 1,
            result: {
              outcome: 'blocked',
              problems: [
                {
                  source: 'creditors',
                  itemId: 'cred-1',
                  field: 'address.postal_code',
                  message: 'A ZIP code is required.',
                },
                {
                  source: 'form/b101',
                  message: 'line 9: 3 prior cases but the form prints 2 rows',
                },
              ],
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(await screen.findByText('The case is not ready to file')).toBeTruthy();
    expect(screen.getByText('Creditors')).toBeTruthy();
    expect(screen.getByText('A ZIP code is required.')).toBeTruthy();
    expect(screen.getByText('Form B101')).toBeTruthy();
    expect(screen.getByText(/prior cases but the form prints/)).toBeTruthy();
    // Nothing was produced, and the screen keeps saying so.
    expect(screen.getByText(/No packet has been assembled yet/)).toBeTruthy();
  });

  it('runs the AI review and lists advisory findings with their form and line', async () => {
    const fetchMock = signedIn({
      list: () => jsonResponse(200, { packets: [packet()] }),
      accept: () => jsonResponse(202, job({ id: REVIEW_JOB_ID, kind: 'petition_review' })),
      status: () =>
        jsonResponse(
          200,
          job({
            id: REVIEW_JOB_ID,
            kind: 'petition_review',
            status: 'succeeded',
            attempts: 1,
            result: {
              outcome: 'reviewed',
              report: {
                packetId: PACKET_ID,
                packetSha256: packet().sha256,
                model: 'claude-opus-5',
                findings: [
                  {
                    severity: 'high',
                    category: 'consistency',
                    form: 'form/b106i',
                    line: '4_combined_monthly_income',
                    message: 'Schedule I income disagrees with the SOFA income answers.',
                  },
                  {
                    severity: 'low',
                    category: 'transfer',
                    form: 'form/b107',
                    line: '',
                    message: 'A closed account in the last year has no matching SOFA entry.',
                  },
                ],
              },
            },
          }),
        ),
    });
    await screen.findByText('chapter7-packet.zip');

    await userEvent.press(
      screen.getByRole('button', { name: 'Run the AI review of the assembled packet' }),
    );

    expect(
      await screen.findByText('Schedule I income disagrees with the SOFA income answers.'),
    ).toBeTruthy();
    // Severity badge, and the form + line citation the preparer navigates by.
    expect(screen.getByText('High')).toBeTruthy();
    expect(screen.getByText('Form B106I · 4_combined_monthly_income')).toBeTruthy();
    // A finding without a single line cites the form alone.
    expect(screen.getByText('Form B107')).toBeTruthy();
    const accepts = fetchMock.mock.calls.filter(
      ([url, init]) => init?.method === 'POST' && String(url).endsWith('/jobs'),
    );
    expect(accepts).toHaveLength(1);
    expect(JSON.parse(String(accepts[0]?.[1]?.body))).toEqual({ kind: 'petition_review' });
  });

  it('says plainly when the review found nothing to flag', async () => {
    signedIn({
      list: () => jsonResponse(200, { packets: [packet()] }),
      accept: () => jsonResponse(202, job({ id: REVIEW_JOB_ID, kind: 'petition_review' })),
      status: () =>
        jsonResponse(
          200,
          job({
            id: REVIEW_JOB_ID,
            kind: 'petition_review',
            status: 'succeeded',
            attempts: 1,
            result: {
              outcome: 'reviewed',
              report: { packetId: PACKET_ID, model: 'claude-opus-5', findings: [] },
            },
          }),
        ),
    });
    await screen.findByText('chapter7-packet.zip');

    await userEvent.press(
      screen.getByRole('button', { name: 'Run the AI review of the assembled packet' }),
    );

    expect(await screen.findByText(/The review found nothing to flag/)).toBeTruthy();
  });

  it('says why the packet cannot be reviewed yet, in the worker’s own words', async () => {
    signedIn({
      accept: () => jsonResponse(202, job({ id: REVIEW_JOB_ID, kind: 'petition_review' })),
      status: () =>
        jsonResponse(
          200,
          job({
            id: REVIEW_JOB_ID,
            kind: 'petition_review',
            status: 'succeeded',
            attempts: 1,
            result: {
              outcome: 'blocked',
              problems: [
                {
                  source: 'packets',
                  message:
                    'No packet has been assembled yet — assemble the filing packet, then run the review.',
                },
              ],
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet. When one is/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Run the AI review of the assembled packet' }),
    );

    expect(await screen.findByText('The packet cannot be reviewed yet')).toBeTruthy();
    expect(
      screen.getByText(
        'No packet has been assembled yet — assemble the filing packet, then run the review.',
      ),
    ).toBeTruthy();
  });

  it('shows the review job record’s own words when it fails', async () => {
    signedIn({
      accept: () => jsonResponse(202, job({ id: REVIEW_JOB_ID, kind: 'petition_review' })),
      status: () =>
        jsonResponse(
          200,
          job({
            id: REVIEW_JOB_ID,
            kind: 'petition_review',
            status: 'failed',
            attempts: 1,
            failure: {
              category: 'not_configured',
              message: 'AI review is not configured in this environment yet.',
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Run the AI review of the assembled packet' }),
    );

    expect(
      await screen.findByText('AI review is not configured in this environment yet.'),
    ).toBeTruthy();
    // The button re-enables for when the environment gains its key.
    expect(
      screen.getByRole('button', { name: 'Run the AI review of the assembled packet' }),
    ).toBeEnabled();
  });

  it('shows the job record’s own words when the pipeline fails', async () => {
    signedIn({
      status: () =>
        jsonResponse(
          200,
          job({
            status: 'failed',
            attempts: 1,
            failure: {
              category: 'case_changed',
              message:
                'The case changed while its packet was being assembled — run assembly again.',
            },
          }),
        ),
    });
    await screen.findByText(/No packet has been assembled yet/);

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );

    expect(
      await screen.findByText(/The case changed while its packet was being assembled/),
    ).toBeTruthy();
    // The button re-enables for the retry the message asks for.
    expect(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    ).toBeEnabled();
  });

  // ── The filing set and checklist (ADR 0024 PR 3) ──────────────

  it('shows the filing set for the case’s court, in order, with each file’s checks', async () => {
    signedIn({});

    expect(await screen.findByText('Filing set and checklist')).toBeTruthy();
    expect(screen.getByText(/^Western District of Texas, Austin Division\./)).toBeTruthy();
    expect(screen.getByText('1. 01-b101.pdf')).toBeTruthy();
    expect(screen.getByText('2. 02-b121.pdf')).toBeTruthy();
    // B121 says how it is handled in this court, not just that it exists.
    expect(screen.getByText('Not filed in this court')).toBeTruthy();
    expect(screen.getByText(/Do not upload this file/)).toBeTruthy();
    expect(screen.getAllByText('Size: unmeasured')).toHaveLength(2);
    expect(screen.getByText(/docket order and file names are not on record/)).toBeTruthy();
  });

  it('lists the checklist with each item’s status and a link to fix it', async () => {
    signedIn({});

    expect(await screen.findByText('Case data: petitions')).toBeTruthy();
    expect(screen.getByText('The petition has not been entered yet.')).toBeTruthy();
    expect(screen.getAllByText('Missing')).toHaveLength(2);
    expect(screen.getByText('Confirm')).toBeTruthy();
    const fix = screen.getByRole('link', { name: 'Go to fix: Case data: petitions' });
    expect(fix.props.href).toBe(`/cases/${CASE_ID}/petition`);
    // A step outside Insolvia has no link.
    expect(screen.queryByRole('link', { name: 'Go to fix: Filing fee' })).toBeNull();
  });

  it('re-reads the filing set after a packet is assembled', async () => {
    let settled = false;
    const fetchMock = signedIn({
      accept: () => jsonResponse(202, job()),
      status: () => {
        settled = true;
        return jsonResponse(
          200,
          job({ status: 'succeeded', attempts: 1, result: { outcome: 'assembled' } }),
        );
      },
      filingSet: () =>
        jsonResponse(
          200,
          filingSetBody(
            settled ? { packet: { id: PACKET_ID, createdAt: '2026-09-03T10:00:00.123Z' } } : {},
          ),
        ),
    });
    await screen.findByText('Filing set and checklist');

    await userEvent.press(
      screen.getByRole('button', { name: 'Assemble the Chapter 7 filing packet for this case' }),
    );
    await screen.findByText('Packet assembled. It is ready to download below.');

    const reads = () =>
      fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/filing-set')).length;
    await waitFor(() => {
      expect(reads()).toBeGreaterThanOrEqual(2);
    });
  });

  it('says so when the filing set cannot load, and the rest of the screen still works', async () => {
    signedIn({ filingSet: () => jsonResponse(500, { error: 'internal', message: 'boom' }) });

    expect(await screen.findByText(/Could not load the filing set/)).toBeTruthy();
    expect(screen.getByText(/No packet has been assembled yet/)).toBeTruthy();
  });
});
