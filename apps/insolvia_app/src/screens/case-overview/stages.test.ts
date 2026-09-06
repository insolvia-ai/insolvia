import type { Case, CaseProblem, Debtor } from '@insolvia-ai/api-client';

import { filingStages, stagesComplete } from './stages';
import type { StageInput } from './stages';

/**
 * The filing spine's derivation.
 *
 * A pure module with its own suite because that is the cheapest level that can
 * observe any of this — the alternative is asserting the same rules through a
 * rendered screen, five API mocks deep, where a failure names a missing string
 * rather than the rule that broke. The screen's own tests are left to assert
 * what only rendering can answer.
 */

const MATTER: Case = {
  id: '00000000-0000-4000-8000-0000000000c1',
  createdBy: '00000000-0000-4000-8000-000000000001',
  chapter: 7,
  district: 'NDCA',
  status: 'intake',
  createdAt: '2026-08-04T10:00:00.000000Z',
  updatedAt: '2026-08-04T10:00:00.000000Z',
};

function debtor(named: boolean): Debtor {
  return {
    id: 'debtor_1',
    case_id: MATTER.id,
    filing_role: 'debtor_1',
    created_at: MATTER.createdAt,
    updated_at: MATTER.createdAt,
    provenance: {},
    ...(named ? { name: { given: 'Sample', surname: 'Debtor' } } : {}),
  } as Debtor;
}

function problem(source: string, message = 'Something is missing.'): CaseProblem {
  return { source, message };
}

/** A case nobody has touched: everything unknown, nothing read. */
function fresh(over: Partial<StageInput> = {}): StageInput {
  return {
    matter: MATTER,
    debtors: [],
    documents: null,
    creditors: null,
    packets: null,
    pendingReview: null,
    problems: null,
    readyToFile: null,
    mayReview: true,
    ...over,
  };
}

function stage(input: StageInput, key: string) {
  const found = filingStages(input).find((candidate) => candidate.key === key);
  if (found === undefined) throw new Error(`no stage ${key}`);
  return found;
}

describe('the filing spine', () => {
  it('always opens with the case, and dates it from the case itself', () => {
    // The one stage with a real date on it. Every other date the mockups
    // imagined would have to be invented — nothing records when a stage
    // finished — so the others carry a word for their state instead.
    const opened = stage(fresh(), 'opened');

    expect(opened.state).toBe('done');
    expect(opened.meta).toBe('2026-08-04');
  });

  it('runs in filing order, so a blockage reads as "everything under this waits"', () => {
    // Ordering is the only reason a connected spine is honest rather than
    // decorative — see the module header.
    const keys = filingStages(fresh()).map((s) => s.key);

    expect(keys).toEqual(['opened', 'intake', 'documents', 'review', 'creditors', 'packet']);
  });

  it('omits the review stage entirely from a firm that cannot see the queue', () => {
    // Not greyed — absent. `extraction_review` defaults to hidden across a
    // firm, and listing a stage nobody there can open would say the case is
    // waiting on a screen that does not exist for them.
    const keys = filingStages(fresh({ mayReview: false })).map((s) => s.key);

    expect(keys).not.toContain('review');
  });

  it('separates "nothing has happened" from "something is wrong"', () => {
    // idle vs blocked. A case opened five minutes ago must not wear a red mark.
    expect(stage(fresh(), 'documents').state).toBe('idle');
    expect(stage(fresh({ problems: [problem('creditors')] }), 'creditors').state).toBe('blocked');
  });

  it('carries the gate’s own sentence onto the stage it belongs to', () => {
    // The mapping from `source` to stage is what turns "the gate refused" into
    // something a person can act on.
    const blocked = stage(
      fresh({ problems: [problem('claims', 'A claim needs an amount.')] }),
      'creditors',
    );

    expect(blocked.note).toBe('A claim needs an amount.');
    expect(blocked.meta).toBe('blocked');
  });

  it('does not blame intake for a creditor problem', () => {
    const stages = filingStages(fresh({ problems: [problem('claims')] }));

    expect(stages.find((s) => s.key === 'intake')?.state).not.toBe('blocked');
  });

  it('counts a debtor only once it has a name', () => {
    // A debtor record exists the moment intake is opened; it is the NAME that
    // means somebody filled it in.
    expect(stage(fresh({ debtors: [debtor(false)] }), 'intake').state).toBe('active');
    expect(stage(fresh({ debtors: [debtor(true)] }), 'intake').state).toBe('done');
  });

  it('shows the review queue as in progress exactly while somebody owes it work', () => {
    expect(stage(fresh({ pendingReview: 12 }), 'review').state).toBe('active');
    expect(stage(fresh({ pendingReview: 12 }), 'review').note).toContain('12 records');
    expect(stage(fresh({ pendingReview: 0 }), 'review').state).toBe('idle');
  });

  it('says a count it could not read is unknown, NOT that there is nothing', () => {
    // The distinction the nullable counts exist for. "Nothing uploaded yet"
    // tells somebody to go and upload; the request having failed tells them
    // nothing — and saying the first when the second is true is a lie the
    // screen cannot walk back.
    expect(stage(fresh({ documents: null }), 'documents').note).toBe('Could not be read');
    expect(stage(fresh({ creditors: null }), 'creditors').note).toBe('Could not be read');

    expect(stage(fresh({ documents: 0 }), 'documents').note).toBe('Nothing uploaded yet');
    expect(stage(fresh({ creditors: 0 }), 'creditors').note).toBe('No creditors yet');
  });

  it('only offers the packet as ready when the gate says the whole case is', () => {
    expect(stage(fresh({ readyToFile: false }), 'packet').state).toBe('idle');
    expect(stage(fresh({ readyToFile: true }), 'packet').state).toBe('active');
    expect(stage(fresh({ packets: 2, readyToFile: true }), 'packet').state).toBe('done');
  });

  it('adds a stage for assets or income ONLY once the gate complains about one', () => {
    // Every Chapter 7 has assets and income, but a spine that listed them from
    // the start would put two permanent grey rows on a case whose intake has
    // not begun. They appear when they mean something.
    expect(filingStages(fresh()).some((s) => s.key === 'assets')).toBe(false);

    const withAssets = filingStages(fresh({ problems: [problem('exemptions')] }));
    expect(withAssets.find((s) => s.key === 'assets')?.state).toBe('blocked');
    // …and still before the packet, which is what waits on it.
    expect(withAssets.map((s) => s.key).indexOf('assets')).toBeLessThan(
      withAssets.map((s) => s.key).indexOf('packet'),
    );
  });

  it('counts the stages that are actually behind us', () => {
    const done = filingStages(
      fresh({ debtors: [debtor(true)], documents: 3, creditors: 2, pendingReview: 0 }),
    );

    // opened + intake + documents + creditors. Review is idle (nothing waiting)
    // and the packet is not assembled.
    expect(stagesComplete(done)).toBe(4);
  });

  it('claims nothing is complete but the opening on a case it knows nothing about', () => {
    expect(stagesComplete(filingStages(fresh()))).toBe(1);
  });
});
