import type { Case, CaseProblem, Debtor } from '@insolvia-ai/api-client';

/**
 * Where a stage has got to.
 *
 * `blocked` and `idle` are different on purpose: blocked means the completeness
 * gate named a reason, and the stage can say what it is; idle means nothing is
 * wrong yet, the work upstream simply has not happened. Rendering them the same
 * would put a red mark on every case that was opened five minutes ago.
 */
export type StageState = 'done' | 'active' | 'blocked' | 'idle';

export interface Stage {
  readonly key: string;
  readonly label: string;
  readonly state: StageState;
  /** One line under the label: what has happened, or what is missing. */
  readonly note: string;
  /** The right-hand column — a date, or a word for the state. */
  readonly meta: string;
  /** The route segment that does this work, when there is one. */
  readonly segment?: string;
}

export interface StageInput {
  readonly matter: Case;
  readonly debtors: readonly Debtor[];
  readonly documents: number | null;
  readonly creditors: number | null;
  readonly packets: number | null;
  readonly pendingReview: number | null;
  /** Absent until the summary answers. */
  readonly problems: readonly CaseProblem[] | null;
  readonly readyToFile: boolean | null;
  /**
   * Whether the firm can see the extraction queue at all.
   *
   * The stage is OMITTED, not greyed, when it cannot: `extraction_review`
   * defaults to hidden across a firm, and a spine that listed a stage nobody
   * there can open would be telling them their case is waiting on a screen
   * that does not exist for them.
   */
  readonly mayReview: boolean;
}

/**
 * Which problem sources belong to which stage.
 *
 * `source` is a collection name, `case`/`debtors`, or a form series id. The
 * mapping is what turns "the gate refused" into "this stage is blocked, and
 * here is the sentence" — without it the spine could only ever say that
 * something, somewhere, was wrong.
 */
const STAGE_SOURCES: Readonly<Record<string, readonly string[]>> = {
  intake: ['debtors', 'petitions', 'case', 'prior_cases', 'related_cases'],
  creditors: ['creditors', 'claims', 'codebtors'],
  assets: ['assets', 'exemptions'],
  income: ['employments', 'income_summaries', 'pay_period_records', 'expenses', 'households'],
};

function blockers(problems: readonly CaseProblem[] | null, stage: string): readonly CaseProblem[] {
  if (problems === null) return [];
  const sources = STAGE_SOURCES[stage];
  if (sources === undefined) return [];
  return problems.filter((problem) => sources.includes(problem.source));
}

/**
 * A stage whose count could not be read.
 *
 * NOT the same as a count of zero, and the distinction is the whole reason
 * these are nullable: "No creditors yet" tells a paralegal to go and add some,
 * while the request having failed tells them nothing at all. Saying the first
 * when the second is true is a lie the screen has no way to walk back.
 */
const UNKNOWN = { state: 'idle' as const, note: 'Could not be read', meta: '—' };

/** A count as a phrase, or the em dash that means "we could not read it". */
function count(n: number | null, one: string, many: string, none: string): string {
  if (n === null) return '—';
  if (n === 0) return none;
  return `${n} ${n === 1 ? one : many}`;
}

/**
 * The filing spine: the case's work in the order it actually happens.
 *
 * **Derived entirely from what the API already answers** — the case, its
 * debtors, four counts, and the completeness gate's problem list. There is no
 * per-stage record on the server and this does not pretend there is: a stage's
 * `meta` carries a real date only where one exists (the case's own
 * `createdAt`), and everything else is a word for the state rather than an
 * invented timestamp.
 *
 * ORDER IS THE INFORMATION. A numbered or connected list is only honest when
 * the sequence means something, and here it does — this is filing order, so
 * "blocked" three rows down reads as "and everything under it waits".
 */
