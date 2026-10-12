import type {
  CandidateStatus,
  ClientRole,
  PortalAnswer,
  PortalMe,
  PortalQuestion,
  PortalQuestionInput,
  PortalQuestionnaire,
} from '@insolvia-ai/api-client';
import { ApiException, ApiValidationException } from '@insolvia-ai/api-client';
import {
  Badge,
  Button,
  Card,
  DateInput,
  Field,
  Input,
  Select,
  Textarea,
} from '@insolvia-ai/design-system';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { usePortalApi } from '@/api/use-portal-api';
import { Heading } from '@/components/heading';
import { PortalShell } from '@/components/portal-shell';
import { StatusScreen } from '@/components/status-screen';
import { fontSizes, spacing, useTheme } from '@/theme';

import {
  ADDRESS_MEMBERS,
  type AddressDraft,
  type AnswerDraft,
  draftFrom,
  emptyDraft,
  summary,
  valueFrom,
} from './answer-form';

type Loaded = {
  readonly me: PortalMe;
  readonly questionnaire: PortalQuestionnaire;
  readonly answers: readonly PortalAnswer[];
};

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly data: Loaded }
  | { readonly kind: 'error' };

/** What each review state means to the person who answered. */
const STATUS = {
  pending: { label: 'Waiting for review', intent: 'warning' },
  accepted: { label: 'Accepted', intent: 'success' },
  corrected: { label: 'Accepted with changes', intent: 'success' },
  rejected: { label: 'Not used', intent: 'neutral' },
  withdrawn: { label: 'Withdrawn', intent: 'neutral' },
} as const satisfies Record<
  CandidateStatus,
  { label: string; intent: 'warning' | 'success' | 'neutral' }
>;

const ROLE_LABEL = { debtor_1: 'Debtor 1', debtor_2: 'Debtor 2' } as const satisfies Record<
  ClientRole,
  string
>;

/**
 * `/portal/questionnaire` — the client's questionnaire, one section at a
 * time (ADR 0023 PR 4). The section is in the URL (`?section=`), so a
 * reload or a link lands where the client was.
 *
 * **Resumable without a draft store.** Every answer is saved the moment the
 * client presses Save, as a candidate in the firm's review queue; reopening
 * the questionnaire reads them back from `GET /v1/portal/answers`. Nothing
 * here is case data, and the screen says so: an answer is "waiting for
 * review" until someone at the firm accepts it. While it waits it can be
 * changed or withdrawn; once reviewed it is the firm's, and a change is a
 * new answer.
 *
 * Only the sections the firm shows arrive, so there is nothing to hide.
 */
export function PortalQuestionnaireScreen() {
  const { call } = usePortalApi();
  const router = useRouter();
  const params = useLocalSearchParams<{ section?: string }>();
  const [state, setState] = useState<State>({ kind: 'loading' });

  const load = useCallback(async () => {
    try {
      const [me, questionnaire, answers] = await Promise.all([
        call((client) => client.getPortalMe()),
        call((client) => client.getPortalQuestionnaire()),
        call((client) => client.listPortalAnswers()),
      ]);
      if (me.ok && questionnaire.ok && answers.ok) {
        setState({
          kind: 'ready',
          data: { me: me.value, questionnaire: questionnaire.value, answers: answers.value },
        });
      }
    } catch {
      setState({ kind: 'error' });
    }
  }, [call]);

  useEffect(() => {
    void load();
  }, [load]);

  if (state.kind === 'loading') {
    return (
      <StatusScreen
        defer
        shell={PortalShell}
        title="Opening your questionnaire"
        message="One moment while we load your answers."
      />
    );
  }
  if (state.kind === 'error') {
    return (
      <StatusScreen
        tone="error"
        shell={PortalShell}
        title="Your questionnaire could not be opened"
        message="Insolvia could not be reached just now. Reload the page to try again."
      />
    );
  }

  const { me, questionnaire, answers } = state.data;
  const sections = questionnaire.sections;
  const index = Math.max(
    0,
    sections.findIndex((section) => section.id === params.section),
  );
  const section = sections[index];
  if (section === undefined) {
    return (
      <StatusScreen
        shell={PortalShell}
        title="Nothing to answer"
        message={`${me.firm.name} has not asked you any questions here.`}
      />
    );
  }
  const goTo = (target: number) => {
    const next = sections[target];
    if (next !== undefined) router.setParams({ section: next.id });
  };

  return (
    <PortalShell>
      <InkText muted>
        Section {index + 1} of {sections.length}
      </InkText>
      <Heading level={1}>{section.title}</Heading>
      <InkText muted>{section.instructions}</InkText>
      <InkText muted>
        Each answer is saved when you press Save, and {me.firm.name} reviews it before it goes into
        your case. You can come back and finish later.
      </InkText>

      {section.questions.map((question) => (
        <QuestionCard
          key={question.id}
          question={question}
          roles={me.roles}
          answers={answers.filter(
            (answer) => answer.questionId === question.id && answer.status !== 'withdrawn',
          )}
          onChanged={() => void load()}
        />
      ))}

      <View style={styles.nav}>
        {index > 0 ? (
          <Button size="lg" intent="secondary" onPress={() => goTo(index - 1)}>
            Previous section
          </Button>
        ) : null}
        {index < sections.length - 1 ? (
          <Button size="lg" onPress={() => goTo(index + 1)}>
            Next section
          </Button>
        ) : (
          <Button size="lg" onPress={() => router.push('/portal')}>
            Back to your portal
          </Button>
        )}
      </View>
    </PortalShell>
  );
}

