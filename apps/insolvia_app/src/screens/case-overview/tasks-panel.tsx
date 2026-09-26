import { ApiValidationException, permits } from '@insolvia-ai/api-client';
import type { FirmColleague, Task } from '@insolvia-ai/api-client';
import {
  Badge,
  Button,
  Checkbox,
  DateInput,
  Field,
  Input,
  Select,
  Textarea,
} from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly tasks: readonly Task[] }
  | { readonly kind: 'error'; readonly message: string };

type Mode =
  | { readonly kind: 'list' }
  | { readonly kind: 'form'; readonly id: string | null; readonly draft: DraftState };

interface DraftState {
  readonly subject: string;
  readonly description: string;
  readonly dueDate: string;
  readonly assigneeSubject: string;
  readonly formSeries: string;
}

const EMPTY_DRAFT: DraftState = {
  subject: '',
  description: '',
  dueDate: '',
  assigneeSubject: '',
  formSeries: '',
};

function draftFrom(task: Task): DraftState {
  return {
    subject: task.subject,
    description: task.description ?? '',
    dueDate: task.dueDate ?? '',
    assigneeSubject: task.assigneeSubject ?? '',
    formSeries: task.formSeries ?? '',
  };
}

function undefinedIfBlank(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed === '' ? undefined : trimmed;
}

/**
 * The tasks panel on the case overview (issue #356 / 14.4): list, add, edit,
 * complete and reassign, all through this one section. "Reassign" is not a
 * separate control — it is the assignee `Select` inside the same edit form
 * every other field goes through, the same shape `screens/firm/creditors`
 * uses for its one add/edit form.
 *
 * Gated on the `tasks` feature, independently of `cases`/`intake` — a staff
 * member chasing paperwork gets `add_edit` on tasks by default
 * (`insolvia_core.firms.default_permissions`) without needing intake access.
 * Renders nothing at all when the caller cannot even view tasks, the same
 * "omit rather than explain" choice the rail's badges make — this is one
 * section among several on a page that is otherwise about filing readiness,
 * not a page of its own that owes an access explanation.
 */
