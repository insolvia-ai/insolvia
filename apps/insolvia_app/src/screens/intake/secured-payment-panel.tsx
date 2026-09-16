import {
  ApiValidationException,
  SECURED_PAYMENT_BUCKETS,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  CaseEntityRequest,
  MeansTestInputBody,
  OtherSecuredPayment,
  SecuredPaymentBucket,
} from '@insolvia-ai/api-client';
import { Button, Field, Input, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import {
  inputBodyOf,
  securedPaymentFor,
  withSecuredPayment,
  withoutSecuredPayment,
} from '@/screens/means-test/inputs';
import { fontSizes, spacing, useTheme } from '@/theme';

import { newRowId } from './row-id';

/**
 * The means-test figures a secured claim carries (issue #349), entered
 * BESIDE the claim rather than on the means-test screen: the average monthly
 * payment over the next 60 months, the past-due amount the debtor must cure
 * to keep the property, and which IRS ownership allowance the payment
 * offsets — the home (B122A-2 line 9b), the first or second claimed vehicle
 * (lines 13b/13e), or none of them (line 33d).
 *
 * WHERE IT IS WRITTEN. These are `other_secured_payments` rows on the case's
 * ONE `means_test_input` record, keyed by `claim_id`, so the engine reads
 * them by bucket exactly as the screen's own rows. The panel reads that
 * record, replaces this claim's row, and writes the whole record back with
 * `staff_typed` provenance — the same rule every editor applies, and exact
 * here because nothing on that record is ever extracted. It does NOT touch
 * the claim itself: a claim's amount is Schedule D's fact, its monthly
 * payment is the means test's, and `ClaimCollateralPanel` above this one
 * keeps the same separation for the collateral figures.
 *
 * The claim's creditor and collateral are copied onto the row so the form's
 * printed 33d row names them; they are copies, taken at save time.
 */

type PanelState =
  | { readonly kind: 'unsaved' }
  | { readonly kind: 'loading' }
  | { readonly kind: 'error' }
  | {
      readonly kind: 'ready';
      readonly inputId: string | null;
      readonly body: MeansTestInputBody;
    };

const BUCKET_OPTIONS = SECURED_PAYMENT_BUCKETS.map((value) => ({
  value,
  label: {
    home: 'The home (line 9b)',
    vehicle_1: 'Vehicle 1 (line 13b)',
    vehicle_2: 'Vehicle 2 (line 13e)',
    other: 'Other property (line 33d)',
  }[value],
}));

export function ClaimSecuredPaymentPanel({
  caseId,
  claimId,
  creditorName,
  propertyDescription,
}: {
  readonly caseId: string;
  readonly claimId: string | null;
  /** What the printed 33d row calls the creditor — copied at save time. */
  readonly creditorName: string | undefined;
  /** What the printed 33d row calls the property — copied at save time. */
  readonly propertyDescription: string | undefined;
}) {
  const theme = useTheme();
  const { call } = useApi();
  const [state, setState] = useState<PanelState>(
    claimId === null ? { kind: 'unsaved' } : { kind: 'loading' },
  );
  const [bucket, setBucket] = useState<SecuredPaymentBucket | null>(null);
  const [monthly, setMonthly] = useState('');
  const [cure, setCure] = useState('');
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});

  useEffect(() => {
    if (claimId === null) return;
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, 'means_test_inputs'));
        if (!result.ok || cancelled) return;
        const first = result.value[0];
        const body =
          first === undefined ? {} : inputBodyOf(first as unknown as Record<string, unknown>);
        const row = securedPaymentFor(body, claimId);
        setBucket(row?.bucket ?? null);
        setMonthly(row?.monthly_payment ?? '');
        setCure(row?.cure_total ?? '');
        setState({ kind: 'ready', inputId: first?.id ?? null, body });
      } catch {
        if (!cancelled) setState({ kind: 'error' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId, claimId]);

  const persist = useCallback(
    async (next: MeansTestInputBody) => {
      if (state.kind !== 'ready') return;
      setStatus('Saving…');
      try {
        const request = {
          ...next,
          provenance: staffTypedProvenance(next as unknown as Record<string, unknown>),
        } as unknown as CaseEntityRequest<'means_test_inputs'>;
        const result = await call((client) =>
          state.inputId === null
            ? client.addCaseEntity(caseId, 'means_test_inputs', request)
            : client.putCaseEntity(caseId, 'means_test_inputs', state.inputId, request),
        );
        if (!result.ok) {
          setStatus('');
          return;
        }
        setState({
          kind: 'ready',
          inputId: result.value.id,
          body: inputBodyOf(result.value as unknown as Record<string, unknown>),
        });
        setErrors({});
        setStatus('Saved — the means test will use it on its next run');
      } catch (cause) {
        if (cause instanceof ApiValidationException) {
          setErrors(cause.fields);
          setStatus('Some answers need attention.');
        } else {
          setStatus('Could not save. Try again.');
        }
      }
    },
    [call, caseId, state],
  );

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  if (state.kind === 'unsaved') {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Means test — this claim’s payment
        </Heading>
        <Text style={[styles.note, muted]}>
          Save the claim first — its means-test payment is entered against the saved record.
        </Text>
      </View>
    );
  }
  if (state.kind !== 'ready' || claimId === null) {
    return (
      <View style={styles.panel}>
        <Heading level={3} size="body">
          Means test — this claim’s payment
        </Heading>
        <Text
          style={[
            styles.note,
            state.kind === 'error'
              ? { color: theme.colors.danger, fontFamily: theme.typography.body }
              : muted,
          ]}
        >
          {state.kind === 'error' ? 'Could not load the means-test inputs.' : 'Loading…'}
        </Text>
      </View>
    );
  }

  const existing = securedPaymentFor(state.body, claimId);
  const rowError = (member: string) =>
    existing === undefined
      ? undefined
      : (errors[`other_secured_payments[${existing.id}].${member}`] ??
        Object.entries(errors).find(
          ([path]) => path.startsWith('other_secured_payments[') && path.endsWith(`.${member}`),
        )?.[1]);

  const save = () => {
    const row: OtherSecuredPayment = {
      id: existing?.id ?? newRowId(),
      claim_id: claimId,
      ...(bucket === null ? {} : { bucket }),
      ...(monthly.trim() === '' ? {} : { monthly_payment: monthly.trim() }),
      ...(cure.trim() === '' ? {} : { cure_total: cure.trim() }),
      ...(creditorName === undefined ? {} : { creditor_name: creditorName }),
      ...(propertyDescription === undefined ? {} : { property_description: propertyDescription }),
    };
    void persist(withSecuredPayment(state.body, row));
  };

  return (
    <View style={styles.panel}>
      <Heading level={3} size="body">
        Means test — this claim’s payment
      </Heading>
      <Text style={[styles.note, muted]}>
        The 60-month average monthly payment, the amount past due, and which IRS ownership allowance
        the payment offsets. Read by the means test on its next run; the claim’s own amount is
        untouched.
      </Text>
      <Field.Root invalid={rowError('bucket') !== undefined}>
        <Field.Label>Which IRS allowance does this payment offset?</Field.Label>
        <Select
          options={BUCKET_OPTIONS}
          value={bucket}
          onValueChange={(next) =>
            setBucket(
              SECURED_PAYMENT_BUCKETS.includes(next as SecuredPaymentBucket)
                ? (next as SecuredPaymentBucket)
                : null,
            )
          }
          placeholder="Choose one"
        />
        {rowError('bucket') ? <Field.Error match>{rowError('bucket')}</Field.Error> : null}
      </Field.Root>
      <Field.Root invalid={rowError('monthly_payment') !== undefined}>
        <Field.Label>Average monthly payment over the next 60 months</Field.Label>
        <Input value={monthly} onValueChange={setMonthly} autoCorrect={false} />
        <Field.Description>Dollars, like 415.00.</Field.Description>
        {rowError('monthly_payment') ? (
          <Field.Error match>{rowError('monthly_payment')}</Field.Error>
        ) : null}
      </Field.Root>
      <Field.Root invalid={rowError('cure_total') !== undefined}>
        <Field.Label>Total past due (the cure amount; line 34 divides it by 60)</Field.Label>
        <Input value={cure} onValueChange={setCure} autoCorrect={false} />
        <Field.Description>
          Dollars, like 2100.00. Leave blank if nothing is past due.
        </Field.Description>
        {rowError('cure_total') ? <Field.Error match>{rowError('cure_total')}</Field.Error> : null}
      </Field.Root>
      <View style={styles.actions}>
        <Button size="lg" intent="secondary" onPress={save}>
          Save means-test payment
        </Button>
        {existing === undefined ? null : (
          <Button
            size="lg"
            intent="secondary"
            onPress={() => {
              setBucket(null);
              setMonthly('');
              setCure('');
              void persist(withoutSecuredPayment(state.body, claimId));
            }}
          >
            Remove means-test payment
          </Button>
        )}
      </View>
      <Text aria-live="polite" style={[styles.note, muted]}>
        {status}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  note: { fontSize: fontSizes.label },
  panel: { gap: spacing.sm, marginTop: spacing.sm },
});
