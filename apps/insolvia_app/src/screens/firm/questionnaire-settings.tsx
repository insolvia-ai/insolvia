import { ApiValidationException } from '@insolvia-ai/api-client';
import type {
  FirmQuestionnaire,
  FirmQuestionnaireSection,
  QuestionnaireSectionId,
} from '@insolvia-ai/api-client';
import { AlertDialog, Button, Field, Switch, Textarea } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

type Notice = { readonly tone: 'error' | 'saved'; readonly message: string };

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly questionnaire: FirmQuestionnaire }
  | { readonly kind: 'error' };

/** One section as this form holds it while somebody edits. */
type Draft = Readonly<
  Record<QuestionnaireSectionId, { readonly enabled: boolean; readonly instructions: string }>
>;

/**
 * The textarea shows the instructions IN FORCE — the firm's own, or the
 * default — so what an administrator reads here is what a client reads. Left
 * as the default (or emptied), the server stores "use the default", so a
 * later improvement to a default still reaches this firm.
 */
function draftFrom(questionnaire: FirmQuestionnaire): Draft {
  const draft = {} as Record<
    QuestionnaireSectionId,
    { readonly enabled: boolean; readonly instructions: string }
  >;
  for (const section of questionnaire.sections) {
    draft[section.id] = {
      enabled: section.enabled,
      instructions: section.instructions ?? section.defaultInstructions,
    };
  }
  return draft;
}

/**
 * Which questionnaire sections the firm's clients see, and what each tells
 * them (ADR 0023 PR 3 / #362) — on the firm screen, for administrators.
 *
 * EVERY SECTION IS LISTED, switched off or not: switching one off hides it
 * from the firm's CLIENTS, never from staff, and this screen is staff. Personal
 * information has no switch — it is always on, and the server refuses a save
 * that says otherwise.
 *
 * One save for the whole form, because the server's save is the whole record.
 * Reset to defaults asks first: it discards every instruction the firm wrote.
 */
