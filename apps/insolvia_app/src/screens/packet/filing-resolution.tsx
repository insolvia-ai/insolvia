import { ApiException, ApiValidationException } from '@insolvia-ai/api-client';
import type {
  Document,
  FilingApprovalView,
  FilingRecord,
  FilingState,
} from '@insolvia-ai/api-client';
import {
  Badge,
  Button,
  Checkbox,
  DateInput,
  Field,
  Input,
  RadioGroup,
  Select,
} from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

const STATE_INTENT: Record<FilingState, BadgeIntent> = {
  claimed: 'primary',
  signed_in: 'primary',
  uploading: 'primary',
  at_final_submit: 'primary',
  submitted: 'primary',
  filed: 'success',
  handed_back: 'warning',
  outcome_unknown: 'danger',
};

const STATE_LABEL: Record<FilingState, string> = {
  claimed: 'Filing — started',
  signed_in: 'Filing — signed in to the court',
  uploading: 'Filing — uploading',
  at_final_submit: 'Filing — submitting',
  submitted: 'Filing — the court answered',
  filed: 'Filed',
  handed_back: 'Handed back to you',
  outcome_unknown: 'Outcome unknown — check the docket',
};

type Outcome = 'filed' | 'not_filed';

/**
 * What became of the approval's filing (ADR 0024 PR 8): the worker's state,
 * its note when it stopped, the court's confirmation, and — for a filing
 * handed back or whose outcome is unknown — the attorney's resolution.
 *
 * ONLY THE FILING'S OWN ATTORNEY RESOLVES IT (`canResolve`), and the API is
 * the judge. A filing the court confirmed can only be recorded as filed,
 * with the court's number already filled in — "not filed" is not offered,
 * because the court has said otherwise. Either answer needs the box saying
 * the court's own docket was checked.
 */
