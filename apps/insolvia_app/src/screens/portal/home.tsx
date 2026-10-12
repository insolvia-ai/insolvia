import type { CaseStatus, PortalMe } from '@insolvia-ai/api-client';
import { ApiException } from '@insolvia-ai/api-client';
import { Button, Card } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { usePortalApi } from '@/api/use-portal-api';
import { Heading } from '@/components/heading';
import { PortalShell } from '@/components/portal-shell';
import { StatusScreen } from '@/components/status-screen';
import { usePortalSession } from '@/session';
import { fontSizes, spacing, useTheme } from '@/theme';

import { DocumentsNeeded } from './documents-needed';
import { QuestionnaireOverview } from './questionnaire-overview';

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly me: PortalMe }
  | { readonly kind: 'refused' }
  | { readonly kind: 'error' };

/**
 * What each stage means to the person the case is about — the firm-facing
 * status names (`intake` … `closed`, issue #355) are the firm's words.
 * Exhaustive over `CaseStatus`, so a new stage cannot ship unexplained.
 * `dismissed` says only what happened and points to the firm: what a
 * dismissal means for this person is the attorney's conversation, not a
 * status line's.
 */
const STAGES = {
  intake: {
    label: 'Preparing your case',
    detail: 'Your firm is gathering the information and documents your filing needs.',
  },
  ready_to_file: {
    label: 'Ready to file',
    detail: 'Your firm has prepared your filing and is getting it ready for the court.',
  },
  filed: {
    label: 'Filed',
    detail: 'Your case has been filed with the bankruptcy court.',
  },
  discharged: {
    label: 'Discharged',
    detail: 'The court has granted your discharge.',
  },
  dismissed: {
    label: 'Dismissed',
    detail: 'The court has dismissed your case. Talk to your law firm about what happens next.',
  },
  closed: {
    label: 'Closed',
    detail: 'The court has closed your case.',
  },
} as const satisfies Record<CaseStatus, { label: string; detail: string }>;

/**
 * `/portal` — the client's landing screen (ADR 0023 PR 2): which firm, and
 * where their case stands — its chapter and stage — **and nothing else.**
 *
 * That restraint is the ADR's read policy, not a placeholder. The answer
 * comes from `GET /v1/portal/me`, whose case block is a server-side
 * projection of exactly two attributes read through the client's binding; a
 * richer screen would need a richer projection, reviewed as one. The
 * questionnaire's sections, as the firm configured them, are their own read
 * (`QuestionnaireOverview`, #362); its questions and the document checklist
 * arrive with #363–#364.
 *
 * A **403** is its own state and not an error: it means the person signed in
 * fine and has no live access to a case — revoked, or never invited — and the
 * one useful instruction is the ADR's: talk to your law firm.
 */
export function PortalHome() {
  const theme = useTheme();
  const { call } = usePortalApi();
  const { signOut } = usePortalSession();
  const [state, setState] = useState<State>({ kind: 'loading' });

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const result = await call((client) => client.getPortalMe());
        if (live && result.ok) setState({ kind: 'ready', me: result.value });
      } catch (cause) {
        if (!live) return;
        setState(
          cause instanceof ApiException && cause.statusCode === 403
            ? { kind: 'refused' }
            : { kind: 'error' },
        );
      }
    })();
    return () => {
      live = false;
    };
  }, [call]);

  if (state.kind === 'loading') {
    return (
      <StatusScreen
        defer
        shell={PortalShell}
        title="Opening your portal"
        message="One moment while we find your case."
      />
    );
  }
  if (state.kind === 'refused') {
    return (
      <StatusScreen
        tone="error"
        shell={PortalShell}
        title="No case is linked to this sign-in"
        message={
          'You are signed in, but this account does not have access to a case in the client ' +
          'portal. If you expected one, please contact your law firm.'
        }
        actions={
          <Button size="lg" intent="secondary" onPress={signOut} style={styles.action}>
            Sign out
          </Button>
        }
      />
    );
  }
  if (state.kind === 'error') {
    return (
      <StatusScreen
        tone="error"
        shell={PortalShell}
        title="Your portal could not be opened"
        message="Insolvia could not be reached just now. Reload the page to try again."
      />
    );
  }

  const { me } = state;
  const stage = STAGES[me.case.stage];
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <PortalShell>
      <Heading level={1}>Welcome, {me.displayName}</Heading>
      <Text style={[styles.lede, muted]}>
        {me.firm.name} is preparing your bankruptcy case with Insolvia.
      </Text>

      <Card.Root style={styles.card}>
        <Heading level={2} size="section">
          Your case
        </Heading>
        <View style={styles.facts}>
          <Fact label="Law firm" value={me.firm.name} />
          <Fact label="Chapter" value={`Chapter ${String(me.case.chapter)}`} />
          <Fact label="Stage" value={stage.label} />
        </View>
        <Text style={[styles.detail, ink]}>{stage.detail}</Text>
      </Card.Root>

      <QuestionnaireOverview firmName={me.firm.name} />

      <DocumentsNeeded firmName={me.firm.name} />

      <Text style={[styles.note, muted]}>
        Questions about your case? Contact {me.firm.name} directly — they can see everything you
        share here.
      </Text>
    </PortalShell>
  );
}

/** One labelled value. A `Text` pair rather than a table: three facts, read top to bottom. */
function Fact({ label, value }: { label: string; value: string }) {
  const theme = useTheme();
  return (
    <View style={styles.fact}>
      <Text
        style={[styles.factLabel, { color: theme.colors.muted, fontFamily: theme.typography.body }]}
      >
        {label}
      </Text>
      <Text
        style={[styles.factValue, { color: theme.colors.ink, fontFamily: theme.typography.body }]}
      >
        {value}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  action: {
    alignSelf: 'flex-start',
    marginTop: spacing.md,
  },
  card: {
    gap: spacing.md,
    padding: spacing.lg,
  },
  detail: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  fact: {
    gap: spacing.xs,
    minWidth: 160,
  },
  factLabel: {
    fontSize: fontSizes.label,
  },
  factValue: {
    fontSize: fontSizes.body,
    fontWeight: '600',
  },
  facts: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.lg,
  },
  lede: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  note: {
    fontSize: fontSizes.label,
    lineHeight: fontSizes.label * 1.5,
  },
});
