import { permits } from '@insolvia-ai/api-client';
import type { FirmMembership, Task } from '@insolvia-ai/api-client';
import { Badge, Checkbox, Select } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type Scope = 'mine' | 'firm';

const SCOPE_OPTIONS = [
  { value: 'mine', label: 'Assigned to me' },
  { value: 'firm', label: 'Firm-wide' },
] as const;

/** How many rows the card shows before deferring to `/my-tasks`. */
const TASKS_SHOWN = 6;

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly tasks: readonly Task[] }
  | { readonly kind: 'error'; readonly message: string };

/**
 * The dashboard's tasks card (issue 14.7 / #359): `GET /v1/me/tasks`, overdue
 * first because the server already sorts by due date and an overdue task's
 * due date is the earliest one on the list. Complete inline — the same
 * `updateTask(caseId, id, {done})` call `/my-tasks` makes — and "See all"
 * expands to that screen, which stays the full worklist this card only
 * previews. The firm-wide/assigned-to-me toggle is `?scope=` on the same
 * endpoint (see `api/routes/tasks.py`), defaulting to "assigned to me".
 */
export function TasksCard({ membership }: { membership: FirmMembership }) {
  const theme = useTheme();
  const { call } = useApi();

  const [scope, setScope] = useState<Scope>('mine');
  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [status, setStatus] = useState('');

  const mayView = permits(membership.permissions.tasks, 'view_only');
  const mayChange = permits(membership.permissions.tasks, 'add_edit');

  const load = useCallback(async () => {
    setList({ kind: 'loading' });
    try {
      const result = await call((client) => client.listMyTasks({ scope }));
      if (result.ok) setList({ kind: 'ready', tasks: result.value });
    } catch {
      setList({ kind: 'error', message: 'Could not load tasks.' });
    }
  }, [call, scope]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  if (!mayView) return null;

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

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
              // A completed task drops off the worklist, the same reading a
              // fresh `GET /v1/me/tasks` would give — see /my-tasks.
              tasks: current.tasks.filter((t) => t.id !== result.value.id),
            }
          : current,
      );
      setStatus(done ? 'Marked done' : 'Reopened');
    } catch {
      setStatus('Could not update it. Try again.');
    }
  };

  const shown = list.kind === 'ready' ? list.tasks.slice(0, TASKS_SHOWN) : [];
  const remaining = list.kind === 'ready' ? list.tasks.length - shown.length : 0;

  return (
    <View
      style={[
        styles.card,
        {
          backgroundColor: theme.colors.card,
          borderColor: theme.colors.line,
          borderRadius: theme.radii.lg,
        },
      ]}
    >
      <View style={[styles.head, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body" style={styles.title}>
          Tasks
        </Heading>
        <Select
          aria-label="Whose tasks"
          options={[...SCOPE_OPTIONS]}
          value={scope}
          onValueChange={(next) => setScope(next as Scope)}
        />
      </View>

      <Text aria-live="polite" style={[styles.help, muted]}>
        {status}
      </Text>

      {list.kind !== 'ready' ? (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading' ? 'Loading tasks…' : list.message}
        </Text>
      ) : shown.length === 0 ? (
        <Text style={[styles.body, muted]}>
          {scope === 'mine' ? 'Nothing assigned to you right now.' : 'No open tasks in the firm.'}
        </Text>
      ) : (
        <View role="list" style={styles.list}>
          {shown.map((task) => (
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

      <Link
        href="/my-tasks"
        style={[styles.expand, { color: theme.colors.primary, fontFamily: theme.typography.body }]}
      >
        {remaining > 0 ? `See all your tasks (${remaining} more)` : 'See all your tasks'}
      </Link>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  card: { borderWidth: 1, padding: spacing.lg },
  expand: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    marginTop: spacing.sm,
    // 44dp, the WCAG 2.5.5 target size this app enforces.
    lineHeight: 44,
  },
  head: {
    alignItems: 'center',
    borderBottomWidth: 1,
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
    justifyContent: 'space-between',
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
  help: { fontSize: fontSizes.label, minHeight: fontSizes.label * 1.5 },
  list: { gap: spacing.md },
  row: {
    alignItems: 'flex-start',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.sm,
    paddingBottom: spacing.sm,
  },
  rowBody: { flex: 1, gap: spacing.xs, minWidth: 0 },
  rowLink: { fontSize: fontSizes.caption, textDecorationLine: 'underline' },
  rowMeta: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowMetaText: { fontSize: fontSizes.caption },
  rowSubject: { fontSize: fontSizes.body },
  title: { flexShrink: 0 },
});