export function filingStages(input: StageInput): readonly Stage[] {
  const { matter, debtors, documents, creditors, packets, pendingReview, problems } = input;

  const named = debtors.filter((debtor) => debtor.name !== undefined);
  const intakeBlockers = blockers(problems, 'intake');
  const creditorBlockers = blockers(problems, 'creditors');
  const assetBlockers = blockers(problems, 'assets');
  const incomeBlockers = blockers(problems, 'income');

  const stages: Stage[] = [
    {
      key: 'opened',
      label: 'Case opened',
      state: 'done',
      note: `Chapter ${matter.chapter} · ${matter.district}`,
      meta: matter.createdAt.slice(0, 10),
    },
    {
      key: 'intake',
      label: 'Debtor intake',
      segment: 'intake',
      ...(named.length > 0 && intakeBlockers.length === 0
        ? {
            state: 'done' as const,
            note: count(named.length, 'debtor', 'debtors', 'nobody') + ' recorded',
            meta: 'complete',
          }
        : intakeBlockers.length > 0
          ? {
              state: 'blocked' as const,
              note: intakeBlockers[0]?.message ?? 'Something is missing.',
              meta: 'blocked',
            }
          : { state: 'active' as const, note: 'Not started', meta: 'to do' }),
    },
    {
      key: 'documents',
      label: 'Documents',
      segment: 'documents',
      ...(documents === null
        ? UNKNOWN
        : documents > 0
          ? {
              state: 'done' as const,
              note: count(documents, 'document', 'documents', 'none') + ' uploaded',
              meta: 'complete',
            }
          : { state: 'idle' as const, note: 'Nothing uploaded yet', meta: '—' }),
    },
    ...(input.mayReview
      ? [
          {
            key: 'review',
            label: 'Extraction review',
            segment: 'extraction-review',
            ...(pendingReview !== null && pendingReview > 0
              ? {
                  state: 'active' as const,
                  note: `${pendingReview} ${pendingReview === 1 ? 'record' : 'records'} still waiting on a human`,
                  meta: 'in progress',
                }
              : { state: 'idle' as const, note: 'Nothing waiting', meta: '—' }),
          },
        ]
      : []),
    {
      key: 'creditors',
      label: 'Creditors and claims',
      segment: 'creditor-matrix',
      ...(creditorBlockers.length > 0
        ? {
            state: 'blocked' as const,
            note: creditorBlockers[0]?.message ?? 'Something is missing.',
            meta: 'blocked',
          }
        : creditors === null
          ? UNKNOWN
          : creditors > 0
            ? {
                state: 'done' as const,
                note: count(creditors, 'creditor', 'creditors', 'none') + ' recorded',
                meta: 'complete',
              }
            : { state: 'idle' as const, note: 'No creditors yet', meta: '—' }),
    },
  ];

  // Only shown once the gate has actually complained about them — before that
  // they are noise on a case nobody has started.
  if (assetBlockers.length > 0) {
    stages.push({
      key: 'assets',
      label: 'Assets and exemptions',
      segment: 'intake',
      state: 'blocked',
      note: assetBlockers[0]?.message ?? 'Something is missing.',
      meta: 'blocked',
    });
  }
  if (incomeBlockers.length > 0) {
    stages.push({
      key: 'income',
      label: 'Income and expenses',
      segment: 'intake',
      state: 'blocked',
      note: incomeBlockers[0]?.message ?? 'Something is missing.',
      meta: 'blocked',
    });
  }

  stages.push({
    key: 'packet',
    label: 'Filing packet',
    segment: 'packet',
    ...(packets !== null && packets > 0
      ? {
          state: 'done' as const,
          note: count(packets, 'packet', 'packets', 'none') + ' assembled',
          meta: 'assembled',
        }
      : input.readyToFile === true
        ? { state: 'active' as const, note: 'Ready to assemble', meta: 'ready' }
        : { state: 'idle' as const, note: 'Assembles once the stages above clear', meta: '—' }),
  });

  return stages;
}

/** How many stages are behind us — the spine's own progress line. */
export function stagesComplete(stages: readonly Stage[]): number {
  return stages.filter((stage) => stage.state === 'done').length;
}
