import type { OutputOptionsRequest, SignaturePagesMode } from '@insolvia-ai/api-client';
import { Checkbox, CheckboxGroup, RadioGroup } from '@insolvia-ai/design-system';
import { StyleSheet, Text, View } from 'react-native';

import { Heading } from '@/components/heading';
import { fontSizes, spacing, useTheme } from '@/theme';

/**
 * The output options (issue 13.11) a screen collects before triggering a
 * render — packet assembly's job body and the single-form preview's query
 * string are both built from this same shape, via {@link outputOptionsRequestFrom}.
 * Always the full shape, never partial: a screen's local state is simpler
 * when every field has a value, and the api-client's own request builders
 * already do the "send only what differs from the default" work.
 */
export interface OutputOptionsValue {
  readonly draftWatermark: boolean;
  readonly printDate: boolean;
  readonly signaturePages: SignaturePagesMode;
  readonly signElectronically: boolean;
}

/** The plain filing set — every option off, nothing this screen has not always produced. */
export const DEFAULT_OUTPUT_OPTIONS: OutputOptionsValue = {
  draftWatermark: false,
  printDate: false,
  signaturePages: 'all',
  signElectronically: false,
};

/**
 * {@link OutputOptionsValue} as the request shape {@link InsolviaApiClient}'s
 * `acceptCaseJob`/`getCaseFormPreview` take — a field is included only when
 * it differs from {@link DEFAULT_OUTPUT_OPTIONS}, mirroring this package's
 * own "omit optional fields at their default" rule
 * (`packages/insolvia_api_client/CLAUDE.md`). At the plain filing set this
 * returns `{}`, which is what keeps a preview's URL free of a query string —
 * and a packet-assembly accept free of an `options` body key — when nothing
 * was actually asked for, exactly as before this feature existed.
 */
export function outputOptionsRequestFrom(value: OutputOptionsValue): OutputOptionsRequest {
  const request: { -readonly [K in keyof OutputOptionsRequest]?: OutputOptionsRequest[K] } = {};
  if (value.draftWatermark !== DEFAULT_OUTPUT_OPTIONS.draftWatermark) {
    request.draftWatermark = value.draftWatermark;
  }
  if (value.printDate !== DEFAULT_OUTPUT_OPTIONS.printDate) {
    request.printDate = value.printDate;
  }
  if (value.signaturePages !== DEFAULT_OUTPUT_OPTIONS.signaturePages) {
    request.signaturePages = value.signaturePages;
  }
  if (value.signElectronically !== DEFAULT_OUTPUT_OPTIONS.signElectronically) {
    request.signElectronically = value.signElectronically;
  }
  return request;
}

const SIGNATURE_PAGES_OPTIONS: readonly { value: SignaturePagesMode; label: string }[] = [
  { value: 'all', label: 'Every page' },
  { value: 'only', label: 'Signature pages only' },
  { value: 'omit', label: 'Omit signature pages' },
];

/** One form this case files, for the {@link FormsSubsetSelector} checklist. */
export interface FormsSubsetOption {
  readonly value: string;
  readonly label: string;
}

/**
 * The output-options panel (issue 13.11), shared by the packet screen
 * (`screens/packet`) and the forms hub's single-form preview
 * (`screens/forms-hub`) — one place for the "Draft" watermark, the
 * top-margin date/time, signature-page selection and `/s/` electronic
 * signatures, so the two screens can never drift on what each option means
 * or how it reads.
 *
 * `forms` is the packet screen's own addition — a subset of the case's
 * filed forms to print — and is omitted entirely on the preview, which
 * already names one form in its own URL.
 */
