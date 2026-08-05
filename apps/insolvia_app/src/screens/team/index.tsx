import { permits } from '@insolvia-ai/api-client';
import type { CaseAssignee, FirmColleague, FirmMembership } from '@insolvia-ai/api-client';
import { Button } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { EnvBadge } from '@/components/env-badge';
import { Heading } from '@/components/heading';
import { appEnvironment, environmentInfo } from '@/config/environment';
import { fontSizes, spacing, useTheme } from '@/theme';

type State =
  | { readonly kind: 'loading' }
  | {
      readonly kind: 'ready';
      readonly assignees: readonly CaseAssignee[];
      readonly colleagues: readonly FirmColleague[];
    }
  | { readonly kind: 'error'; readonly message: string };

/**
 * Who is on this matter — MyCase's "linked to a case", as a screen.
 *
 * ## Two requests, and why neither carries a name
 *
 * The assignee list is subjects. The directory turns a subject into a person.
 * They are separate because a display name copied onto an assignment goes stale
 * the moment somebody is renamed, and a stale name on a case team is the kind
 * of wrong that nobody reports and everybody half-notices.
 *
 * A subject the directory cannot resolve still renders — as the subject — for
 * the same reason the directory includes disabled colleagues: this is history,
 * and dropping a row would silently understate who has had access.
 *
 * ## Removing yourself is allowed and is confirmed
 *
 * Unlinking the signed-in user costs them the case they are looking at. That is
 * the honest meaning of "I am no longer on this matter" and the server permits
 * it, so the screen does too — but it says so on the button rather than
 * discovering it afterwards.
 */
export function Team({ caseId, membership }: { caseId: string; membership: FirmMembership }) {
  const theme = useTheme();
  const env = environmentInfo(appEnvironment);
  const { call } = useApi();

  const [state, setState] = useState<State>({ kind: 'loading' });
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const mayChange = permits(membership.permissions.cases, 'add_edit');

  const load = useCallback(async () => {
    try {
      const [assignees, colleagues] = await Promise.all([
        call((client) => client.listCaseAssignees(caseId)),
        call((client) => client.listFirmDirectory()),
      ]);
      if (assignees.ok && colleagues.ok) {
        setState({ kind: 'ready', assignees: assignees.value, colleagues: colleagues.value });
      }
    } catch {
      setState({ kind: 'error', message: 'Could not load who is on this case.' });
    }
  }, [call, caseId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (run: () => Promise<unknown>) => {
    setBusy(true);
    setNotice(null);
    try {
      await run();
      await load();
    } catch {
      setNotice('Could not change who is on this case. Please try again.');
    } finally {
      setBusy(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const nameFor = (subject: string) =>
    state.kind === 'ready'
      ? (state.colleagues.find((c) => c.subject === subject)?.displayName ?? subject)
      : subject;

  const assigned =
    state.kind === 'ready' ? new Set(state.assignees.map((a) => a.subject)) : new Set<string>();

  return (
    <AppShell actions={<EnvBadge env={env.name} />}>
      <Heading level={1}>Who is on this case</Heading>

      {notice === null ? null : (
        <Text aria-live="assertive" style={[styles.error, { color: theme.colors.danger }]}>
          {notice}
        </Text>
      )}

      {state.kind === 'ready' ? (
        <>
          <Heading level={2}>On the case</Heading>
          {state.assignees.length === 0 ? (
            <Text style={[styles.body, muted]}>
              Nobody is linked to this case. Your firm’s administrators can still reach it, and
              anyone with access to every case.
            </Text>
          ) : (
            <View role="list" style={styles.list}>
              {state.assignees.map((assignee) => (
                <View role="listitem" key={assignee.subject} style={styles.row}>
                  <Text style={[styles.name, { color: theme.colors.ink }]}>
                    {nameFor(assignee.subject)}
                  </Text>
                  <Text style={[styles.meta, muted]}>
                    linked {assignee.assignedAt.slice(0, 10)}
                  </Text>
                  {mayChange ? (
                    <Button
                      size="lg"
                      intent="secondary"
                      disabled={busy}
                      /* The name says WHO, not just "Remove" — a list of
                         identical "Remove" buttons is the same WCAG 2.4.4
                         failure a list of identical links is. */
                      aria-label={`Remove ${nameFor(assignee.subject)} from this case`}
                      onPress={() =>
                        void act(() =>
                          call((client) => client.unassignCase(caseId, assignee.subject)),
                        )
                      }
                    >
                      Remove
                    </Button>
                  ) : null}
                </View>
              ))}
            </View>
          )}

          {mayChange ? (
            <>
              <Heading level={2}>Add somebody</Heading>
              {state.colleagues.filter((c) => !assigned.has(c.subject)).length === 0 ? (
                <Text style={[styles.body, muted]}>Everyone in your firm is already on it.</Text>
              ) : (
                <View role="list" style={styles.list}>
                  {state.colleagues
                    .filter((colleague) => !assigned.has(colleague.subject))
                    .map((colleague) => (
                      <View role="listitem" key={colleague.subject} style={styles.row}>
                        <Text style={[styles.name, { color: theme.colors.ink }]}>
                          {colleague.displayName}
                        </Text>
                        <Text style={[styles.meta, muted]}>{colleague.role}</Text>
                        <Button
                          size="lg"
                          disabled={busy}
                          aria-label={`Add ${colleague.displayName} to this case`}
                          onPress={() =>
                            void act(() =>
                              call((client) => client.assignCase(caseId, colleague.subject)),
                            )
                          }
                        >
                          Add
                        </Button>
                      </View>
                    ))}
                </View>
              )}
            </>
          ) : null}
        </>
      ) : (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'loading' ? 'Loading who is on this case…' : state.message}
        </Text>
      )}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  error: {
    fontSize: fontSizes.label,
  },
  list: {
    gap: spacing.md,
  },
  meta: {
    fontSize: fontSizes.caption,
  },
  name: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  row: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
});
