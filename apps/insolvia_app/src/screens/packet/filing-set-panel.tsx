import type {
  ChecklistItemStatus,
  FilingDocument,
  FilingSet,
  FilingSetCheckOutcome,
} from '@insolvia-ai/api-client';
import { Badge } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly filingSet: FilingSet }
  | { readonly kind: 'error' };

/** The checklist item a filed case's filing set consists of. */
const FILED_ITEM = 'filed';

const STATUS_INTENT: Record<ChecklistItemStatus, BadgeIntent> = {
  ready: 'success',
  missing: 'danger',
  action: 'primary',
  confirm: 'warning',
};

const STATUS_LABEL: Record<ChecklistItemStatus, string> = {
  ready: 'Ready',
  missing: 'Missing',
  action: 'To do',
  confirm: 'Confirm',
};

const OUTCOME_INTENT: Record<FilingSetCheckOutcome, BadgeIntent> = {
  pass: 'success',
  fail: 'danger',
  warn: 'warning',
  unmeasured: 'neutral',
};

const CHECK_LABEL: Record<string, string> = {
  size: 'Size',
  text_layer: 'Text layer',
  page_size: 'Page size',
};

function handlingLabel(document: FilingDocument): string | null {
  switch (document.handling) {
    case 'own_event':
      return 'Own docket event';
    case 'restricted':
      return 'Restricted — not on the public docket';
    case 'not_filed':
      return 'Not filed in this court';
    default:
      return document.source === 'outside' ? 'Prepared outside Insolvia' : null;
  }
}

/**
 * The filing set and hand-off checklist (ADR 0024 build PR 3) — what the
 * attorney files from their own CM/ECF session: the documents in the court's
 * docket order under the names to upload them as, each file's check results,
 * and a checklist built from the case record. Every court is hand-off until
 * its filing driver is verified, and every hand-back lands here.
 *
 * `reloadKey` changes when a packet is assembled, so the checks re-read the
 * new packet's measurements.
 */
export function FilingSetPanel({
  caseId,
  reloadKey,
}: {
  readonly caseId: string;
  readonly reloadKey: number;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.getCaseFilingSet(caseId));
      if (result.ok) {
        setState({ kind: 'ready', filingSet: result.value });
      }
    } catch {
      setState({ kind: 'error' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load, reloadKey]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const link = { color: theme.colors.primary, fontFamily: theme.typography.body };

  if (state.kind !== 'ready') {
    return (
      <View>
        <Heading level={2}>Filing set and checklist</Heading>
        <Text style={[styles.body, muted]}>
          {state.kind === 'loading'
            ? 'Loading the filing set…'
            : 'Could not load the filing set. Reload the page to try again.'}
        </Text>
      </View>
    );
  }

  const { filingSet } = state;
  const courtName = filingSet.court?.name ?? 'No court set';
  const court = `${courtName}${
    filingSet.court?.divisionName !== undefined ? `, ${filingSet.court.divisionName}` : ''
  }`;
  // A filed case's checklist is its one `filed` item (core/filing_set.py):
  // the case is on the docket, so nothing below is a step to take any more.
  const filed = filingSet.checklist.some((item) => item.id === FILED_ITEM);

  return (
    <View>
      <Heading level={2}>Filing set and checklist</Heading>
      <Text style={[styles.body, muted]}>
        {filed
          ? `${court}. This case is filed: the documents below are the record of what was prepared for the court, and no further filing is possible.`
          : `${court}. Automated filing is not available for this court yet: the attorney files from their own CM/ECF session, uploading the documents below in this order and under these names.`}
      </Text>
      {filingSet.orderBasis === 'default' ? (
        <Text style={[styles.body, muted]}>
          This court’s docket order and file names are not on record, so the packet’s own are used.
        </Text>
      ) : null}

      <Heading level={3}>Documents</Heading>
      <View role="list" style={styles.list}>
        {filingSet.documents.map((document, index) => {
          const handling = handlingLabel(document);
          return (
            <View role="listitem" key={document.key} style={styles.row}>
              <Text style={[styles.rowTitle, ink]}>{`${index + 1}. ${document.fileName}`}</Text>
              <Text style={[styles.body, muted]}>{document.title}</Text>
              {handling !== null ? (
                <View style={styles.badges}>
                  <Badge intent={document.handling === 'file' ? 'neutral' : 'warning'} size="sm">
                    {handling}
                  </Badge>
                </View>
              ) : null}
              {document.note !== undefined ? (
                <Text style={[styles.body, muted]}>{document.note}</Text>
              ) : null}
              <View role="list" style={styles.checks}>
                {document.checks.map((check) => (
                  <View role="listitem" key={check.check} style={styles.check}>
                    <Badge intent={OUTCOME_INTENT[check.outcome]} size="sm">
                      {`${CHECK_LABEL[check.check] ?? check.check}: ${check.outcome}`}
                    </Badge>
                    <Text style={[styles.checkText, muted]}>{check.message}</Text>
                  </View>
                ))}
              </View>
            </View>
          );
        })}
      </View>

      <Heading level={3}>Checklist</Heading>
      <View role="list" style={styles.list}>
        {filingSet.checklist.map((item) => (
          <View role="listitem" key={item.id} style={styles.row}>
            <View style={styles.badges}>
              <Badge intent={STATUS_INTENT[item.status]} size="sm">
                {item.id === FILED_ITEM ? 'Filed' : STATUS_LABEL[item.status]}
              </Badge>
              <Text style={[styles.rowTitle, ink]}>{item.title}</Text>
            </View>
            <Text style={[styles.body, muted]}>{item.detail}</Text>
            {item.link !== undefined ? (
              <Link
                href={item.link === '' ? `/cases/${caseId}` : `/cases/${caseId}/${item.link}`}
                aria-label={`Go to fix: ${item.title}`}
                style={[styles.body, link]}
              >
                Go there
              </Link>
            ) : null}
          </View>
        ))}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  badges: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  check: {
    alignItems: 'flex-start',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.xs,
  },
  checkText: {
    flexShrink: 1,
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
  checks: {
    gap: spacing.xs / 2,
    marginTop: spacing.xs / 2,
  },
  list: {
    gap: spacing.md,
    marginTop: spacing.sm,
  },
  row: {
    gap: spacing.xs / 2,
  },
  rowTitle: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
});
