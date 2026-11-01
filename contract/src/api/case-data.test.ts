// A case's records and everything computed from them: the generic
// collections, the creditor matrix, summary, liens, exemptions, standards,
// means test and plan, the forms hub, previews and the filing set.
import {
  CASE_COLLECTIONS,
  type CaseCollection,
  type CaseEntity,
  type CaseEntityRequest,
} from '@insolvia-ai/api-client';
import { beforeAll, describe, expect, it } from '@jest/globals';

import { firmAdmin, scratchCase } from '../support/actors.ts';
import { fixtureCase, fixtureCases } from '../support/fixtures.ts';
import { needsBlobUrls, offlineOnly } from '../support/gates.ts';
import { expectApiError, type Spec, withMessage } from '../support/wire.ts';

const ENTITY: Spec = {
  id: 'string',
  case_id: 'string',
  created_at: 'string',
  updated_at: 'string',
  provenance: 'object',
  amended: 'boolean',
};
const IDENTITY = new Set(['id', 'case_id', 'created_at', 'updated_at', 'amended']);
const PROBLEM: Spec = { 'code?': 'string', message: 'string' };

let chapter13: string;
let chapter7: string;
beforeAll(async () => {
  chapter13 = (await fixtureCase(13)).id;
  chapter7 = (await fixtureCase(7)).id;
});

describe('the generic case collections', () => {
  it('every collection lists, and every fixture record reads back alone', async () => {
    const admin = firmAdmin();
    for (const collection of CASE_COLLECTIONS) {
      const items = await admin.api.listCaseEntities(chapter13, collection);
      admin.wire(200, { [collection]: [ENTITY] });
      const first = items[0];
      if (first === undefined) continue;
      const one = await admin.api.getCaseEntity(chapter13, collection, first.id);
      admin.wire(200, ENTITY);
      withMessage(collection, () => expect(one).toEqual(first));
    }
  });

  it('every fixture record survives a round trip into a scratch case', async () => {
    // Each chapter 13 fixture record is re-posted into a scratch case, with any
    // reference to another fixture record (a claim's creditor, say) mapped to
    // its scratch copy — then read, saved again, and deleted. Wire parity for
    // every collection's write path, with bodies the domain already accepts.
    const admin = firmAdmin();
    const { caseId } = await scratchCase(13);
    const idMap = new Map<string, string>();
    const remap = (value: unknown): unknown => {
      if (typeof value === 'string') return idMap.get(value) ?? value;
      if (Array.isArray(value)) return value.map(remap);
      if (value !== null && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, remap(v)]));
      }
      return value;
    };
    const created: [CaseCollection, string][] = [];
    for (const collection of CASE_COLLECTIONS) {
      const items = await admin.api.listCaseEntities(chapter13, collection);
      for (const item of items) {
        const body = remap(
          Object.fromEntries(Object.entries(item).filter(([key]) => !IDENTITY.has(key))),
        ) as CaseEntityRequest<typeof collection>;
        const added = await admin.api.addCaseEntity(caseId, collection, body);
        admin.wire(201, ENTITY);
        idMap.set(item.id, added.id);
        created.push([collection, added.id]);
        const saved = await admin.api.putCaseEntity(caseId, collection, added.id, body);
        admin.wire(200, ENTITY);
        expect(saved.id).toBe(added.id);
      }
    }
    expect(created.length).toBeGreaterThan(20);

    for (const [collection, id] of created.reverse()) {
      await admin.api.deleteCaseEntity(caseId, collection, id);
      admin.wire(204);
    }
    const [collection, id] = created[0] as [CaseCollection, string];
    await expectApiError(admin.api.getCaseEntity(caseId, collection, id), 404, 'NotFoundError');
    await expectApiError(admin.api.deleteCaseEntity(caseId, collection, id), 404, 'NotFoundError');
  });

  it('refuses an invalid record by field, and an unknown collection or case', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    await expectApiError(
      admin.api.addCaseEntity(caseId, 'creditors', { name: 42 } as never),
      400,
      'ValidationError',
    );
    await expectApiError(
      admin.api.listCaseEntities(caseId, 'not_a_collection' as CaseCollection),
      404,
      'NotFoundError',
    );
    await expectApiError(
      admin.api.listCaseEntities('no-such-case', 'creditors'),
      404,
      'NotFoundError',
    );
    const creditor: CaseEntity<'creditors'> = await admin.api.addCaseEntity(caseId, 'creditors', {
      name: 'Example Bank',
      provenance: { name: { source: 'staff_typed' } },
    });
    await expectApiError(
      admin.api.putCaseEntity(caseId, 'creditors', creditor.id, { name: 7 } as never),
      400,
      'ValidationError',
    );
  });
});

