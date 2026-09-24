import {
  ApiValidationException,
  FILING_PROFESSIONAL_ROLES,
  staffTypedProvenance,
} from '@insolvia-ai/api-client';
import type {
  Address,
  FilingProfessionalBody,
  FilingProfessionalRole,
  FirmColleague,
  PersonName,
} from '@insolvia-ai/api-client';
import { Button, DateInput, Field, Input, Select } from '@insolvia-ai/design-system';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMe } from '@/api/me';
import { useApi } from '@/api/use-api';
import { Heading } from '@/components/heading';
import { labelize } from '@/screens/intake/collections';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * B101 Part 7 — who signs (issue #342). 0-2 `filing_professional` records per
 * case: an attorney signs the petition, or a bankruptcy petition preparer
 * does (→ Form 119); the pro se path (neither) needs no record here at all.
 *
 * A ONE-RECORD-PER-ROLE FORM, not the generic `CollectionEditor` — the issue
 * asks for a dedicated signer-block form, and unlike the three repeating
 * lists this collection is not a plain list a paralegal adds rows to: there
 * are at most two meaningful rows, one per role, and the role picker below
 * IS the record picker. Saves are explicit, like the collection editor's own
 * (a half-typed attorney block autosaving would create a record from two
 * keystrokes), and like it the whole record is sent on every save — the
 * generic entity endpoints are PUT, not PATCH (invariant 1: every populated
 * field needs provenance, checked against a complete record).
 *
 * "USE FIRM DEFAULT" (issue #360). An attorney's bar number, bar state and
 * firm lines do not change from case to case, so the firm keeps them: each
 * attorney's `signatureBlock` on their firm-user row (the directory carries
 * it) and the firm's `letterhead` for the firm lines a block leaves blank.
 * The button copies the chosen attorney's block onto THIS form — it does not
 * save. The preparer still reads every line and presses save, and the record
 * goes up with `staff_typed` provenance like anything else they confirmed:
 * a prefill is a suggestion, and the person who confirms it is the source.
 */

const ROLE_OPTIONS = FILING_PROFESSIONAL_ROLES.map((value) => ({ value, label: labelize(value) }));

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

interface StoredRecord {
  readonly id: string;
  readonly body: FilingProfessionalBody;
}

export function SignerSection({ caseId }: { caseId: string }) {
  const theme = useTheme();
  const { call } = useApi();
  const me = useMe();
  const principal = me.kind === 'ready' ? me.principal : undefined;

  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [records, setRecords] = useState<Partial<Record<FilingProfessionalRole, StoredRecord>>>({});
  const [role, setRole] = useState<FilingProfessionalRole>('attorney');
  const [body, setBody] = useState<FilingProfessionalBody>({});
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);
  // The colleagues whose rows carry a signature block — the firm's attorneys,
  // in practice. Loaded separately from the record: a directory this screen
  // could not fetch costs the prefill button, never the form.
  const [attorneys, setAttorneys] = useState<readonly FirmColleague[]>([]);
  const [prefillFrom, setPrefillFrom] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listFirmDirectory());
        if (!result.ok || cancelled) return;
        const withBlocks = result.value.filter((person) => person.signatureBlock !== null);
        setAttorneys(withBlocks);
      } catch {
        // No directory, no prefill offer — the form itself is unaffected.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call]);

  // Default the picker to the signed-in person when they are an option —
  // the common case is the attorney preparing their own petition.
  useEffect(() => {
    if (prefillFrom !== null || attorneys.length === 0) return;
    const self = attorneys.find((person) => person.subject === principal?.subject);
    setPrefillFrom((self ?? attorneys[0]!).subject);
  }, [attorneys, prefillFrom, principal]);

  const useFirmDefault = () => {
    const person = attorneys.find((candidate) => candidate.subject === prefillFrom);
    const block = person?.signatureBlock;
    if (person === undefined || block === null || block === undefined) return;
    const letterhead = principal?.firm?.letterhead ?? null;
    const address = block.address ?? letterhead?.address;
    setRole('attorney');
    setBody((current) => ({
      ...current,
      role: 'attorney',
      name: {
        ...(person.firstName ? { given: person.firstName } : {}),
        ...(person.lastName ? { surname: person.lastName } : {}),
      },
      ...defined('firm_name', block.firm_name ?? letterhead?.name),
      ...(address === undefined ? {} : { address }),
      ...defined('phone', block.phone ?? letterhead?.phone),
      ...defined('email', block.email ?? letterhead?.email),
      ...defined('bar_number', block.bar_number),
      ...defined('bar_state', block.bar_state),
    }));
    setErrors({});
    setStatus(
      `Prefilled from ${person.displayName}’s signature block — check every line, then save.`,
    );
  };

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) =>
          client.listCaseEntities(caseId, 'filing_professionals'),
        );
        if (!result.ok || cancelled) return;
        const byRole: Partial<Record<FilingProfessionalRole, StoredRecord>> = {};
        for (const entity of result.value) {
          // Two rows can share a role only if something wrote them outside
          // this screen; the first one found is what this form edits and
          // saves over — the same "first block only" trade the B101
          // projection itself makes for sole proprietorships.
          if (entity.role !== undefined && byRole[entity.role] === undefined) {
            const {
              id: _id,
              case_id: _caseId,
              created_at: _created,
              updated_at: _updated,
              provenance: _provenance,
              ...rest
            } = entity;
            byRole[entity.role] = { id: entity.id, body: rest };
          }
        }
        setRecords(byRole);
        setBody(byRole.attorney?.body ?? {});
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load the signer block.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const switchRole = (next: FilingProfessionalRole) => {
    setRole(next);
    setBody(records[next]?.body ?? {});
    setErrors({});
    setStatus('');
  };

  const persist = useCallback(async () => {
    setSaving(true);
    setStatus('Saving…');
    try {
      const toSave: FilingProfessionalBody = { ...body, role };
      const existingId = records[role]?.id ?? null;
      // See the same cast in `screens/petition/index.tsx` — an interface
      // body needs it, a `Record<string, unknown>` does not.
      const request = {
        ...toSave,
        provenance: staffTypedProvenance(toSave as unknown as Record<string, unknown>),
      };
      const result = await call((client) =>
        existingId === null
          ? client.addCaseEntity(caseId, 'filing_professionals', request)
          : client.putCaseEntity(caseId, 'filing_professionals', existingId, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      const {
        id: _id,
        case_id: _caseId,
        created_at: _created,
        updated_at: _updated,
        provenance: _provenance,
        ...rest
      } = result.value;
      setRecords((current) => ({ ...current, [role]: { id: result.value.id, body: rest } }));
      setErrors({});
      setStatus('Saved');
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
  }, [body, call, caseId, records, role]);

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const setName = (part: keyof PersonName, value: string) =>
    setBody((current) => ({ ...current, name: { ...current.name, [part]: value } }));
  const setAddress = (part: keyof Address, value: string) =>
    setBody((current) => ({ ...current, address: { ...current.address, [part]: value } }));

  return (
    <View style={styles.section}>
      <Heading level={2}>Who signs</Heading>
      <Text style={[styles.help, muted]}>
        An attorney signs the petition, or a bankruptcy petition preparer does (Form 119). Choose
        which block to fill in — a pro se filing needs neither.
      </Text>
      <Text
        aria-live={load.kind === 'error' ? 'assertive' : 'polite'}
        style={[
          styles.status,
          load.kind === 'error'
            ? { color: theme.colors.danger, fontFamily: theme.typography.body }
            : muted,
        ]}
      >
        {load.kind === 'loading'
          ? 'Loading the signer block…'
          : load.kind === 'error'
            ? load.message
            : status}
      </Text>

      {load.kind !== 'ready' ? null : (
        <View style={styles.form}>
          {attorneys.length === 0 ? (
            <Text style={[styles.help, muted]}>
              No attorney at your firm has a signature block yet — add yours on your account page
              and this block prefills from it.
            </Text>
          ) : (
            <View style={styles.prefill}>
              <Field.Root>
                <Field.Label>Prefill from</Field.Label>
                <Select
                  options={attorneys.map((person) => ({
                    value: person.subject,
                    label: person.displayName,
                  }))}
                  value={prefillFrom}
                  onValueChange={setPrefillFrom}
                />
              </Field.Root>
              <Button size="lg" intent="secondary" onPress={useFirmDefault}>
                Use firm default
              </Button>
            </View>
          )}

          <Field.Root>
            <Field.Label>Role</Field.Label>
            <Select
              options={[...ROLE_OPTIONS]}
              value={role}
              onValueChange={(next) => switchRole(next as FilingProfessionalRole)}
            />
          </Field.Root>

          <TextField
            label="First name"
            path="name.given"
            value={body.name?.given}
            onChangeText={(v) => setName('given', v)}
            errors={errors}
          />
          <TextField
            label="Middle name"
            path="name.middle"
            value={body.name?.middle}
            onChangeText={(v) => setName('middle', v)}
            errors={errors}
          />
          <TextField
            label="Last name"
            path="name.surname"
            value={body.name?.surname}
            onChangeText={(v) => setName('surname', v)}
            errors={errors}
          />
          <TextField
            label="Suffix"
            path="name.suffix"
            value={body.name?.suffix}
            onChangeText={(v) => setName('suffix', v)}
            errors={errors}
          />
          <TextField
            label="Firm name"
            path="firm_name"
            value={body.firm_name}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, firm_name: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          {(['line1', 'line2', 'city', 'state', 'postal_code'] as const).map((part) => (
            <TextField
              key={part}
              label={`Address — ${addressLabel(part)}`}
              path={`address.${part}`}
              value={body.address?.[part]}
              onChangeText={(v) => setAddress(part, v)}
              errors={errors}
            />
          ))}
          <TextField
            label="Phone"
            path="phone"
            value={body.phone}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, phone: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          <TextField
            label="Email"
            path="email"
            value={body.email}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, email: v === '' ? undefined : v }))
            }
            errors={errors}
          />
          <TextField
            label="Bar number"
            path="bar_number"
            value={body.bar_number}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, bar_number: v === '' ? undefined : v }))
            }
            errors={errors}
            disabled={role !== 'attorney'}
            disabledReason="Only applies to an attorney."
          />
          <TextField
            label="Bar state"
            path="bar_state"
            value={body.bar_state}
            onChangeText={(v) =>
              setBody((current) => ({ ...current, bar_state: v === '' ? undefined : v }))
            }
            errors={errors}
            disabled={role !== 'attorney'}
            disabledReason="Only applies to an attorney."
          />
          <Field.Root invalid={Boolean(errors.signature_date)}>
            <Field.Label>Date signed</Field.Label>
            <DateInput
              value={body.signature_date ?? ''}
              onValueChange={(next, dateStatus) => {
                if (dateStatus === 'incomplete') return;
                setBody((current) => ({
                  ...current,
                  signature_date: next === '' ? undefined : next,
                }));
              }}
            />
            {errors.signature_date ? (
              <Field.Error match>{errors.signature_date}</Field.Error>
            ) : null}
          </Field.Root>

          <Button size="lg" disabled={saving} onPress={() => void persist()}>
            {records[role] === undefined ? 'Save signer' : 'Save changes'}
          </Button>
        </View>
      )}
    </View>
  );
}

