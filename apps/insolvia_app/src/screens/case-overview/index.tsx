import type { CaseSummary, FirmColleague, InsolviaApiClient } from '@insolvia-ai/api-client';
import { Badge, Button } from '@insolvia-ai/design-system';
import { Link, useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';

import { useApi } from '@/api/use-api';
import { caseTitle, chapterAndDistrict, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { contentMaxWidth, fontSizes, railBreakpoint, spacing, useTheme } from '@/theme';

import { filingStages, stagesComplete } from './stages';
import type { Stage, StageState } from './stages';

/**
 * A count this screen shows, once it knows it.
 *
 * `null` is "not read yet or could not be read" and renders as an em dash
 * rather than as `0`. The difference matters on this screen more than most: a
 * case with no creditors and a case whose creditors failed to load look
 * identical if both say zero, and only one of them means "go and add some".
 */
type Count = number | null;

/**
 * The counts THIS screen reads.
 *
 * The review queue is deliberately absent: the shell already reads it for the
 * rail's badge and publishes it through `useCase()`, and asking again here
 * would be two requests for one number. Documents are read here rather than
 * there because the rail does not show them — see `CaseCounts`.
 */
interface Counts {
  readonly documents: Count;
  readonly creditors: Count;
  readonly packets: Count;
  readonly people: Count;
}

const NOTHING: Counts = { documents: null, creditors: null, packets: null, people: null };

/** The aside's width — stat tiles and the figures, both of which want a set measure. */
const ASIDE_WIDTH = 300;

/** How many blockers the "needs a human" list shows before deferring. */
const PROBLEMS_SHOWN = 3;

/**
 * A problem's `source` as a human reads it.
 *
 * `source` is a collection name, `case`/`debtors`, or a form series id like
 * `form/b106d`. The form ids are the ones worth translating — `B106D` is what
 * the schedule is actually called, and what a paralegal would search for.
 */
export function sourceLabel(source: string): string {
  if (source.startsWith('form/')) return source.slice('form/'.length).toUpperCase();
  return source.replace(/_/g, ' ');
}

/**
 * A total for a stat tile: `8412.66` → `$8.4k`.
 *
 * PRESENTATION ONLY, and only ever narrowing. The exact figure is what the
 * schedules print and what the table below it shows; a tile is for the glance,
 * and `$74,182.10` at 24px in a 2×2 grid is a number nobody reads. Parsing is
 * tolerant because the value is a decimal STRING from the server (see
 * `CaseTotals`) and an unparseable one must not take the screen down.
 */
export function abbreviateMoney(value: string): string {
  const amount = Number.parseFloat(value);
  if (!Number.isFinite(amount)) return '—';
  if (Math.abs(amount) >= 1_000_000) return `$${(amount / 1_000_000).toFixed(1)}m`;
  if (Math.abs(amount) >= 1_000) return `$${(amount / 1_000).toFixed(1)}k`;
  return `$${Math.round(amount)}`;
}

/**
 * `/cases/<id>` — the case's own page.
 *
 * **This route did not exist.** `/cases/<id>` was a 404: there were six case
 * screens and nothing above them, so a case had no destination and the list had
 * to offer six links per row to reach any of it. This is what a row points at
 * now, and it answers the two questions somebody opens a case to ask — where
 * has this got to, and what is waiting on me.
 *
 * **Everything here is read from the API, and only what the API has.** The
 * spine is derived in `stages.ts` from the counts and the completeness gate's
 * own problem list; the money comes from `GET /v1/cases/{id}/summary`, which
 * computes it from the same functions the official schedules print from. There
 * is deliberately no activity feed and no "schedules complete" fraction — the
 * audit log is unreadable to this API by design and nothing serves the other,
 * and inventing either would mean an overview that disagrees with the case.
 */
export function CaseOverview() {
  const theme = useTheme();
  const router = useRouter();
  const { caseId, matter, debtors, counts: shellCounts, mayReview } = useCase();
  const { call } = useApi();
  const { width } = useWindowDimensions();

  const [counts, setCounts] = useState<Counts>(NOTHING);
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);
  // `null` while unread or unreadable — the same "we do not know" the counts
  // use, and for the same reason: a case that looks ready because its summary
  // failed to load is worse than one that says nothing.
  const [summary, setSummary] = useState<CaseSummary | null>(null);

  useEffect(() => {
    // Guards the state writes against a case the user navigated away from
    // mid-flight, which is easy to do from the rail.
    let live = true;

    /**
     * One count, read on its own and allowed to fail on its own.
     *
     * A page showing four counts and one em dash is useful; one that shows an
     * error because the packet list was briefly unhappy is not. Every read here
     * is a nicety next to the case itself, which the shell above has already
     * loaded — or this screen would not be rendering.
     */
    const read = async <T,>(
      request: (client: InsolviaApiClient) => Promise<readonly T[]>,
    ): Promise<Count> => {
      try {
        const result = await call(request);
        return result.ok ? result.value.length : null;
      } catch {
        return null;
      }
    };

    const loadAll = async () => {
      const [documents, creditors, packets, people] = await Promise.all([
        read((client) => client.listDocuments(caseId)),
        read((client) => client.listCaseEntities(caseId, 'creditors')),
        read((client) => client.listCasePackets(caseId)),
        read((client) => client.listCaseAssignees(caseId)),
      ]);
      if (!live) return;
      setCounts({ documents, creditors, packets, people });

      try {
        const answered = await call((client) => client.getCaseSummary(caseId));
        if (live && answered.ok) setSummary(answered.value);
      } catch {
        // Same trade as the counts. The spine still renders from what the
        // counts know, and simply claims nothing about readiness.
      }

      try {
        const directory = await call((client) => client.listFirmDirectory());
        if (live && directory.ok) setColleagues(directory.value);
      } catch {
        // A subject is a worse answer than a name and a much better one than an
        // error over a page that otherwise loaded — the same trade the case
        // list makes.
      }
    };

    void loadAll();
    return () => {
      live = false;
    };
  }, [call, caseId]);

  const openedBy =
    colleagues.find((colleague) => colleague.subject === matter.createdBy)?.displayName ??
    matter.createdBy;

  const stages = filingStages({
    matter,
    debtors,
    documents: counts.documents,
    creditors: counts.creditors,
    packets: counts.packets,
    pendingReview: shellCounts.pendingReview,
    problems: summary?.problems ?? null,
    readyToFile: summary?.readyToFile ?? null,
    mayReview,
  });

  const stacked = width < railBreakpoint;
  const waiting = shellCounts.pendingReview !== null && shellCounts.pendingReview > 0;

  return (
    <>
      {/* ── Identity ───────────────────────────────────────────────────── */}
      <View style={styles.head}>
        <Heading level={1}>{caseTitle(matter, debtors)}</Heading>
        <Text
          style={[styles.meta, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
        >
          {chapterAndDistrict(matter)} · opened {matter.createdAt.slice(0, 10)} by {openedBy}
        </Text>
        {waiting ? (
          <Link
            href={`/cases/${caseId}/extraction-review`}
            aria-label={`Review ${shellCounts.pendingReview} extracted records`}
            style={[
              styles.alert,
              { color: theme.colors.warning, fontFamily: theme.typography.body },
            ]}
          >
            {shellCounts.pendingReview} records waiting on review
          </Link>
        ) : null}
      </View>

      <View style={[styles.columns, stacked ? styles.columnsStacked : null]}>
        <View style={styles.main}>
          {/* ── The spine ───────────────────────────────────────────────── */}
          <Section
            title="Filing readiness"
            meta={
              summary === null
                ? 'checking…'
                : `${stagesComplete(stages)} of ${stages.length} stages complete`
            }
          >
            <View style={styles.spine}>
              {stages.map((stage, index) => (
                <StageRow key={stage.key} stage={stage} last={index === stages.length - 1} />
              ))}
            </View>
          </Section>

          {/* ── What the gate is waiting on ─────────────────────────────── */}
          {summary !== null && !summary.readyToFile ? (
            <Section title="Needs a human" meta="what the filing gate is waiting on">
              <View style={styles.stack}>
                {summary.problems.slice(0, PROBLEMS_SHOWN).map((problem, index) => (
                  <Blocker
                    key={`${problem.source}-${problem.field ?? index}`}
                    label={sourceLabel(problem.source)}
                    message={problem.message}
                  />
                ))}
              </View>
              {summary.problems.length > PROBLEMS_SHOWN ? (
                <Text
                  style={[
                    styles.note,
                    { color: theme.colors.muted, fontFamily: theme.typography.body },
                  ]}
                >
                  …and {summary.problems.length - PROBLEMS_SHOWN} more, listed in full on the filing
                  packet screen.
                </Text>
              ) : null}
              <View style={styles.actions}>
                {waiting ? (
                  <Button
                    size="lg"
                    onPress={() => {
                      router.push(`/cases/${caseId}/extraction-review`);
                    }}
                  >
                    Review {String(shellCounts.pendingReview)} records
                  </Button>
                ) : null}
                <Button
                  size="lg"
                  intent="secondary"
                  onPress={() => {
                    router.push(`/cases/${caseId}/packet`);
                  }}
                >
                  Filing packet
                </Button>
              </View>
            </Section>
          ) : null}
        </View>

        {/* ── At a glance ─────────────────────────────────────────────── */}
        <View style={stacked ? styles.asideStacked : styles.aside}>
          <Section title="At a glance">
            <View style={styles.tiles}>
              <Tile
                value={counts.creditors === null ? '—' : String(counts.creditors)}
                label="Creditors"
              />
              <Tile
                value={summary === null ? '—' : abbreviateMoney(summary.totals.liabilities)}
                label="Liabilities"
              />
              <Tile
                value={counts.documents === null ? '—' : String(counts.documents)}
                label="Documents"
              />
              <Tile
                value={counts.people === null ? '—' : String(counts.people)}
                label="On the case"
              />
            </View>
          </Section>

          <Section title="Assets and liabilities">
            <View style={styles.figures}>
              <Figure label="Assets" value={summary?.totals.assets} />
              <Figure label="Secured claims" value={summary?.totals.secured} />
              <Figure label="Priority unsecured" value={summary?.totals.priorityUnsecured} />
              <Figure label="Nonpriority unsecured" value={summary?.totals.nonpriorityUnsecured} />
              <Figure label="Total liabilities" value={summary?.totals.liabilities} emphasis />
            </View>
          </Section>
        </View>
      </View>
    </>
  );
}

/**
 * A titled block — the page's one grouping unit, and now a CARD.
 *
 * A hairline under a heading was enough while the palette had a saturated
 * ground doing the separating. It is not enough on warm neutrals: with no
 * colour between a block and the page, a rule on its own reads as a wireframe
 * rather than as an object. A white surface, a hairline border and the brand's
 * `lg` corner is what makes it a thing rather than a gap.
 */
function Section({ title, meta, children }: { title: string; meta?: string; children: ReactNode }) {
  const theme = useTheme();
  return (
    <View
      style={[
        styles.section,
        {
          backgroundColor: theme.colors.card,
          borderColor: theme.colors.line,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <View style={[styles.sectionHead, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body">
          {title}
        </Heading>
        {meta === undefined ? null : (
          <Text
            style={[
              styles.sectionMeta,
              { color: theme.colors.muted, fontFamily: theme.typography.body },
            ]}
          >
            {meta}
          </Text>
        )}
      </View>
      {children}
    </View>
  );
}

/**
 * One stage of the spine.
 *
 * The state is carried THREE ways — the marker's fill, the word in the right
 * column, and the sentence underneath — because colour alone is not a status.
 * A reader who cannot use the hue still gets "blocked" and the reason.
 */
function StageRow({ stage, last }: { stage: Stage; last: boolean }) {
  const theme = useTheme();
  const tone = stageTone(stage.state, theme.colors);

  return (
    <View style={styles.stage}>
      <View style={styles.stageRail}>
        <View
          style={[
            styles.stageMark,
            { borderColor: tone, backgroundColor: stage.state === 'done' ? tone : 'transparent' },
          ]}
        />
        {last ? null : <View style={[styles.stageLine, { backgroundColor: theme.colors.line }]} />}
      </View>

      <View style={styles.stageBody}>
        <Text
          style={[
            styles.stageLabel,
            {
              color: stage.state === 'idle' ? theme.colors.muted : theme.colors.ink,
              fontFamily: theme.typography.body,
            },
          ]}
        >
          {stage.label}
        </Text>
        <Text
          style={[
            styles.stageNote,
            { color: theme.colors.muted, fontFamily: theme.typography.body },
          ]}
        >
          {stage.note}
        </Text>
      </View>

      <Text style={[styles.stageMeta, { color: tone, fontFamily: theme.typography.mono }]}>
        {stage.meta}
      </Text>
    </View>
  );
}

function stageTone(state: StageState, colors: ReturnType<typeof useTheme>['colors']): string {
  if (state === 'done') return colors.success;
  if (state === 'active') return colors.primary;
  if (state === 'blocked') return colors.danger;
  return colors.muted;
}

/** One reason the gate refused, with the stripe that says it is a refusal. */
function Blocker({ label, message }: { label: string; message: string }) {
  const theme = useTheme();
  return (
    <View style={[styles.blocker, { borderLeftColor: theme.colors.danger }]}>
      <View style={styles.blockerTop}>
        <Badge intent="danger" size="sm">
          {label}
        </Badge>
      </View>
      <Text
        style={[
          styles.blockerText,
          { color: theme.colors.muted, fontFamily: theme.typography.body },
        ]}
      >
        {message}
      </Text>
    </View>
  );
}

/** One number, big, in a grid whose 1px gaps show the rule colour through. */
function Tile({ value, label }: { value: string; label: string }) {
  const theme = useTheme();
  return (
    <View style={styles.tile}>
      <Text
        style={[
          styles.tileValue,
          { color: theme.colors.ink, fontFamily: theme.typography.heading },
        ]}
      >
        {value}
      </Text>
      <Text
        style={[styles.tileLabel, { color: theme.colors.muted, fontFamily: theme.typography.mono }]}
      >
        {label}
      </Text>
    </View>
  );
}

/** One exact figure. Rendered as the server sent it — no arithmetic here. */
function Figure({
  label,
  value,
  emphasis = false,
}: {
  label: string;
  value?: string | undefined;
  emphasis?: boolean;
}) {
  const theme = useTheme();
  return (
    <View style={[styles.figure, { borderTopColor: theme.colors.line }]}>
      <Text
        style={[
          styles.figureLabel,
          {
            color: emphasis ? theme.colors.ink : theme.colors.muted,
            fontFamily: theme.typography.body,
          },
        ]}
      >
        {label}
      </Text>
      <Text
        style={[
          styles.figureValue,
          {
            color: theme.colors.ink,
            fontFamily: theme.typography.mono,
            fontWeight: emphasis ? '600' : '400',
          },
        ]}
      >
        {value ?? '—'}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.md,
  },
  alert: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size this app enforces on anything pressable.
    lineHeight: 44,
  },
  aside: {
    gap: spacing.lg,
    width: ASIDE_WIDTH,
  },
  asideStacked: {
    gap: spacing.lg,
    width: '100%',
  },
  blocker: {
    // Inside a card already, so this drops its own fill and border and keeps
    // only the severity stripe. A card within a card is two objects where
    // there is one.
    borderLeftWidth: 2,
    gap: spacing.xs,
    paddingBottom: spacing.sm,
    paddingLeft: spacing.md,
  },
  blockerText: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  blockerTop: {
    flexDirection: 'row',
  },
  columns: {
    flexDirection: 'row',
    gap: spacing.xl,
    // THE ONE SCREEN THAT ASKS FOR MORE than the default reading measure, and
    // it says so here rather than making the shell decide for all six. This is
    // exactly the two columns below — the capped spine plus the aside — so on
    // a wide display the pair stays together instead of drifting apart.
    maxWidth: contentMaxWidth + spacing.xl + ASIDE_WIDTH,
  },
  columnsStacked: {
    flexDirection: 'column',
  },
  figure: {
    borderTopWidth: 1,
    flexDirection: 'row',
    gap: spacing.md,
    justifyContent: 'space-between',
    paddingVertical: spacing.sm,
  },
  figureLabel: {
    fontSize: fontSizes.label,
  },
  figureValue: {
    fontSize: fontSizes.label,
    fontVariant: ['tabular-nums'],
  },
  figures: {
    marginTop: spacing.xs,
  },
  head: {
    gap: spacing.xs,
    marginBottom: spacing.lg,
  },
  main: {
    flex: 1,
    gap: spacing.lg,
    // CAPPED AT THE READING MEASURE, and that is what this column is: every row
    // in it puts a label at one edge and its state at the other — "Documents"
    // and "—", "Total liabilities" and its figure. Left to stretch on a wide
    // display those two ends drift a thousand pixels apart and stop reading as
    // one row. The workspace around it still uses the full frame; the tables on
    // the sibling screens want that width and this column does not.
    maxWidth: contentMaxWidth,
    minWidth: 0,
  },
  meta: {
    fontSize: fontSizes.label,
  },
  note: {
    fontSize: fontSizes.caption,
    marginTop: spacing.sm,
  },
  section: {
    borderWidth: 1,
    gap: spacing.sm,
    padding: spacing.lg,
  },
  sectionHead: {
    alignItems: 'baseline',
    borderBottomWidth: 1,
    flexDirection: 'row',
    // Wraps rather than crushing the meta against the title in the 300px
    // aside; on the wide main column both still sit on one line.
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    paddingBottom: spacing.xs,
  },
  sectionMeta: {
    fontSize: fontSizes.caption,
  },
  spine: {
    marginTop: spacing.xs,
  },
  stack: {
    gap: spacing.sm,
  },
  stage: {
    alignItems: 'flex-start',
    flexDirection: 'row',
    gap: spacing.sm,
  },
  stageBody: {
    flex: 1,
    gap: 1,
    minWidth: 0,
    paddingBottom: spacing.md,
  },
  stageLabel: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  stageLine: {
    bottom: 0,
    left: 5,
    position: 'absolute',
    top: 14,
    width: 1,
  },
  stageMark: {
    borderWidth: 1.5,
    height: 11,
    marginTop: 3,
    width: 11,
  },
  stageMeta: {
    fontSize: fontSizes.caption,
    paddingTop: 3,
  },
  stageNote: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  stageRail: {
    alignItems: 'center',
    alignSelf: 'stretch',
    width: 11,
  },
  tile: {
    flexBasis: '46%',
    flexGrow: 1,
    gap: 2,
    paddingVertical: spacing.sm,
  },
  tileLabel: {
    fontSize: 10,
    letterSpacing: 0.8,
    textTransform: 'uppercase',
  },
  tileValue: {
    fontSize: fontSizes.section,
    fontVariant: ['tabular-nums'],
    // 700 for the reason brand/fonts.json gives: Cormorant at anything less
    // reads as a hairline at this size.
    fontWeight: '700',
    letterSpacing: -0.5,
  },
  tiles: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
});
