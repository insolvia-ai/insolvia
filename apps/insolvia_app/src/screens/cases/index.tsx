import { ApiValidationException } from '@insolvia-ai/api-client';
import type { Case, CaseChapter, CourtRegistry, FirmColleague } from '@insolvia-ai/api-client';
import { Badge, Button, Field, RadioGroup, Select, Table } from '@insolvia-ai/design-system';
import type { BadgeIntent } from '@insolvia-ai/design-system';
import { Link } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useMembership } from '@/api/me';
import { useApi } from '@/api/use-api';
import { AppShell } from '@/components/app-shell';
import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

const CHAPTERS: readonly { readonly value: CaseChapter; readonly label: string }[] = [
  { value: 7, label: 'Chapter 7' },
  { value: 13, label: 'Chapter 13' },
  { value: 11, label: 'Chapter 11' },
  { value: 12, label: 'Chapter 12' },
];

/** How a case's own status reads, rather than the wire's snake_case. */
const STATUS_LABEL: Record<Case['status'], string> = {
  intake: 'In intake',
  ready_to_file: 'Ready to file',
  filed: 'Filed',
};

const STATUS_INTENT: Record<Case['status'], BadgeIntent> = {
  intake: 'neutral',
  ready_to_file: 'success',
  filed: 'primary',
};

type ListState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly cases: readonly Case[] }
  | { readonly kind: 'error'; readonly message: string };

type RegistryState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly registry: CourtRegistry }
  | { readonly kind: 'error' };

/**
 * The case list, and the form that opens one — the screen that closes issue
 * 8.3's loop: sign in, `POST /v1/cases`, `GET /v1/cases`, a case on screen.
 *
 * Deliberately not the intake questionnaire. This creates the case *record* —
 * chapter, court and division — and nothing else; the multi-step
 * questionnaire that fills a case is 8.5, and building a thin version of it
 * here would be something 8.5 has to unpick.
 *
 * THE COURT IS PICKED, NOT TYPED (issue #360). `GET /v1/courts` is the
 * registry the server validates against, so the two `Select`s below offer
 * exactly what it will accept, and the printed district name is derived on
 * the server from the pair — the form never sends a district string. The
 * firm's defaults (`/v1/me`'s firm block) preselect the court, division and
 * chapter; the preparer changes any of them per case.
 *
 * Everything visual is ours: {@link AppShell}, {@link Heading}, and the design
 * system's `Button`, `Field`, `Select` and `RadioGroup` leaves. Chapter stays a
 * radio group even though the package has shipped `Select` since 0.4.0: with
 * four options a radio group is the better control regardless — every option is
 * visible and reachable without opening anything. The court list is ten
 * districts with up to seven divisions each, which is a `Select`'s job.
 */
