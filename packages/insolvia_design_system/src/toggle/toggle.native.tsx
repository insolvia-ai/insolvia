// NATIVE LEAF — React Native primitives over @insolvia-ai/tokens. Shares the
// exact pressed-state model with the web leaf (toggle.props); what is
// reimplemented here is the a11y surface. react-native's own AccessibilityRole
// union DOES include `'togglebutton'`, but this package's only shipped target
// is app-web (react-native-web) — verified empirically against this repo's
// pinned react-native-web@0.21.2: it maps `'togglebutton'` straight through
// as a non-standard `role="togglebutton"`, which is not a real WAI-ARIA role
// (unlike `'switch'`/`'radio'`, which ARE, despite the same map not listing
// them either) and so gets NO computed accessible name from the Text child.
// `accessibilityRole` stays `'button'` instead — a role react-native-web
// resolves correctly, with the normal accessible-name-from-content rule.
// Pressed/disabled are reported BOTH ways, the same split
// radio-group.native.tsx and switch.native.tsx document: `accessibilityState`
// for real native platforms, and `aria-pressed` for react-native-web, which
// is what actually ships (ADR 0004 — web is the only target). This split
// matters because react-native-web's DOM prop translation in the installed
// version does NOT flatten `accessibilityState` into DOM attributes at all —
// relying on it alone would ship a web toggle with no pressed state exposed.
// Unlike `aria-checked`/`aria-disabled` (typed RN props those two leaves use
// directly), `aria-pressed` has no RN type at all, so it needs the same
// contained, documented cast Field's native leaf uses for
// `aria-describedby`/`aria-invalid`.
import * as React from 'react';
import { Pressable, StyleSheet, Text, type PressableProps } from 'react-native';

import { radii, spacing } from '@insolvia-ai/tokens';

import { useNativeColors } from '../lib/native-theme';
import { useToggleState, type ToggleOwnProps } from './toggle.props';

// `disabled` is Omit-ed from PressableProps: it declares `boolean | null |
// undefined`, ToggleOwnProps declares `boolean | undefined` — extending both
// interfaces with the same member name at two different (if overlapping)
// types is a hard TS error, not a style choice, so the component's own
// narrower type wins outright (the AccordionRootProps `defaultValue` Omit on
// the web leaf is the same idiom, for the same reason). `onPress` is
// Omit-ed too: this component owns press handling to drive the toggle.
export interface ToggleProps extends Omit<PressableProps, 'disabled' | 'onPress'>, ToggleOwnProps {
  children?: React.ReactNode;
}

export function Toggle({
  pressed,
  defaultPressed,
  onPressedChange,
  disabled,
  value,
  children,
  style,
  ...props
}: ToggleProps) {
  const state = useToggleState(pressed, defaultPressed, onPressedChange, disabled, value);
  // Colors resolve per render so the leaf follows the OS scheme; only the
  // scheme-independent layout lives at module level in StyleSheet.create.
  const c = useNativeColors();

  // Web-only ARIA attribute, not in react-native's PressableProps type but
  // forwarded verbatim by react-native-web — see the file header.
  const webAria = { 'aria-pressed': state.pressed } as PressableProps;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: state.pressed, disabled: state.disabled }}
      {...webAria}
      disabled={state.disabled}
      onPress={() => state.toggle()}
      style={(pressableState) => [
        styles.base,
        {
          backgroundColor: state.pressed ? c.primary : 'transparent',
          opacity: state.disabled ? 0.5 : pressableState.pressed ? 0.9 : 1,
        },
        typeof style === 'function' ? style(pressableState) : style,
      ]}
      {...props}
    >
      <Text style={[styles.label, { color: state.pressed ? c.primaryText : c.ink }]}>
        {children}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  base: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: radii.md,
    paddingVertical: spacing.sm,
    paddingHorizontal: spacing.md,
  },
  label: { fontSize: 14, fontWeight: '500' },
});