/** `{ [key]: value }` when the value is set, `{}` when it is not — so a
 * prefill never writes an explicit `undefined` the save would carry. */
function defined<K extends string>(key: K, value: string | undefined): Partial<Record<K, string>> {
  return value === undefined ? {} : ({ [key]: value } as Record<K, string>);
}

function addressLabel(part: 'line1' | 'line2' | 'city' | 'state' | 'postal_code'): string {
  switch (part) {
    case 'line1':
      return 'Street';
    case 'line2':
      return 'Apartment, suite or unit';
    case 'city':
      return 'City';
    case 'state':
      return 'State';
    case 'postal_code':
      return 'ZIP code';
  }
}

function TextField({
  label,
  path,
  value,
  onChangeText,
  errors,
  disabled = false,
  disabledReason,
}: {
  label: string;
  path: string;
  value: string | undefined;
  onChangeText: (value: string) => void;
  errors: Readonly<Record<string, string>>;
  disabled?: boolean;
  disabledReason?: string;
}) {
  const message = errors[path];
  return (
    <Field.Root invalid={Boolean(message)}>
      <Field.Label>{label}</Field.Label>
      <Input
        value={value ?? ''}
        onValueChange={onChangeText}
        autoCorrect={false}
        disabled={disabled}
      />
      {disabled && disabledReason ? <Field.Description>{disabledReason}</Field.Description> : null}
      {message ? <Field.Error match>{message}</Field.Error> : null}
    </Field.Root>
  );
}

const styles = StyleSheet.create({
  form: { gap: spacing.md },
  help: { fontSize: fontSizes.label },
  prefill: { gap: spacing.sm },
  section: { gap: spacing.md },
  status: { fontSize: fontSizes.label },
});