export function TasksPanel({ caseId }: { caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();
  const membership = useMembership();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);
  const [mode, setMode] = useState<Mode>({ kind: 'list' });
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  const mayView =
    membership !== null &&
    membership !== undefined &&
    permits(membership.permissions.tasks, 'view_only');
  const mayChange =
    membership !== null &&
    membership !== undefined &&
    permits(membership.permissions.tasks, 'add_edit');

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listTasks(caseId));
      if (result.ok) setList({ kind: 'ready', tasks: result.value });
    } catch {
      setList({ kind: 'error', message: 'Could not load this case’s tasks.' });
    }
    try {
      const directory = await call((client) => client.listFirmDirectory());
      if (directory.ok) setColleagues(directory.value);
    } catch {
      // A subject is a worse answer than a name and a much better one than an
      // error over a panel that otherwise loaded.
    }
  }, [call, caseId]);

  useEffect(() => {
    if (mayView) void load();
  }, [load, mayView]);

  if (!mayView) return null;

  const nameFor = (subject: string): string =>
    colleagues.find((c) => c.subject === subject)?.displayName ?? subject;

  const persist = async (form: Mode & { readonly kind: 'form' }) => {
    setSaving(true);
    setStatus('Saving…');
    setErrors({});
    try {
      const { draft } = form;
      const result = await call((client) =>
        form.id === null
          ? client.createTask(caseId, {
              subject: draft.subject,
              description: undefinedIfBlank(draft.description),
              dueDate: undefinedIfBlank(draft.dueDate),
              assigneeSubject: undefinedIfBlank(draft.assigneeSubject),
              formSeries: undefinedIfBlank(draft.formSeries),
            })
          : client.updateTask(caseId, form.id, {
              subject: draft.subject,
              description: undefinedIfBlank(draft.description) ?? null,
              dueDate: undefinedIfBlank(draft.dueDate) ?? null,
              assigneeSubject: undefinedIfBlank(draft.assigneeSubject) ?? null,
              formSeries: undefinedIfBlank(draft.formSeries) ?? null,
            }),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setList((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              tasks:
                form.id === null
                  ? [...current.tasks, result.value]
                  : current.tasks.map((t) => (t.id === result.value.id ? result.value : t)),
            }
          : current,
      );
      setStatus('Saved');
      setMode({ kind: 'list' });
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setStatus('Some answers need attention.');
      } else {
        setStatus('Could not save. Your entries are still here — try again.');
      }
    } finally {
      setSaving(false);
    }
  };

  const toggleDone = async (task: Task, done: boolean) => {
    setStatus(done ? 'Completing…' : 'Reopening…');
    try {
      const result = await call((client) => client.updateTask(caseId, task.id, { done }));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setList((current) =>
        current.kind === 'ready'
          ? {
              kind: 'ready',
              tasks: current.tasks.map((t) => (t.id === result.value.id ? result.value : t)),
            }
          : current,
      );
      setStatus(done ? 'Marked done' : 'Reopened');
    } catch {
      setStatus('Could not update it. Try again.');
    }
  };

  const remove = async (id: string) => {
    setStatus('Removing…');
    try {
      const result = await call((client) => client.deleteTask(caseId, id));
      if (!result.ok) {
        setStatus('');
        return;
      }
      setList((current) =>
        current.kind === 'ready'
          ? { kind: 'ready', tasks: current.tasks.filter((t) => t.id !== id) }
          : current,
      );
      setStatus('Removed');
    } catch {
      setStatus('Could not remove it. Try again.');
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

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
      <View style={[styles.head, { borderBottomColor: theme.colors.line }]}>
        <Heading level={2} size="body" style={styles.title}>
          Tasks
        </Heading>
      </View>

      <Text
        aria-live={status === 'Some answers need attention.' ? 'assertive' : 'polite'}
        style={[styles.help, muted]}
      >
        {status}
      </Text>

      {mode.kind === 'list' ? (
        <View style={styles.list}>
          {list.kind !== 'ready' ? (
            <Text
              aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
              style={[styles.body, muted]}
            >
              {list.kind === 'loading' ? 'Loading tasks…' : list.message}
            </Text>
          ) : list.tasks.length === 0 ? (
            <Text style={[styles.body, muted]}>No tasks yet.</Text>
          ) : (
            <View role="list" style={styles.list}>
              {list.tasks.map((task) => (
                <View
                  key={task.id}
                  role="listitem"
                  style={[styles.row, { borderColor: theme.colors.line }]}
                >
                  <Checkbox.Root
                    aria-label={task.done ? `Reopen ${task.subject}` : `Mark ${task.subject} done`}
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
                        task.done ? styles.rowSubjectDone : null,
                      ]}
                    >
                      {task.subject}
                    </Text>
                    <View style={styles.rowMeta}>
                      {task.dueDate !== undefined ? (
                        <Text style={[styles.rowMetaText, muted]}>Due {task.dueDate}</Text>
                      ) : null}
                      {task.assigneeSubject !== undefined ? (
                        <Text style={[styles.rowMetaText, muted]}>
                          {nameFor(task.assigneeSubject)}
                        </Text>
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
                    </View>
                  </View>

                  {mayChange ? (
                    <View style={styles.rowActions}>
                      <Button
                        size="lg"
                        intent="secondary"
                        aria-label={`Edit ${task.subject}`}
                        onPress={() => {
                          setErrors({});
                          setStatus('');
                          setMode({ kind: 'form', id: task.id, draft: draftFrom(task) });
                        }}
                      >
                        Edit
                      </Button>
                      <Button
                        size="lg"
                        intent="secondary"
                        aria-label={`Remove ${task.subject}`}
                        onPress={() => void remove(task.id)}
                      >
                        Remove
                      </Button>
                    </View>
                  ) : null}
                </View>
              ))}
            </View>
          )}
          {mayChange ? (
            <Button
              size="lg"
              onPress={() => {
                setErrors({});
                setStatus('');
                setMode({ kind: 'form', id: null, draft: EMPTY_DRAFT });
              }}
            >
              Add task
            </Button>
          ) : null}
        </View>
      ) : (
        <TaskForm
          mode={mode}
          colleagues={colleagues}
          saving={saving}
          errors={errors}
          onChange={(draft) => setMode({ ...mode, draft })}
          onSave={() => void persist(mode)}
          onCancel={() => {
            setErrors({});
            setStatus('');
            setMode({ kind: 'list' });
          }}
        />
      )}
    </View>
  );
}