export function Cases() {
  const theme = useTheme();
  const { call } = useApi();
  const membership = useMembership();

  const [list, setList] = useState<ListState>({ kind: 'loading' });
  // Subject -> name, so `createdBy` renders as a colleague rather than a uuid.
  // Loaded once and separately from the cases: it fails independently, and a
  // directory this screen could not fetch should cost names, not the list.
  const [colleagues, setColleagues] = useState<readonly FirmColleague[]>([]);
  const [courts, setCourts] = useState<RegistryState>({ kind: 'loading' });
  const [chapter, setChapter] = useState<CaseChapter>(7);
  const [court, setCourt] = useState<string | null>(null);
  const [division, setDivision] = useState<string | null>(null);
  // The firm defaults are applied ONCE, when they first arrive — after that
  // the form is the preparer's, and a membership refresh must not snap a
  // half-filled form back to the defaults.
  const [defaultsApplied, setDefaultsApplied] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (defaultsApplied || membership === undefined || membership === null) return;
    setDefaultsApplied(true);
    if (membership.defaultChapter !== null) setChapter(membership.defaultChapter);
    if (membership.defaultCourt !== null) setCourt(membership.defaultCourt);
    if (membership.defaultDivision !== null) setDivision(membership.defaultDivision);
  }, [defaultsApplied, membership]);

  useEffect(() => {
    let cancelled = false;
    const loadCourts = async () => {
      try {
        const result = await call((client) => client.listCourts());
        if (result.ok && !cancelled) {
          setCourts({ kind: 'ready', registry: result.value });
        }
      } catch {
        if (!cancelled) setCourts({ kind: 'error' });
      }
    };
    void loadCourts();
    return () => {
      cancelled = true;
    };
  }, [call]);

  const load = useCallback(async () => {
    try {
      const result = await call((client) => client.listCases({}));
      if (result.ok) {
        setList({ kind: 'ready', cases: result.value.cases });
      }
      // !ok means the session ended and useApi already navigated; leaving the
      // screen in `loading` is correct — it is about to unmount.
    } catch {
      setList({ kind: 'error', message: 'Could not load your cases.' });
    }
  }, [call]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    const loadDirectory = async () => {
      try {
        const result = await call((client) => client.listFirmDirectory());
        if (result.ok) {
          setColleagues(result.value);
        }
      } catch {
        // Names are a nicety here; the list is not. Falling back to the
        // subject is worse than a name and much better than an error screen
        // over a case list that loaded perfectly.
      }
    };
    void loadDirectory();
  }, [call]);

  const submit = async () => {
    setSubmitting(true);
    setFieldErrors({});
    setFormError(null);
    try {
      // The pair goes as chosen, empty when not — the server's per-field
      // message ("Choose the bankruptcy court…") is the validation, not a
      // second copy of the rule here (ADR 0001).
      const result = await call((client) =>
        client.createCase({ chapter, court: court ?? '', division: division ?? '' }),
      );
      if (result.ok) {
        await load();
      }
    } catch (cause) {
      if (cause instanceof ApiValidationException) {
        // The server is the source of truth for validation (ADR 0001), so its
        // per-field messages are rendered as-is rather than restated here.
        setFieldErrors(cause.fields);
      } else {
        setFormError('Could not open the case. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <AppShell>
      <Heading level={1}>Your cases</Heading>

      {/* level={2}: the screen owns the one <h1>; a heading picked for size
          rather than structure is what produces a `heading-order` failure. */}
      <Heading level={2}>Open a case</Heading>

      <View style={styles.form}>
        {/*
          RadioGroup.Item IS the 20dp circle — the package's own tests render it
          self-closing. Putting the label inside it makes four 20dp circles each
          try to contain a word, and they overlap into an unreadable pile. The
          label is a SIBLING; the only thing that belongs inside the Item is the
          Indicator dot.

          Two consequences that have to be handled here rather than assumed:
          `aria-label`, because a circle containing only a dot has no accessible
          name; and `hitSlop`, because 20dp is far under the 44dp WCAG 2.5.5
          target this app enforces — 12 on each side takes the tappable area to
          44 without changing the visual.
        */}
        <RadioGroup.Root
          aria-label="Chapter"
          value={String(chapter)}
          onValueChange={(next) => setChapter(Number(next) as CaseChapter)}
          style={styles.chapters}
        >
          {CHAPTERS.map((option) => (
            <View key={option.value} style={styles.chapterOption}>
              <RadioGroup.Item value={String(option.value)} aria-label={option.label} hitSlop={12}>
                <RadioGroup.Indicator />
              </RadioGroup.Item>
              <Text
                style={[
                  styles.chapterLabel,
                  { color: theme.colors.ink, fontFamily: theme.typography.body },
                ]}
              >
                {option.label}
              </Text>
            </View>
          ))}
        </RadioGroup.Root>
        {fieldErrors.chapter ? (
          <Text
            aria-live="assertive"
            style={[
              styles.error,
              { color: theme.colors.danger, fontFamily: theme.typography.body },
            ]}
          >
            {fieldErrors.chapter}
          </Text>
        ) : null}

        {courts.kind === 'ready' ? (
          <CourtPicker
            registry={courts.registry}
            court={court}
            division={division}
            onCourtChange={(next) => {
              setCourt(next);
              // A division belongs to its court; a new court starts with none.
              setDivision(null);
            }}
            onDivisionChange={setDivision}
            errors={fieldErrors}
          />
        ) : (
          <Text
            aria-live={courts.kind === 'error' ? 'assertive' : 'polite'}
            style={[styles.body, muted]}
          >
            {courts.kind === 'loading'
              ? 'Loading the court registry…'
              : 'Could not load the court registry — a case cannot be opened until it loads.'}
          </Text>
        )}

        <View style={styles.actions}>
          {/* size="lg" (48dp): the package's md is 40dp, under the 44dp
              WCAG 2.5.5 target-size floor this app enforces. */}
          <Button size="lg" onPress={submit} disabled={submitting || courts.kind !== 'ready'}>
            {submitting ? 'Opening…' : 'Open case'}
          </Button>
        </View>

        {formError === null ? null : (
          <Text
            aria-live="assertive"
            style={[
              styles.error,
              { color: theme.colors.danger, fontFamily: theme.typography.body },
            ]}
          >
            {formError}
          </Text>
        )}
      </View>

      <Heading level={2}>Existing cases</Heading>
      {list.kind === 'ready' ? (
        <CaseList cases={list.cases} colleagues={colleagues} />
      ) : (
        <Text
          aria-live={list.kind === 'error' ? 'assertive' : 'polite'}
          style={[styles.body, muted]}
        >
          {list.kind === 'loading' ? 'Loading your cases…' : list.message}
        </Text>
      )}
    </AppShell>
  );
}

/**
 * The court and division pickers, fed by the registry. Two `Select`s rather
 * than one flattened list: a district has up to seven divisions and the
 * court is the fact the preparer knows first — the division follows from the
 * debtor's county, which the registry also carries and a later screen can
 * use to suggest it.
 */
export function CourtPicker({
  registry,
  court,
  division,
  onCourtChange,
  onDivisionChange,
  errors,
  courtLabel = 'Court',
  divisionLabel = 'Division',
  courtField = 'court',
  divisionField = 'division',
}: {
  registry: CourtRegistry;
  court: string | null;
  division: string | null;
  onCourtChange: (court: string) => void;
  onDivisionChange: (division: string) => void;
  errors: Readonly<Record<string, string>>;
  courtLabel?: string;
  divisionLabel?: string;
  /** The error-map keys, which differ between a case (`court`) and a firm default (`defaultCourt`). */
  courtField?: string;
  divisionField?: string;
}) {
  const district = registry.districts.find((d) => d.code === court);
  const courtOptions = registry.districts.map((d) => ({ value: d.code, label: d.name }));
  const divisionOptions = (district?.divisions ?? []).map((d) => ({
    value: d.code,
    label: d.name,
  }));
  return (
    <>
      <Field.Root name={courtField} invalid={Boolean(errors[courtField])}>
        <Field.Label>{courtLabel}</Field.Label>
        <Select
          options={courtOptions}
          value={court}
          onValueChange={onCourtChange}
          placeholder="Choose a court"
        />
        <Field.Description>
          The bankruptcy court this case will be filed in, from the court registry.
        </Field.Description>
        {errors[courtField] ? <Field.Error match>{errors[courtField]}</Field.Error> : null}
      </Field.Root>
      <Field.Root name={divisionField} invalid={Boolean(errors[divisionField])}>
        <Field.Label>{divisionLabel}</Field.Label>
        <Select
          options={divisionOptions}
          value={division}
          onValueChange={onDivisionChange}
          placeholder={district === undefined ? 'Choose a court first' : 'Choose a division'}
          disabled={district === undefined}
        />
        {errors[divisionField] ? <Field.Error match>{errors[divisionField]}</Field.Error> : null}
      </Field.Root>
    </>
  );
}

function CaseList({
  cases,
  colleagues,
}: {
  cases: readonly Case[];
  colleagues: readonly FirmColleague[];
}) {
  const theme = useTheme();
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  // A subject the directory does not carry still renders — as the subject. A
  // case opened by somebody since removed from the firm is history, and hiding
  // who opened it would be a worse answer than an unfamiliar id.
  const openedBy = (subject: string) =>
    colleagues.find((colleague) => colleague.subject === subject)?.displayName ?? subject;

  if (cases.length === 0) {
    return <Text style={[styles.body, muted]}>No cases yet. Open one above to get started.</Text>;
  }

  return (
    /*
      A TABLE, and one link per row.

      Each row used to carry six links — intake, team, documents, packet,
      creditor matrix, review — because `/cases/<id>` did not exist and there
      was nothing else for a row to point at. At 44dp apiece that made a list of
      nine cases some 2,400px of stacked navigation, and it made this screen the
      only route into any case screen: moving from intake to documents meant
      coming back here and finding the row again. The six now live in the case's
      own rail (`CaseShell`), which leaves a row free to be a row.

      `Table` from the design system rather than Views: its native leaf asserts
      `table`/`row`/`columnheader`/`cell` by hand, which is exactly the wiring
      that is easy to get wrong and impossible to see.
    */
    <Table.Root dense>
      <Table.Head>
        <Table.Row>
          <Table.HeaderCell>Case</Table.HeaderCell>
          <Table.HeaderCell width={90}>Chapter</Table.HeaderCell>
          <Table.HeaderCell width={90}>District</Table.HeaderCell>
          <Table.HeaderCell width={140}>Status</Table.HeaderCell>
          <Table.HeaderCell width={150}>Opened</Table.HeaderCell>
        </Table.Row>
      </Table.Head>
      <Table.Body>
        {cases.map((item) => (
          <Table.Row key={item.id}>
            <Table.Cell>
              {/*
                The row's ONE link, and the accessible name carries the case
                rather than repeating "Open case" nine times — WCAG 2.4.4 is
                about a link making sense out of context. The visible text is
                the start of that name, which is what 2.5.3 asks for.

                It is a `Link` and not a pressable row because these render real
                `<a href>`s: middle-click and open-in-new-tab are how anyone
                works two cases at once.
              */}
              <Link
                href={`/cases/${item.id}`}
                aria-label={`Chapter ${item.chapter} case in ${item.district}, opened ${item.createdAt.slice(0, 10)} by ${openedBy(item.createdBy)}`}
                style={[
                  styles.caseLink,
                  { color: theme.colors.primary, fontFamily: theme.typography.body },
                ]}
              >
                Chapter {item.chapter} · {item.district}
              </Link>
            </Table.Cell>
            <Table.Cell width={90}>{String(item.chapter)}</Table.Cell>
            <Table.Cell width={90}>{item.district}</Table.Cell>
            <Table.Cell width={140}>
              <Badge intent={STATUS_INTENT[item.status]} size="sm">
                {STATUS_LABEL[item.status]}
              </Badge>
            </Table.Cell>
            <Table.Cell width={150}>
              <Text style={[styles.caseMeta, muted]}>
                {item.createdAt.slice(0, 10)}
                {'\n'}
                {openedBy(item.createdBy)}
              </Text>
            </Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table.Root>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    marginTop: spacing.xs,
  },
  body: {
    fontSize: fontSizes.body,
    lineHeight: fontSizes.body * 1.5,
  },
  caseLink: {
    fontSize: fontSizes.label,
    fontWeight: '600',
    // 44dp, the WCAG 2.5.5 target size this app enforces on anything
    // pressable — a text link is no exception.
    lineHeight: 44,
  },
  caseMeta: {
    fontSize: fontSizes.caption,
  },
  chapterLabel: {
    fontSize: fontSizes.label,
  },
  chapterOption: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  chapters: {
    // Overrides the Root's own column default — four short options read better
    // across than stacked, and wrap when the column is narrow.
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.md,
  },
  error: {
    fontSize: fontSizes.label,
  },
  form: {
    gap: spacing.md,
    marginBottom: spacing.lg,
    marginTop: spacing.sm,
  },
});