function InkText({ children, muted = false }: { children: React.ReactNode; muted?: boolean }) {
  const theme = useTheme();
  return (
    <Text
      style={[
        styles.body,
        {
          color: muted ? theme.colors.muted : theme.colors.ink,
          fontFamily: theme.typography.body,
        },
      ]}
    >
      {children}
    </Text>
  );
}

type Editing = {
  /** The pending answer being changed, or none for a new answer. */
  readonly answerId?: string;
  readonly draft: AnswerDraft;
  readonly role: ClientRole;
};

/** One question: the answers already given, and a form for the next. */
function QuestionCard({
  question,
  roles,
  answers,
  onChanged,
}: {
  question: PortalQuestion;
  roles: readonly ClientRole[];
  answers: readonly PortalAnswer[];
  onChanged: () => void;
}) {
  const { call } = usePortalApi();
  const [editing, setEditing] = useState<Editing | null>(null);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const firstRole = roles[0] ?? 'debtor_1';
  const askRole = question.perDebtor && roles.length > 1;
  const pending = answers.some((answer) => answer.status === 'pending');
  // A one-answer question with an answer waiting is changed, not answered
  // again — the server refuses a second one with a 409.
  const canAdd = question.repeats || !pending;

  const start = (answer?: PortalAnswer) => {
    setErrors({});
    setProblem(null);
    setEditing(
      answer === undefined
        ? { draft: emptyDraft(question), role: firstRole }
        : {
            answerId: answer.id,
            draft: draftFrom(question, answer.value),
            role: answer.filingRole,
          },
    );
  };

  const save = async () => {
    if (editing === null) return;
    setBusy(true);
    setErrors({});
    setProblem(null);
    const value = valueFrom(question, editing.draft);
    try {
      const result = await call((client) =>
        editing.answerId === undefined
          ? client.createPortalAnswer({
              questionId: question.id,
              ...(roles.length > 1 ? { filingRole: askRole ? editing.role : firstRole } : {}),
              value,
            })
          : client.updatePortalAnswer(editing.answerId, value),
      );
      if (result.ok) {
        setEditing(null);
        onChanged();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
        setProblem(cause.fields.value ?? cause.fields.filingRole ?? null);
      } else if (cause instanceof ApiException && cause.statusCode === 409) {
        setProblem('Your firm has already reviewed this answer. Reload the page to see it.');
      } else {
        setProblem('Your answer could not be saved. Try again in a moment.');
      }
    } finally {
      setBusy(false);
    }
  };

  const withdraw = async (answer: PortalAnswer) => {
    setProblem(null);
    try {
      const result = await call((client) => client.withdrawPortalAnswer(answer.id));
      if (result.ok) onChanged();
    } catch {
      setProblem('That answer could not be withdrawn. Reload the page to see where it stands.');
    }
  };

  return (
    <Card.Root style={styles.card}>
      <Heading level={2} size="section">
        {question.text}
      </Heading>
      {question.help === undefined ? null : <InkText muted>{question.help}</InkText>}

      {answers.length === 0 ? null : (
        <View role="list" style={styles.answers}>
          {answers.map((answer) => (
            <View role="listitem" key={answer.id} style={styles.answer}>
              <View style={styles.answerLine}>
                <InkText>{summary(question, answer.value) || 'Answered'}</InkText>
                <Badge intent={STATUS[answer.status].intent} size="sm">
                  {STATUS[answer.status].label}
                </Badge>
                {roles.length > 1 ? (
                  <InkText muted>For {ROLE_LABEL[answer.filingRole]}</InkText>
                ) : null}
              </View>
              {answer.status === 'pending' && editing?.answerId !== answer.id ? (
                <View style={styles.actions}>
                  <Button size="lg" intent="secondary" onPress={() => start(answer)}>
                    Change
                  </Button>
                  <Button size="lg" intent="secondary" onPress={() => void withdraw(answer)}>
                    Withdraw
                  </Button>
                </View>
              ) : null}
            </View>
          ))}
        </View>
      )}

      {editing === null ? (
        canAdd ? (
          <View style={styles.actions}>
            <Button
              size="lg"
              intent={answers.length === 0 ? 'primary' : 'secondary'}
              onPress={() => start()}
            >
              {answers.length === 0 ? 'Answer' : question.repeats ? 'Add another' : 'Change answer'}
            </Button>
          </View>
        ) : null
      ) : (
        <View style={styles.form}>
          {askRole ? (
            <Field.Root name="filingRole">
              <Field.Label>Who is this about?</Field.Label>
              <Select
                options={roles.map((role) => ({ value: role, label: ROLE_LABEL[role] }))}
                value={editing.role}
                onValueChange={(role) => setEditing({ ...editing, role: role as ClientRole })}
                disabled={editing.answerId !== undefined}
              />
            </Field.Root>
          ) : null}
          {question.inputs.map((input) => (
            <AnswerInput
              key={input.key}
              input={input}
              value={editing.draft[input.key] ?? ''}
              errors={errors}
              onChange={(next) =>
                setEditing({ ...editing, draft: { ...editing.draft, [input.key]: next } })
              }
            />
          ))}
          <View style={styles.actions}>
            <Button size="lg" onPress={() => void save()} disabled={busy}>
              Save
            </Button>
            <Button size="lg" intent="secondary" onPress={() => setEditing(null)} disabled={busy}>
              Cancel
            </Button>
          </View>
        </View>
      )}
      {problem === null ? null : (
        <View aria-live="assertive">
          <InkText>{problem}</InkText>
        </View>
      )}
    </Card.Root>
  );
}

