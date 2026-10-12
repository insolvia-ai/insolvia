// The contract pin for the filing set and checklist (ADR 0024 build PR 3),
// in its own file so the section reads as one unit. Keep every literal in
// sync with services/api/src/insolvia_api/api/routes/filing_set.py
// (filing_set_json) and core/filing_set.py.
//
// The conventions are index.test.ts's: a stubbed fetch, an obviously fake
// token, the client imported by package name. This repo is public.
import { describe, expect, test } from 'vitest';

import { ApiException, InsolviaApiClient } from '@insolvia-ai/api-client';
import type { FetchLike } from '@insolvia-ai/api-client';

const ACCESS_TOKEN = 'test-access-token';
const BASE_URL = 'https://staging-api.insolvia.ai';
const CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
const PACKET_ID = 'b2c3d4e5-0000-4000-8000-0000000000b2';

function stub(status: number, body: unknown): { fetch: FetchLike; seen: string[] } {
  const seen: string[] = [];
  return {
    seen,
    fetch: (input, init) => {
      seen.push(`${init?.method ?? 'GET'} ${input}`);
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status,
          headers: { 'content-type': 'application/json' },
        }),
      );
    },
  };
}

function clientFor(s: { fetch: FetchLike }): InsolviaApiClient {
  return new InsolviaApiClient(BASE_URL, { fetch: s.fetch, accessToken: () => ACCESS_TOKEN });
}

const FULL = {
  registryRelease: 'courts/us-bankruptcy@2026-10-06',
  filingMethod: 'hand_off',
  orderBasis: 'default',
  namesBasis: 'default',
  maxBytes: 52428800,
  maxBytesBasis: 'court_unverified',
  court: { code: 'txwb', name: 'Western District of Texas', divisionName: 'Austin Division' },
  packet: { id: PACKET_ID, createdAt: '2099-01-02T03:04:05.006Z' },
  documents: [
    {
      key: 'form/b101',
      title: 'Official Form 101 — Voluntary Petition',
      fileName: '01-b101.pdf',
      source: 'packet',
      handling: 'file',
      checks: [
        { check: 'size', outcome: 'pass', message: '0.1 MB, under the 50.0 MB limit.' },
        { check: 'text_layer', outcome: 'pass', message: 'Every page has a text layer.' },
        { check: 'page_size', outcome: 'pass', message: 'Every page is US letter.' },
      ],
    },
    {
      key: 'form/b121',
      title: 'Official Form 121',
      fileName: '02-b121.pdf',
      source: 'packet',
      handling: 'not_filed',
      checks: [{ check: 'size', outcome: 'unmeasured', message: 'Assemble again.' }],
      note: 'Do not upload this file.',
    },
  ],
  checklist: [
    { id: 'packet', status: 'ready', title: 'Filing packet', detail: 'Assembled.', link: 'packet' },
    { id: 'fee', status: 'confirm', title: 'Filing fee', detail: 'Not recorded.' },
  ],
};

describe('the filing set', () => {
  test('GETs /v1/cases/{id}/filing-set and decodes every member', async () => {
    const s = stub(200, FULL);
    const filingSet = await clientFor(s).getCaseFilingSet(CASE_ID);

    expect(s.seen).toEqual([`GET ${BASE_URL}/v1/cases/${CASE_ID}/filing-set`]);
    expect(filingSet).toEqual(FULL);
  });

  test('a case with no court and no packet leaves both members absent', async () => {
    const { court: _court, packet: _packet, ...bare } = FULL;
    const filingSet = await clientFor(stub(200, bare)).getCaseFilingSet(CASE_ID);

    expect('court' in filingSet).toBe(false);
    expect('packet' in filingSet).toBe(false);
    expect(filingSet.documents[1]?.note).toBe('Do not upload this file.');
    expect('link' in (filingSet.checklist[1] ?? {})).toBe(false);
  });

  test('an unknown check outcome is a malformed response, not a silent pass', async () => {
    const broken = {
      ...FULL,
      documents: [
        { ...FULL.documents[0], checks: [{ check: 'size', outcome: 'ok', message: '' }] },
      ],
    };
    await expect(clientFor(stub(200, broken)).getCaseFilingSet(CASE_ID)).rejects.toBeInstanceOf(
      ApiException,
    );
  });

  test('a 404 rejects', async () => {
    await expect(
      clientFor(stub(404, { error: 'not_found', message: 'case not found' })).getCaseFilingSet(
        CASE_ID,
      ),
    ).rejects.toBeInstanceOf(ApiException);
  });
});