describe('what a case computes', () => {
  it('the creditor matrix, summary and liens', async () => {
    const admin = firmAdmin();
    const matrix = await admin.api.getCreditorMatrix(chapter13);
    admin.wire(200, {
      fileName: 'string',
      creditorCount: 'integer',
      duplicatesOmitted: 'integer',
      problems: ['object'],
      'content?': 'string',
    });
    expect(matrix.creditorCount).toBeGreaterThan(0);
    // Every fixture creditor has an address the matrix can print — the seed
    // loader's cross references resolved to ids that exist in this stack.
    for (const fixture of await fixtureCases()) {
      const printed = await admin.api.getCreditorMatrix(fixture.case.id);
      withMessage(fixture.spec.handle, () => {
        expect(printed.problems).toEqual([]);
        expect(printed.creditorCount).toBe(fixture.spec.collections?.creditors?.length ?? 0);
      });
    }

    const summary = await admin.api.getCaseSummary(chapter13);
    admin.wire(200, {
      readyToFile: 'boolean',
      problems: [PROBLEM],
      totals: 'object',
      liens: { claims: ['object'], assets: ['object'] },
    });
    expect(typeof summary.readyToFile).toBe('boolean');

    await admin.api.getCaseLiens(chapter13);
    admin.wire(200, { claims: ['object'], assets: ['object'] });
  });

  it('exemptions, standards and the means test', async () => {
    const admin = firmAdmin();
    await admin.api.getCaseExemptionAnalysis(chapter13);
    admin.wire(200, {
      asOf: 'string',
      asOfSource: 'string',
      election: 'object',
      entries: ['object'],
      limits: ['object'],
      lookbacks: 'object',
      assets: ['object'],
      warnings: ['string'],
      problems: ['string'],
    });

    await admin.api.getCaseStandards(chapter13);
    admin.wire(200, {
      asOf: 'string',
      state: 'string|null',
      county: 'string|null',
      householdSize: 'integer|null',
      jurisdictionSource: 'string|null',
      nationalStandards: 'object',
      localStandards: 'object',
      problems: ['string'],
    });

    for (const caseId of [chapter7, chapter13]) {
      const test = await admin.api.getCaseMeansTest(caseId);
      admin.wire(200, {
        asOf: 'string',
        asOfSource: 'string',
        chapter: 'integer',
        releaseIds: 'object',
        jurisdiction: { state: 'string|null', county: 'string|null', district: 'string' },
        maritalFilingStatus: { value: 'string|null', source: 'string' },
        household: 'object',
        exemptions: 'object',
        cmi: 'object',
        debt: 'object',
        comparison: 'object|null',
        outcome: 'string',
        determinedBy: 'string|null',
        lines: ['object'],
        chapter13: 'object|null',
        problems: ['string'],
      });
      expect([7, 13]).toContain(test.chapter);
    }
    // The chapter 13 fixture is above the median over five years, so B122C-2 files.
    const trace = await admin.api.getCaseMeansTest(chapter13);
    expect(trace.problems).toEqual([]);
    expect(trace.outcome).toBe('above_median');
    expect(trace.chapter13).toMatchObject({
      commitmentPeriodMonths: 60,
      disposableIncomeRequired: true,
    });
  });

  it('the chapter 13 plan, and what-if scenarios', async () => {
    const admin = firmAdmin();
    const PLAN: Spec = {
      planPresent: 'boolean',
      chapter: 'integer',
      commitmentPeriod: 'object',
      funding: 'object',
      trusteePercentage: 'string|null',
      classes: ['object'],
      unsecured: 'object',
      feasibility: 'object',
      liquidation: 'object',
      bestInterests: 'object',
      warnings: ['string'],
      problems: ['string'],
    };
    const plan = await admin.api.getCasePlanCalculation(chapter13);
    admin.wire(200, PLAN);
    expect(plan.planPresent).toBe(true);
    // The fixture plan is feasible and passes § 1325(a)(4).
    expect(plan.problems).toEqual([]);
    expect(plan.feasibility).toMatchObject({ feasible: true });
    expect(plan.bestInterests).toMatchObject({ passes: true });
    expect(plan.commitmentPeriod).toMatchObject({ months: 60 });

    const [stored] = await admin.api.listCaseEntities(chapter13, 'plans');
    const body = Object.fromEntries(
      Object.entries(stored ?? {}).filter(([key]) => !IDENTITY.has(key) && key !== 'provenance'),
    );
    const scenarios = await admin.api.calculatePlanScenarios(chapter13, [
      { label: 'as filed', plan: body as never },
      { plan: body as never },
    ]);
    admin.wire(200, { scenarios: [{ label: 'string|null', calculation: PLAN }] });
    expect(scenarios).toHaveLength(2);
    expect(scenarios[1]?.label).toBeNull();

    await expectApiError(admin.api.calculatePlanScenarios(chapter13, []), 400, 'ValidationError');
  });
});

