import { ApiValidationException, staffTypedProvenance } from '@insolvia-ai/api-client';
import type {
  EstimatedCreditorsBand,
  EstimatedDollarBand,
  PetitionBody,
} from '@insolvia-ai/api-client';
import { Button } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useApi } from '@/api/use-api';
import { CaseColumn, useCase } from '@/components/case-shell';
import { Heading } from '@/components/heading';
import { CollectionEditor } from '@/screens/intake/collection-editor';
import { COLLECTION_SPECS } from '@/screens/intake/collections';
import { fontSizes, spacing, useTheme } from '@/theme';

import { creditorsBand, dollarBand } from './estimates';
import { PetitionFields } from './petition-fields';
import { SignerSection } from './signer-section';
import { formatFormDate, statutoryDates } from './statutory-dates';

/**
 * `/cases/<caseId>/petition` — the B101 answers (issue #342).
 *
 * Three parts, matching the issue's own split:
 *
 * 1. **The petition body** — ONE record per case by meaning. Loaded here,
 *    edited by `PetitionFields`, saved explicitly (no autosave: a half-typed
 *    answer to "estimated liabilities" is not something to persist on a
 *    debounce, unlike the debtor's name fields).
 * 2. **The three repeating lists** — prior cases, related cases, sole
 *    proprietorships — reuse the generic `CollectionEditor` from the intake
 *    screen, driven by the specs already added to `collections.ts`. Nothing
 *    bespoke: they are plain lists like creditors or assets.
 * 3. **The signer block** — `SignerSection`, a dedicated one-role-at-a-time
 *    form (see its own header comment for why it isn't the generic editor).
 *
 * ESTIMATES DERIVE FROM THE CASE, NOT FROM NOTHING. The creditor count and
 * the Schedule A/B and D–F totals are read once here (the case overview's
 * own pattern: a nicety next to the petition record, allowed to fail on its
 * own) and handed to `PetitionFields`, which shows the derived band unless
 * the attorney checks "Enter manually" for that field. The derived value is
 * kept in sync with the saved body by the effect below, so Save always
 * writes what is on screen.
 */

const PRIOR_CASE_SPEC = COLLECTION_SPECS.find((spec) => spec.collection === 'prior_cases');
const RELATED_CASE_SPEC = COLLECTION_SPECS.find((spec) => spec.collection === 'related_cases');
const SOLE_PROPRIETORSHIP_SPEC = COLLECTION_SPECS.find(
  (spec) => spec.collection === 'sole_proprietorships',
);

type LoadState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'error'; readonly message: string };

function bodyOf(record: Record<string, unknown>): PetitionBody {
  const {
    id: _id,
    case_id: _caseId,
    created_at: _created,
    updated_at: _updated,
    provenance: _provenance,
    ...body
  } = record;
  return body as PetitionBody;
}