export function QuestionnaireSettings({
  editable,
  onNotice,
}: {
  editable: boolean;
  onNotice: (notice: Notice | null) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [confirmReset, setConfirmReset] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.getFirmQuestionnaire());
        if (!cancelled && result.ok) {
          setState({ kind: 'ready', questionnaire: result.value });
          setDraft(draftFrom(result.value));
        }
      } catch {
        if (!cancelled) {
          setState({ kind: 'error' });
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  const settle = (questionnaire: FirmQuestionnaire) => {
    setState({ kind: 'ready', questionnaire });
    setDraft(draftFrom(questionnaire));
  };

  const save = async () => {
    if (state.kind !== 'ready' || draft === null) return;
    setBusy(true);
    setFieldErrors({});
    onNotice(null);
    try {
      const result = await call((client) =>
        client.saveFirmQuestionnaire({
          sections: state.questionnaire.sections.map((section) => ({
            id: section.id,
            enabled: draft[section.id].enabled,
            instructions: draft[section.id].instructions,
          })),
        }),
      );
      if (result.ok) {
        settle(result.value);
        onNotice({ tone: 'saved', message: 'The client questionnaire is saved.' });
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setFieldErrors(cause.fields);
      } else {
        onNotice({
          tone: 'error',
          message: 'Could not save the client questionnaire. Please try again.',
        });
      }
    } finally {
      setBusy(false);
    }
  };

  const reset = async () => {
    setBusy(true);
    setFieldErrors({});
    onNotice(null);
    try {
      const result = await call((client) => client.resetFirmQuestionnaire());
      if (result.ok) {
        settle(result.value);
        onNotice({
          tone: 'saved',
          message: 'The client questionnaire is back to the defaults.',
        });
      }
    } catch {
      onNotice({
        tone: 'error',
        message: 'Could not reset the client questionnaire. Please try again.',
      });
    } finally {
      setBusy(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };

  return (
    <View style={styles.form}>
      <Heading level={2}>Client questionnaire</Heading>
      <Text style={[styles.body, muted]}>
        Choose which sections your clients answer in the client portal, and what you tell them about
        each. A section you switch off is hidden from your clients only — your firm still sees it,
        and can complete it with the client.
      </Text>

      {state.kind !== 'ready' || draft === null ? (
        <Text
          aria-live={state.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {state.kind === 'error'
            ? 'Could not load the client questionnaire.'
            : 'Loading the client questionnaire…'}
        </Text>
      ) : (
        <>
          <Text style={[styles.caption, muted]}>
            {state.questionnaire.isDefault
              ? 'Your firm uses the default questionnaire: every section, with our instructions.'
              : `Last saved ${(state.questionnaire.updatedAt ?? '').slice(0, 10)}.`}
          </Text>

          <View role="list" style={styles.sections}>
            {state.questionnaire.sections.map((section) => (
              <View role="listitem" key={section.id}>
                <SectionSettings
                  section={section}
                  value={draft[section.id]}
                  editable={editable && !busy}
                  error={fieldErrors[`sections.${section.id}.instructions`]}
                  switchError={fieldErrors[`sections.${section.id}.enabled`]}
                  onChange={(next) => setDraft({ ...draft, [section.id]: next })}
                  ink={ink}
                  muted={muted}
                />
              </View>
            ))}
          </View>

          {editable ? (
            <View style={styles.actions}>
              <Button size="lg" onPress={() => void save()} disabled={busy}>
                {busy ? 'Saving…' : 'Save questionnaire'}
              </Button>
              <Button
                size="lg"
                intent="secondary"
                disabled={busy || state.questionnaire.isDefault}
                onPress={() => setConfirmReset(true)}
              >
                Reset to defaults
              </Button>
            </View>
          ) : (
            <Text style={[styles.body, muted]}>
              Changing the questionnaire is an administrator’s job.
            </Text>
          )}
        </>
      )}

      <AlertDialog.Root
        open={confirmReset}
        onOpenChange={(next) => {
          if (!next) setConfirmReset(false);
        }}
      >
        <AlertDialog.Popup>
          <AlertDialog.Title>Reset the questionnaire?</AlertDialog.Title>
          <AlertDialog.Description>
            Every section is switched back on for your clients, and the instructions your firm wrote
            are replaced by ours. Answers your clients have already given are not affected.
          </AlertDialog.Description>
          <View style={styles.actions}>
            <Button
              size="lg"
              intent="danger"
              disabled={busy}
              onPress={() => {
                setConfirmReset(false);
                void reset();
              }}
            >
              Reset
            </Button>
            <AlertDialog.Close>Cancel</AlertDialog.Close>
          </View>
        </AlertDialog.Popup>
      </AlertDialog.Root>
    </View>
  );
}

type TextColour = { readonly color: string; readonly fontFamily: string };

function SectionSettings({
  section,
  value,
  editable,
  error,
  switchError,
  onChange,
  ink,
  muted,
}: {
  section: FirmQuestionnaireSection;
  value: { readonly enabled: boolean; readonly instructions: string };
  editable: boolean;
  error: string | undefined;
  switchError: string | undefined;
  onChange: (next: { readonly enabled: boolean; readonly instructions: string }) => void;
  ink: TextColour;
  muted: TextColour;
}) {
  const label = `Show ${section.title.toLowerCase()} to clients`;
  return (
    <View style={styles.section}>
      <Heading level={3}>{section.title}</Heading>
      <Text style={[styles.caption, muted]}>{section.covers}</Text>

      {section.switchable ? (
        <View style={styles.switchRow}>
          <Switch.Root
            aria-label={label}
            checked={value.enabled}
            disabled={!editable}
            onCheckedChange={(enabled) => onChange({ ...value, enabled })}
          >
            <Switch.Thumb />
          </Switch.Root>
          <Text style={[styles.body, ink]}>
            {value.enabled ? 'Shown to clients' : 'Hidden from clients — staff only'}
          </Text>
        </View>
      ) : (
        <Text style={[styles.body, ink]}>Always shown to clients</Text>
      )}
      {switchError ? (
        <Text aria-live="assertive" style={[styles.caption, ink]}>
          {switchError}
        </Text>
      ) : null}

      {editable ? (
        <Field.Root name={`instructions-${section.id}`} invalid={Boolean(error)}>
          <Field.Label>Instructions for your client</Field.Label>
          <Textarea
            value={value.instructions}
            onValueChange={(instructions) => onChange({ ...value, instructions })}
          />
          <Field.Description>
            Leave as it is, or empty it, to use our default instructions.
          </Field.Description>
          {error ? <Field.Error match>{error}</Field.Error> : null}
        </Field.Root>
      ) : (
        <Text style={[styles.body, ink]}>{value.instructions}</Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caption: {
    fontSize: fontSizes.caption,
    lineHeight: fontSizes.caption * 1.5,
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
    marginTop: spacing.sm,
  },
  section: {
    gap: spacing.sm,
  },
  sections: {
    gap: spacing.lg,
  },
  switchRow: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
  },
});