function TaskForm({
  mode,
  colleagues,
  saving,
  errors,
  onChange,
  onSave,
  onCancel,
}: {
  mode: Mode & { readonly kind: 'form' };
  colleagues: readonly FirmColleague[];
  saving: boolean;
  errors: Readonly<Record<string, string>>;
  onChange: (draft: DraftState) => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  const { draft } = mode;
  const set = <K extends keyof DraftState>(key: K, value: DraftState[K]) =>
    onChange({ ...draft, [key]: value });

  const assigneeOptions = [
    { value: '', label: 'Unassigned' },
    ...colleagues.map((c) => ({ value: c.subject, label: c.displayName })),
  ];

  return (
    <View style={styles.form}>
      <Field.Root name="subject" invalid={Boolean(errors.subject)}>
        <Field.Label>Subject</Field.Label>
        <Input
          value={draft.subject}
          onValueChange={(next) => set('subject', next)}
          autoCorrect={false}
        />
        {errors.subject ? <Field.Error match>{errors.subject}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="description" invalid={Boolean(errors.description)}>
        <Field.Label>Description</Field.Label>
        <Textarea value={draft.description} onValueChange={(next) => set('description', next)} />
        {errors.description ? <Field.Error match>{errors.description}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="dueDate" invalid={Boolean(errors.dueDate)}>
        <Field.Label>Due date</Field.Label>
        <DateInput
          value={draft.dueDate}
          onValueChange={(next, status) => {
            if (status === 'incomplete') return;
            set('dueDate', next);
          }}
        />
        {errors.dueDate ? <Field.Error match>{errors.dueDate}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="assigneeSubject" invalid={Boolean(errors.assigneeSubject)}>
        <Field.Label>Assigned to</Field.Label>
        <Select
          options={assigneeOptions}
          value={draft.assigneeSubject}
          onValueChange={(next) => set('assigneeSubject', next)}
        />
        {errors.assigneeSubject ? <Field.Error match>{errors.assigneeSubject}</Field.Error> : null}
      </Field.Root>

      <Field.Root name="formSeries" invalid={Boolean(errors.formSeries)}>
        <Field.Label>Form (optional)</Field.Label>
        <Input
          value={draft.formSeries}
          onValueChange={(next) => set('formSeries', next.toLowerCase())}
          autoCorrect={false}
          autoCapitalize="none"
        />
        <Field.Description>
          A short form key, like “b106d” — leave blank if this task is not about one specific form.
        </Field.Description>
        {errors.formSeries ? <Field.Error match>{errors.formSeries}</Field.Error> : null}
      </Field.Root>

      <View style={styles.rowActions}>
        <Button size="lg" disabled={saving} onPress={onSave}>
          {mode.id === null ? 'Add task' : 'Save changes'}
        </Button>
        <Button size="lg" intent="secondary" onPress={onCancel}>
          Cancel
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  form: { gap: spacing.md, marginTop: spacing.sm },
  head: {
    borderBottomWidth: 1,
    marginBottom: spacing.md,
    paddingBottom: spacing.md,
  },
  help: { fontSize: fontSizes.label, marginBottom: spacing.sm },
  list: { gap: spacing.md },
  row: {
    alignItems: 'flex-start',
    borderBottomWidth: 1,
    flexDirection: 'row',
    gap: spacing.sm,
    paddingBottom: spacing.sm,
  },
  rowActions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowBody: { flex: 1, gap: spacing.xs, minWidth: 0 },
  rowMeta: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  rowMetaText: { fontSize: fontSizes.caption },
  rowSubject: { fontSize: fontSizes.body },
  rowSubjectDone: { textDecorationLine: 'line-through' },
  section: { borderWidth: 1, marginTop: spacing.lg, padding: spacing.lg },
  title: { flexShrink: 0 },
});