describe('forms, previews, packets and the filing set', () => {
  needsBlobUrls().it('lists the forms hub and previews one form as a PDF', async () => {
    const admin = firmAdmin();
    const forms = await admin.api.listCaseForms(chapter7);
    admin.wire(200, {
      forms: [
        {
          series: 'string',
          form: 'string',
          title: 'string',
          officialNumber: 'string',
          'metric?': 'object',
          problems: [PROBLEM],
          openTaskCount: 'integer',
        },
      ],
    });
    const b101 = forms.find((f) => f.officialNumber === '101') ?? forms[0];
    expect(b101).toBeDefined();

    const preview = await admin.api.getCaseFormPreview(chapter7, b101!.form, {
      draftWatermark: true,
    });
    admin.wire(200, {
      problems: [PROBLEM],
      'url?': 'string',
      'method?': 'string',
      'expiresAt?': 'string',
      options: 'object',
    });
    if (preview.url !== undefined) {
      const pdf = await fetch(preview.url);
      expect(pdf.status).toBe(200);
      expect(new TextDecoder().decode((await pdf.arrayBuffer()).slice(0, 5))).toBe('%PDF-');
    }

    await expectApiError(admin.api.getCaseFormPreview(chapter7, 'no-such-form'), 404);
  });

  it('packets are listed, and an unknown one is a 404', async () => {
    const admin = firmAdmin();
    const packets = await admin.api.listCasePackets(chapter7);
    admin.wire(200, { packets: ['object'] });
    expect(Array.isArray(packets)).toBe(true);
    await expectApiError(admin.api.getPacketUrl(chapter7, 'no-such-packet'), 404, 'NotFoundError');
  });

  it('the filing set names the court and its documents', async () => {
    const admin = firmAdmin();
    const set = await admin.api.getCaseFilingSet(chapter7);
    admin.wire(200, {
      'court?': { code: 'string', name: 'string', 'divisionName?': 'string' },
      registryRelease: 'string',
      filingMethod: 'string',
      'packet?': { id: 'string', createdAt: 'string' },
      orderBasis: 'string',
      namesBasis: 'string',
      maxBytes: 'integer',
      maxBytesBasis: 'string',
      documents: ['object'],
      checklist: ['object'],
    });
    expect(set.court?.code).toBe('flmb');

    // Every fixture case is served its court's filing set and checklist. The
    // fixture cases are never assembled here (each run would store another
    // multi-megabyte packet), so the file checks read "unmeasured".
    for (const fixture of await fixtureCases()) {
      const served = await admin.api.getCaseFilingSet(fixture.case.id);
      withMessage(fixture.spec.handle, () => {
        expect(served.court?.code).toBe(fixture.spec.court);
        expect(served.filingMethod).toBe('hand_off');
        const keys = served.documents.map((d) => (d as { key: string }).key);
        expect(keys[0]).toBe('form/b101');
        expect(keys).toContain('creditor_matrix');
        const b121 = served.documents.find((d) => (d as { key: string }).key === 'form/b121');
        expect(['restricted', 'not_filed']).toContain((b121 as { handling?: string }).handling);
        const checklist = new Map(
          served.checklist.map((item) => {
            const entry = item as { id: string; status: string };
            return [entry.id, entry.status];
          }),
        );
        for (const required of ['court', 'packet', 'ssn_statement', 'filing_method']) {
          expect(checklist.has(required)).toBe(true);
        }
        expect(checklist.get('court')).toBe('ready');
      });
    }
  });
});

describe('pipeline jobs', () => {
  offlineOnly(
    'queues real packet assembly on the deployed worker — a multi-megabyte packet per run',
  ).it('accepts a job and reports it queued; refuses an unknown kind', async () => {
    const admin = firmAdmin();
    const { caseId } = await scratchCase();
    const JOB: Spec = {
      id: 'string',
      kind: 'string',
      status: 'string',
      createdBy: 'string',
      attempts: 'integer',
      createdAt: 'string',
      updatedAt: 'string',
      'documentId?': 'string',
      'options?': 'object',
      'failure?': 'object',
      'result?': 'object',
    };
    const job = await admin.api.acceptCaseJob(caseId, 'packet_assembly', {
      outputOptions: { draftWatermark: true, printDate: false },
    });
    admin.wire(202, JOB);
    expect(job.status).toBe('queued');

    const read = await admin.api.getCaseJob(caseId, job.id);
    admin.wire(200, JOB);
    expect(read.id).toBe(job.id);

    await expectApiError(
      admin.api.acceptCaseJob(caseId, 'nonsense' as never),
      400,
      'ValidationError',
    );
    await expectApiError(admin.api.getCaseJob(caseId, 'no-such-job'), 404, 'NotFoundError');
  });
});