export function Petition() {
  const theme = useTheme();
  const { call } = useApi();
  const { caseId, matter } = useCase();

  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [petitionId, setPetitionId] = useState<string | null>(null);
  const [body, setBody] = useState<PetitionBody>({});
  const [status, setStatus] = useState('');
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [saving, setSaving] = useState(false);

  // Read once, allowed to fail on its own — the estimates simply derive
  // nothing and the field falls back to "enter manually", same trade the
  // case overview makes for its own counts.
  const [creditorCount, setCreditorCount] = useState<number | null>(null);
  const [totals, setTotals] = useState<{ assets: string; liabilities: string } | null>(null);

  const [manualCreditors, setManualCreditors] = useState(false);
  const [manualAssets, setManualAssets] = useState(false);
  const [manualLiabilities, setManualLiabilities] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, 'petitions'));
        if (!result.ok || cancelled) return;
        // ONE per case by meaning (docs/reference/case-data-model.md); the
        // first record found is the one this screen edits, matching the
        // signer section's "first block only" trade.
        const first = result.value[0];
        if (first !== undefined) {
          setPetitionId(first.id);
          setBody(bodyOf(first as unknown as Record<string, unknown>));
        }
        setLoad({ kind: 'ready' });
      } catch {
        if (!cancelled) setLoad({ kind: 'error', message: 'Could not load the petition.' });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await call((client) => client.listCaseEntities(caseId, 'creditors'));
        if (!cancelled && result.ok) setCreditorCount(result.value.length);
      } catch {
        // A derived default is a nicety; the petition form is not.
      }
      try {
        const summary = await call((client) => client.getCaseSummary(caseId));
        if (!cancelled && summary.ok) {
          setTotals({
            assets: summary.value.totals.assets,
            liabilities: summary.value.totals.liabilities,
          });
        }
      } catch {
        // Same trade.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [call, caseId]);

  const derivedCreditors: EstimatedCreditorsBand | undefined =
    creditorCount === null ? undefined : creditorsBand(creditorCount);
  const derivedAssets: EstimatedDollarBand | undefined =
    totals === null ? undefined : dollarBand(totals.assets);
  const derivedLiabilities: EstimatedDollarBand | undefined =
    totals === null ? undefined : dollarBand(totals.liabilities);

  // Once a petition record has loaded, a stored estimate that does not match
  // what would be derived is treated as a deliberate override — the only
  // signal the stored record carries, since "manual" is not a separate
  // field. Runs once per load, not on every derived-value change, so
  // flipping the checkbox later is the only thing that changes it again.
  useEffect(() => {
    if (load.kind !== 'ready') return;
    setManualCreditors(
      body.estimated_creditors !== undefined && body.estimated_creditors !== derivedCreditors,
    );
    setManualAssets(body.estimated_assets !== undefined && body.estimated_assets !== derivedAssets);
    setManualLiabilities(
      body.estimated_liabilities !== undefined && body.estimated_liabilities !== derivedLiabilities,
    );
    // Deliberately NOT re-run when the derived values themselves change —
    // see the comment above. Only the load transition matters here.
  }, [load.kind]);

  // Keeps the body in step with the derived value for every field that is
  // NOT overridden, so Save always writes what is on screen — "the derived
  // value is what is saved unless overridden" (issue #342).
  useEffect(() => {
    if (manualCreditors) return;
    setBody((current) =>
      current.estimated_creditors === derivedCreditors
        ? current
        : { ...current, estimated_creditors: derivedCreditors },
    );
  }, [manualCreditors, derivedCreditors]);
  useEffect(() => {
    if (manualAssets) return;
    setBody((current) =>
      current.estimated_assets === derivedAssets
        ? current
        : { ...current, estimated_assets: derivedAssets },
    );
  }, [manualAssets, derivedAssets]);
  useEffect(() => {
    if (manualLiabilities) return;
    setBody((current) =>
      current.estimated_liabilities === derivedLiabilities
        ? current
        : { ...current, estimated_liabilities: derivedLiabilities },
    );
  }, [manualLiabilities, derivedLiabilities]);

  const persist = async () => {
    setSaving(true);
    setStatus('Saving…');
    try {
      // `PetitionBody` is a strict interface, not `Record<string, unknown>`
      // (TypeScript gives a type alias an implicit index signature but not an
      // interface), so it needs the same cast `CollectionEditor` makes for
      // every descriptor-driven body — the server re-validates either way.
      const request = {
        ...body,
        provenance: staffTypedProvenance(body as unknown as Record<string, unknown>),
      };
      const result = await call((client) =>
        petitionId === null
          ? client.addCaseEntity(caseId, 'petitions', request)
          : client.putCaseEntity(caseId, 'petitions', petitionId, request),
      );
      if (!result.ok) {
        setStatus('');
        return;
      }
      setPetitionId(result.value.id);
      setBody(bodyOf(result.value as unknown as Record<string, unknown>));
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
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  const dates = statutoryDates(body.expected_filing_date);

  return (
    <CaseColumn>
      <Heading level={1}>Petition (B101)</Heading>
      <Text style={[styles.help, muted]}>
        The case-level answers B101 asks: fee handling, prior and related cases, sole
        proprietorships, and who signs.
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
          ? 'Loading the petition…'
          : load.kind === 'error'
            ? load.message
            : status}
      </Text>

      {load.kind === 'ready' ? (
        <>
          <PetitionFields
            body={body}
            onChange={setBody}
            chapter={matter.chapter}
            errors={errors}
            estimatedCreditors={{
              derived: derivedCreditors,
              manual: manualCreditors,
              onManualChange: setManualCreditors,
            }}
            estimatedAssets={{
              derived: derivedAssets,
              manual: manualAssets,
              onManualChange: setManualAssets,
            }}
            estimatedLiabilities={{
              derived: derivedLiabilities,
              manual: manualLiabilities,
              onManualChange: setManualLiabilities,
            }}
          />
          <Button size="lg" disabled={saving} onPress={() => void persist()}>
            {petitionId === null ? 'Save petition' : 'Save changes'}
          </Button>
        </>
      ) : null}

      {PRIOR_CASE_SPEC !== undefined ? (
        <View style={styles.list}>
          <Text style={[styles.help, muted]}>
            {dates === null
              ? '8-year lookback: set an expected filing date above to see the cutoff.'
              : `8-year lookback: a prior case filed on or after ${formatFormDate(dates.priorCaseLookbackCutoff)} belongs here.`}
          </Text>
          <CollectionEditor caseId={caseId} spec={PRIOR_CASE_SPEC} />
        </View>
      ) : null}

      {RELATED_CASE_SPEC !== undefined ? (
        <CollectionEditor caseId={caseId} spec={RELATED_CASE_SPEC} />
      ) : null}

      {SOLE_PROPRIETORSHIP_SPEC !== undefined ? (
        <CollectionEditor caseId={caseId} spec={SOLE_PROPRIETORSHIP_SPEC} />
      ) : null}

      <SignerSection caseId={caseId} />
    </CaseColumn>
  );
}

const styles = StyleSheet.create({
  help: { fontSize: fontSizes.label },
  list: { gap: spacing.sm },
  status: { fontSize: fontSizes.label, marginBottom: spacing.sm },
});