export function OutputOptionsPanel({
  value,
  onChange,
  disabled = false,
  forms,
}: {
  readonly value: OutputOptionsValue;
  readonly onChange: (next: OutputOptionsValue) => void;
  readonly disabled?: boolean;
  readonly forms?: {
    readonly options: readonly FormsSubsetOption[];
    /** `undefined` means every form — the API's own "no subset" reading. */
    readonly selected: readonly string[] | undefined;
    readonly onChange: (next: readonly string[] | undefined) => void;
  };
}) {
  const theme = useTheme();
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };

  return (
    <View style={styles.panel}>
      <Heading level={3}>Output options</Heading>

      <ToggleRow
        checked={value.draftWatermark}
        onCheckedChange={(checked) => onChange({ ...value, draftWatermark: checked })}
        disabled={disabled}
        label='"Draft" watermark'
        description="Stamps every page so it is never mistaken for a filing."
      />

      <ToggleRow
        checked={value.printDate}
        onCheckedChange={(checked) => onChange({ ...value, printDate: checked })}
        disabled={disabled}
        label="Print date and time"
        description="Today's date and time in the top margin of every page."
      />

      <ToggleRow
        checked={value.signElectronically}
        onCheckedChange={(checked) => onChange({ ...value, signElectronically: checked })}
        disabled={disabled}
        label='Sign electronically ("/s/")'
        description="Fills each debtor's own signature line with /s/ and their name — never the attorney's."
      />

      <View style={styles.group}>
        <Text style={[styles.groupLabel, ink]}>Signature pages</Text>
        <RadioGroup.Root
          aria-label="Signature pages"
          value={value.signaturePages}
          onValueChange={(next) =>
            onChange({ ...value, signaturePages: next as SignaturePagesMode })
          }
          disabled={disabled}
          style={styles.radioGroup}
        >
          {SIGNATURE_PAGES_OPTIONS.map((option) => (
            <View key={option.value} style={styles.radioOption}>
              <RadioGroup.Item value={option.value} aria-label={option.label} hitSlop={12}>
                <RadioGroup.Indicator />
              </RadioGroup.Item>
              <Text style={[styles.radioLabel, ink]}>{option.label}</Text>
            </View>
          ))}
        </RadioGroup.Root>
      </View>

      {forms !== undefined ? (
        <FormsSubsetSelector
          options={forms.options}
          selected={forms.selected}
          onChange={forms.onChange}
          disabled={disabled}
        />
      ) : null}

      <Text style={[styles.note, muted]}>
        The plain filing set — every option off — is exactly what "Assemble packet" has always
        produced.
      </Text>
    </View>
  );
}

function ToggleRow({
  checked,
  onCheckedChange,
  disabled,
  label,
  description,
}: {
  readonly checked: boolean;
  readonly onCheckedChange: (checked: boolean) => void;
  readonly disabled: boolean;
  readonly label: string;
  readonly description: string;
}) {
  const theme = useTheme();
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const muted = { color: theme.colors.muted, fontFamily: theme.typography.body };
  return (
    <View style={styles.toggleRow}>
      <Checkbox.Root
        checked={checked}
        onCheckedChange={onCheckedChange}
        disabled={disabled}
        aria-label={label}
      >
        <Checkbox.Indicator>✓</Checkbox.Indicator>
      </Checkbox.Root>
      <View style={styles.toggleText}>
        <Text aria-hidden style={[styles.toggleLabel, ink]}>
          {label}
        </Text>
        <Text style={[styles.toggleDescription, muted]}>{description}</Text>
      </View>
    </View>
  );
}

/**
 * The packet screen's own addition: which of the case's filed forms to
 * include. Every option checked (the default) means "every form" — the API's
 * `forms` field is then omitted entirely ({@link outputOptionsRequestFrom}'s
 * caller does this translation, not this component), so a case whose filed
 * set changes between load and assemble never trips the "unfiled form"
 * refusal on a subset it never meant to send.
 */
function FormsSubsetSelector({
  options,
  selected,
  onChange,
  disabled,
}: {
  readonly options: readonly FormsSubsetOption[];
  readonly selected: readonly string[] | undefined;
  readonly onChange: (next: readonly string[] | undefined) => void;
  readonly disabled: boolean;
}) {
  const theme = useTheme();
  const ink = { color: theme.colors.ink, fontFamily: theme.typography.body };
  const allValues = options.map((option) => option.value);
  const checkedValues = selected ?? allValues;

  return (
    <View style={styles.group}>
      <Text style={[styles.groupLabel, ink]}>Forms to include</Text>
      <CheckboxGroup.Root
        value={[...checkedValues]}
        onValueChange={(next) => {
          onChange(allValues.every((v) => next.includes(v)) ? undefined : next);
        }}
        disabled={disabled}
      >
        <View style={styles.checkboxGroup}>
          {options.map((option) => (
            <View key={option.value} style={styles.toggleRow}>
              <Checkbox.Root value={option.value} aria-label={option.label} disabled={disabled}>
                <Checkbox.Indicator>✓</Checkbox.Indicator>
              </Checkbox.Root>
              <Text aria-hidden style={[styles.toggleLabel, ink]}>
                {option.label}
              </Text>
            </View>
          ))}
        </View>
      </CheckboxGroup.Root>
    </View>
  );
}

const styles = StyleSheet.create({
  checkboxGroup: {
    gap: spacing.xs,
  },
  group: {
    gap: spacing.xs,
    marginTop: spacing.sm,
  },
  groupLabel: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  note: {
    fontSize: fontSizes.caption,
    marginTop: spacing.sm,
  },
  panel: {
    gap: spacing.sm,
    marginTop: spacing.sm,
  },
  radioGroup: {
    gap: spacing.sm,
  },
  radioLabel: {
    fontSize: fontSizes.label,
  },
  radioOption: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  toggleDescription: {
    fontSize: fontSizes.caption,
  },
  toggleLabel: {
    fontSize: fontSizes.label,
    fontWeight: '600',
  },
  toggleRow: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.sm,
  },
  toggleText: {
    flexShrink: 1,
    gap: spacing.xs / 4,
  },
});
