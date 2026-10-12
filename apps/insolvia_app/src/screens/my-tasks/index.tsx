import { permits } from '@insolvia-ai/api-client';
import type { FirmMembership, Task } from '@insolvia-ai/api-client';
import { Badge, Checkbox } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly tasks: readonly Task[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * `/my-tasks` — every task assigned to the signed-in caller, across every
 * case in their firm they can reach (issue #356 / 14.4).
 *
 * **A PLAIN SCREEN, DELIBERATELY.** The dashboard (issue #359) will fold this
 * into a fuller "what's waiting on me" view later; until then this is its own
 * destination, reachable from the account screen, rather than something built
 * twice.
 *
 * `GET /v1/me/tasks` already sorts soonest-due-first and already refuses to
 * name a task from a case the caller cannot reach (ADR 0009) — this screen
 * renders exactly what it is given, the same "never re-derive a server rule"
 * discipline every other list screen in this app follows.
 */
export function MyTasks({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const { call } = useApi();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [status, setStatus] = useState('');

  const mayView = permits(membership.permissions.tasks, 'view_only');
  const mayChange = permits(membership.permissions.tasks, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listMyTasks());
      if (result.ok) setList({ kind: 'ready', tasks: result.value });
    } catch {
      setList({ kind: 'error', message: 'Could not load your tasks.' });
    }
  }, [call]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (!mayView) {
    return (
      <AppShell>
        <Heading level={1}>Your tasks</Heading>
        <Text style={[styles.body, muted]}>
          Your firm has not given you access to tasks. Ask one of your firm’s administrators if you
          need it.
        </Text>
      </AppShell>
    );
  }

  const toggleDone = async (task: Task, done: boolean) => {
    setStatus(done ? 'Completing…' : 'Reopening…');
    try {
      const result = await call((client) => client.updateTask(task.caseId, task.id, { done }));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setList((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              // A completed task drops out of the caller's own worklist —
              // the same reading GET /v1/me/tasks would give on a fresh
              // load, since it counts open work rather than a history.
              tasks: current.tasks.filter((t) => t.id !== result.value.id),
            }
          : current,
      );
      setStatus(done ? 'Marked done' : 'Reopened');
    } catch {
      setStatus('Could not update it. Try again.');
    }
  };

  return (
    <AppShell>
      <Heading level={1}>Your tasks</Heading>
      <Text style={[styles.body, muted]}>
        Assigned to you, across every case you can reach — soonest due first.
      </Text>

      <Text aria-live="polite" style={[styles.help, muted]}>
        {status}
      </Text>

      {list.kind !== 'ready' ? (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading' ? 'Loading your tasks…' : list.message}
        </Text>
      ) : list.tasks.length === 0 ? (
        <Text style={[styles.body, muted]}>Nothing assigned to you right now.</Text>
      ) : (
        <View role="list" style={styles.list}>
          {list.tasks.map((task) => (
            <View
              key={task.id}
              role="listitem"
              style={[styles.row, { borderColor: theme.colors.line }]}
            >
              <Checkbox.Root
                aria-label={`Mark ${task.subject} done`}
                checked={task.done}
                disabled={!mayChange}
                onCheckedChange={(next) => void toggleDone(task, next)}
              >
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>

              <View style={styles.rowBody}>
                <Text
                  style={[
                    styles.rowSubject,
                    { color: theme.colors.ink, fontFamily: theme.typography.body },
                  ]}
                >
                  {task.subject}
                </Text>
                <View style={styles.rowMeta}>
                  {task.dueDate !== undefined ? (
                    <Text style={[styles.rowMetaText, muted]}>Due {task.dueDate}</Text>
                  ) : null}
                  {task.formSeries !== undefined ? (
                    <Badge intent="neutral" size="sm">
                      {task.formSeries.toUpperCase()}
                    </Badge>
                  ) : null}
                  {task.overdue ? (
                    <Badge intent="danger" size="sm">
                      Overdue
                    </Badge>
                  ) : null}
                  <Link
                    href={`/cases/${task.caseId}`}
                    style={[
                      styles.rowLink,
                      { color: theme.colors.primary, fontFamily: theme.typography.body },
                    ]}
                  >
                    Open case
                  </Link>
                </View>
              </View>
            </View>
          ))}
        </View>
      )}
    </AppShell>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  help: { fontSize: fontSizes.label, minHeight: fontSizes.label * 1.5 },
  list: { gap: spacing.md, marginTop: spacing.sm },
  row: {
    alignItems: 'flex-start',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.sm,
    paddingBottom: spacing.sm,
  },
  rowBody: { flex: 1, gap: spacing.xs, minWidth: 0 },
  rowLink: {
    fontSize: fontSizes.caption,
    textDecorationLine: 'underline',
  },
  rowMeta: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowMetaText: { fontSize: fontSizes.caption },
  rowSubject: { fontSize: fontSizes.body },
});