export function FilingRecordPanel({
  caseId,
  filing,
  canResolve,
  onResolved,
}: {
  readonly caseId: string;
  readonly filing: FilingRecord;
  readonly canResolve: boolean;
  readonly onResolved: (view: FilingApprovalView) => void;
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const confirmation = filing.confirmation;
  const resolution = filing.resolution;

  return (
    <View style={styles.section}>
      <Heading level={3}>Filing</Heading>
      <View style={styles.badges}>
        <Badge intent={STATE_INTENT[filing.state]} size="sm">
          {STATE_LABEL[filing.state]}
        </Badge>
        {resolution !== undefined ? (
          <Badge intent={resolution.outcome === 'filed' ? 'success' : 'neutral'} size="sm">
            {resolution.outcome === 'filed' ? 'Recorded as filed' : 'Recorded as not filed'}
          </Badge>
        ) : null}
      </View>
      {filing.handBack !== undefined && filing.state !== 'filed' ? (
        <View style={styles.status}>
          <Text style={[styles.rowTitle, ink]}>{filing.handBack.title}</Text>
          <Text style={[styles.body, ink]}>{filing.handBack.action}</Text>
          {filing.handBack.courtSaid !== undefined ? (
            <Text
              style={[styles.body, muted]}
            >{`The court said: ${filing.handBack.courtSaid}`}</Text>
          ) : null}
        </View>
      ) : null}
      {confirmation !== undefined ? (
        <Text style={[styles.body, ink]} selectable>
          {`The court confirmed case ${confirmation.caseNumber}, filed ${confirmation.filedAt}.`}
        </Text>
      ) : null}
      {resolution !== undefined ? (
        <Text style={[styles.body, muted]}>
          {resolution.outcome === 'filed'
            ? `Recorded as filed: case ${resolution.caseNumber ?? ''}, petition date ${resolution.filedAt ?? ''}. Docket checked ${resolution.docketCheckedAt.slice(0, 16).replace('T', ' ')} UTC.`
            : `Recorded as not on the court's docket (checked ${resolution.docketCheckedAt.slice(0, 16).replace('T', ' ')} UTC). A new filing can be approved.`}
        </Text>
      ) : null}
      {filing.resolvable && canResolve ? (
        <ResolutionForm caseId={caseId} filing={filing} onResolved={onResolved} />
      ) : filing.resolvable ? (
        <Text style={[styles.body, muted]}>
          Only the attorney whose court login this filing used can record what the court’s docket
          shows for it.
        </Text>
      ) : null}
    </View>
  );
}

function ResolutionForm({
  caseId,
  filing,
  onResolved,
}: {
  readonly caseId: string;
  readonly filing: FilingRecord;
  readonly onResolved: (view: FilingApprovalView) => void;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const confirmed = filing.confirmation;
  const [outcome, setOutcome] = useState<Outcome>('filed');
  const [caseNumber, setCaseNumber] = useState(confirmed?.caseNumber ?? '');
  const [filedAt, setFiledAt] = useState(
    /^\d{4}-\d{2}-\d{2}/.test(confirmed?.filedAt ?? '')
      ? (confirmed?.filedAt ?? '').slice(0, 10)
      : '',
  );
  const [documentId, setDocumentId] = useState('');
  const [notices, setNotices] = useState<readonly Document[]>([]);
  const [checked, setChecked] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const result = await call((client) => client.listDocuments(caseId));
        if (live && result.ok) {
          setNotices(
            result.value.filter((d) => d.status === 'stored' && d.kind === 'court_notice'),
          );
        }
      } catch {
        // The upload is optional; without the list the field is simply absent.
      }
    })();
    return () => {
      live = false;
    };
  }, [call, caseId]);

  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const danger = { color: theme.colors.danger, fontFamily: theme.typography.body };
  const mayBeNotFiled = confirmed === undefined;
  const chosen: Outcome = mayBeNotFiled ? outcome : 'filed';

  const submit = async () => {
    setBusy(true);
    setErrors({});
    setMessage(null);
    try {
      const result = await call((client) =>
        client.resolveFiling(
          caseId,
          filing.filingId,
          chosen === 'filed'
            ? {
                outcome: 'filed',
                caseNumber: caseNumber.trim(),
                filedAt,
                ...(documentId === '' ? {} : { confirmationDocumentId: documentId }),
              }
            : { outcome: 'not_filed' },
        ),
      );
      if (result.ok) onResolved(result.value);
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        setErrors(cause.fields);
      } else if (cause instanceof ApiException) {
        setMessage(cause.message);
      } else {
        setMessage('Could not record it. Please try again.');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <View style={styles.form}>
      <Heading level={3}>Record what the court’s docket shows</Heading>
      <Text style={[styles.body, muted]}>
        Look the debtor up on the court’s own case query first. If the case is there, record its
        number: the case becomes filed. If it is not, record that — and only then can a new filing
        be approved.
      </Text>
      {mayBeNotFiled ? (
        <RadioGroup.Root
          aria-label="What the docket shows"
          value={outcome}
          onValueChange={(next) => setOutcome(next as Outcome)}
          disabled={busy}
          style={styles.radioGroup}
        >
          {(
            [
              ['filed', 'The case is on the docket (I filed it, or the court has it)'],
              ['not_filed', 'The case is not on the docket'],
            ] as const
          ).map(([value, label]) => (
            <View key={value} style={styles.radioOption}>
              <RadioGroup.Item value={value} aria-label={label} hitSlop={12}>
                <RadioGroup.Indicator />
              </RadioGroup.Item>
              <Text style={[styles.body, ink, styles.grow]}>{label}</Text>
            </View>
          ))}
        </RadioGroup.Root>
      ) : (
        <Text style={[styles.body, ink]}>
          The court confirmed this filing, so it can only be recorded as filed, under the court’s
          number.
        </Text>
      )}
      {chosen === 'filed' ? (
        <>
          <Field.Root name="case_number" invalid={Boolean(errors.case_number)}>
            <Field.Label>Case number</Field.Label>
            <Input
              value={caseNumber}
              onValueChange={setCaseNumber}
              placeholder="6:26-bk-10000"
              autoCorrect={false}
              autoCapitalize="none"
            />
            <Field.Description>As the docket prints it, office first.</Field.Description>
            {errors.case_number ? <Field.Error match>{errors.case_number}</Field.Error> : null}
          </Field.Root>
          <Field.Root name="filed_at" invalid={Boolean(errors.filed_at)}>
            <Field.Label>Petition date</Field.Label>
            <DateInput
              value={filedAt}
              picker="calendar"
              onValueChange={(next, status) => {
                if (status === 'incomplete') return;
                setFiledAt(next);
              }}
            />
            {errors.filed_at ? <Field.Error match>{errors.filed_at}</Field.Error> : null}
          </Field.Root>
          {notices.length > 0 ? (
            <Field.Root
              name="confirmation_document_id"
              invalid={Boolean(errors.confirmation_document_id)}
            >
              <Field.Label>The court’s notice (optional)</Field.Label>
              <Select
                options={[
                  { value: '', label: 'None' },
                  ...notices.map((d) => ({ value: d.id, label: d.fileName })),
                ]}
                value={documentId}
                onValueChange={setDocumentId}
              />
              <Field.Description>
                A court notice uploaded to this case’s documents.
              </Field.Description>
              {errors.confirmation_document_id ? (
                <Field.Error match>{errors.confirmation_document_id}</Field.Error>
              ) : null}
            </Field.Root>
          ) : null}
        </>
      ) : null}
      <View style={styles.agree}>
        <Checkbox.Root
          aria-label="I checked the court's own docket for this debtor"
          checked={checked}
          onCheckedChange={setChecked}
        >
          <Checkbox.Indicator>✓</Checkbox.Indicator>
        </Checkbox.Root>
        <Text style={[styles.body, ink, styles.grow]}>
          I checked the court’s own docket for this debtor.
        </Text>
      </View>
      {errors.docket_checked ? (
        <Text style={[styles.body, danger]}>{errors.docket_checked}</Text>
      ) : null}
      <View style={styles.actions}>
        <Button
          size="lg"
          intent={chosen === 'filed' ? 'primary' : 'danger'}
          disabled={
            busy || !checked || (chosen === 'filed' && (caseNumber.trim() === '' || filedAt === ''))
          }
          onPress={() => void submit()}
        >
          {busy ? 'Recording…' : chosen === 'filed' ? 'Record as filed' : 'Record as not filed'}
        </Button>
      </View>
      {message === null ? null : (
        <Text aria-live="polite" style={[styles.body, danger]}>
          {message}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  agree: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  badges: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  body: { fontSize: fontSizes.body, lineHeight: fontSizes.body * 1.5 },
  form: { gap: spacing.md },
  grow: { flex: 1 },
  radioGroup: { gap: spacing.sm },
  radioOption: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  rowTitle: { fontSize: fontSizes.body, fontWeight: '600' },
  section: { gap: spacing.sm },
  status: { gap: spacing.xs },
});
