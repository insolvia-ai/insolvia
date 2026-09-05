import { permits } from '@insolvia-ai/api-client';
import type { FirmColleague, InsolviaApiClient } from '@insolvia-ai/api-client';
import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { caseTitle, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * A count this screen shows, once it knows it.
 *
 * `null` is "not read yet or could not be read" and renders as an em dash
 * rather than as `0`. The difference matters on this screen more than most: a
 * case with no creditors and a case whose creditors failed to load look
 * identical if both say zero, and only one of them means "go and add some".
 */
type Count = number | null;

interface Counts {
  readonly documents: Count;
  readonly creditors: Count;
  readonly packets: Count;
  readonly people: Count;
  readonly pendingReview: Count;
}

const NOTHING: Counts = {
  documents: null,
  creditors: null,
  packets: null,
  people: null,
  pendingReview: null,
};

/** One row of the standing list: what the section is, and where it has got to. */
interface Standing {
  readonly segment: string;
  readonly label: string;
  readonly value: string;
  /** Draws attention — work is waiting on a person here. */
  readonly waiting?: boolean;
}

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** A count as a sentence, or the em dash that means "we do not know". */
function says(count: Count, one: string, many: string, none: string): string {
  if (count === null) return '—';
  if (count === 0) return none;
  return plural(count, one, many);
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
 * counts come from the same endpoints the sections themselves use. There is
 * deliberately no activity feed, no "schedules complete" fraction and no
 * unsecured-debt total, all of which would make a better-looking page: nothing
 * serves them, and inventing them on the client would mean a case overview that
 * disagrees with the case. They are worth adding to the API, not faking here.
 */
export function CaseOverview() {
  const theme = useTheme();
  const { caseId, matter, debtors } = useCase();
  const membership = useMembership();
  const { call } = useApi();

  const [counts, setCounts] = useState<Counts>(NOTHING);
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);

  // The extraction queue is the one gated read: the feature defaults to hidden
  // across the firm, so asking for it unconditionally would 403 for most users
  // and cost a request to learn what `permits` already knows. Same courtesy
  // rule as the rail — the API re-checks regardless.
  const mayReview =
    membership != null && permits(membership.permissions.extraction_review, 'view_only');

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
      const [documents, creditors, packets, people, pendingReview] = await Promise.all([
        read((client) => client.listDocuments(caseId)),
        read((client) => client.listCaseEntities(caseId, 'creditors')),
        read((client) => client.listCasePackets(caseId)),
        read((client) => client.listCaseAssignees(caseId)),
        mayReview
          ? read((client) => client.listExtractionCandidates(caseId, 'pending'))
          : Promise.resolve<Count>(null),
      ]);
      if (!live) return;
      setCounts({ documents, creditors, packets, people, pendingReview });

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
  }, [call, caseId, mayReview]);

  const openedBy =
    colleagues.find((colleague) => colleague.subject === matter.createdBy)?.displayName ??
    matter.createdBy;

  const standing: readonly Standing[] = [
    {
      segment: 'intake',
      label: 'Intake',
      value:
        debtors.length === 0
          ? 'Not started'
          : `${plural(debtors.length, 'debtor', 'debtors')} recorded`,
    },
    {
      segment: 'documents',
      label: 'Documents',
      value: says(counts.documents, 'document', 'documents', 'None uploaded'),
    },
    ...(mayReview
      ? [
          {
            segment: 'extraction-review',
            label: 'Extraction review',
            value: says(
              counts.pendingReview,
              'record waiting',
              'records waiting',
              'Nothing waiting',
            ),
            waiting: counts.pendingReview !== null && counts.pendingReview > 0,
          },
        ]
      : []),
    {
      segment: 'creditor-matrix',
      label: 'Creditor matrix',
      value: says(counts.creditors, 'creditor', 'creditors', 'No creditors yet'),
    },
    {
      segment: 'packet',
      label: 'Filing packet',
      value: says(counts.packets, 'packet assembled', 'packets assembled', 'Not assembled'),
    },
    {
      segment: 'team',
      label: 'Team',
      value: says(counts.people, 'person', 'people', 'Nobody assigned'),
    },
  ];

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <>
      <Heading level={1}>{caseTitle(matter, debtors)}</Heading>
      <Text style={[styles.body, muted]}>
        Chapter {matter.chapter} · {matter.district} · opened {matter.createdAt.slice(0, 10)} by{' '}
        {openedBy}
      </Text>

      <Heading level={2}>Where this case stands</Heading>
      <View role="list" style={styles.list}>
        {standing.map((row) => (
          <View role="listitem" key={row.segment} style={styles.row}>
            {/*
              A `Link`, not a pressable row: these render real `<a href>`s, which
              is what lets a paralegal keep the packet open in one tab while
              working the review queue in another. The accessible name carries
              the section AND its state, so "Documents, 9 documents" makes sense
              read out of context — WCAG 2.4.4 — while the visible label stays
              the start of that name for 2.5.3.
            */}
            <Link
              href={`/cases/${caseId}/${row.segment}`}
              aria-label={`${row.label} — ${row.value}`}
              style={[
                styles.rowLink,
                { color: theme.colors.primary, fontFamily: theme.typography.body },
              ]}
            >
              {row.label}
            </Link>
            <Text
              style={[
                styles.rowValue,
                {
                  color: row.waiting === true ? theme.colors.warning : theme.colors.muted,
                  fontFamily: theme.typography.body,
                },
              ]}
            >
              {row.value}
            </Text>
          </View>
        ))}
      </View>
    </>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  list: {
    gap: spacing.xs,
  },
  row: {
    alignItems: 'baseline',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    justifyContent: 'space-between',
  },
  rowLink: {
    fontSize: fontSizes.body,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size this app enforces on anything pressable.
    lineHeight: 44,
  },
  rowValue: {
    fontSize: fontSizes.label,
  },
});