/** One box — or, for an address, its labelled members. */
function AnswerInput({
  input,
  value,
  errors,
  onChange,
}: {
  input: PortalQuestionInput;
  value: string | AddressDraft;
  errors: Readonly<Record<string, string>>;
  onChange: (next: string | AddressDraft) => void;
}) {
  const label = input.required ? input.label : `${input.label} (optional)`;
  if (input.type === 'address') {
    const address = typeof value === 'string' ? {} : value;
    return (
      <View role="group" aria-label={input.label} style={styles.address}>
        <InkText>{label}</InkText>
        {ADDRESS_MEMBERS.map((member) => {
          const error = errors[`value.${input.key}.${member.key}`];
          return (
            <Field.Root
              key={member.key}
              name={`${input.key}.${member.key}`}
              invalid={error !== undefined}
            >
              <Field.Label>{member.label}</Field.Label>
              <Input
                value={address[member.key] ?? ''}
                onValueChange={(next) => onChange({ ...address, [member.key]: next })}
              />
              {error === undefined ? null : <Field.Error match>{error}</Field.Error>}
            </Field.Root>
          );
        })}
      </View>
    );
  }
  const text = typeof value === 'string' ? value : '';
  const error = errors[`value.${input.key}`];
  return (
    <Field.Root name={input.key} invalid={error !== undefined}>
      <Field.Label>{label}</Field.Label>
      {input.type === 'long_text' ? (
        <Textarea value={text} onValueChange={onChange} />
      ) : input.type === 'date' ? (
        <DateInput
          value={text}
          onValueChange={(next, status) => {
            if (status === 'incomplete') return;
            onChange(next);
          }}
        />
      ) : (
        <Input value={text} onValueChange={onChange} />
      )}
      {input.help === undefined ? null : <Field.Description>{input.help}</Field.Description>}
      {error === undefined ? null : <Field.Error match>{error}</Field.Error>}
    </Field.Root>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  address: {
    gap: spacing.sm,
  },
  answer: {
    gap: spacing.xs,
  },
  answerLine: {
    alignItems: 'center',
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
  },
  answers: {
    gap: spacing.md,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  card: {
    gap: spacing.md,
    padding: spacing.lg,
  },
  form: {
    gap: spacing.md,
  },
  nav: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    justifyContent: 'space-between',
  },
});
