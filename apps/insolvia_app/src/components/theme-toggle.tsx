import { Pressable, StyleSheet, Text } from 'react-native';

import { fontSizes, useTheme, useThemePreference } from '@/theme';
import type { ThemePreference } from '@/theme';

/**
 * What each state looks like and what pressing it does next.
 *
 * THREE STATES, NOT TWO, and the third is the point: `system` is what a device
 * that switches to dark in the evening needs, and a plain light/dark toggle
 * cannot express it. The cost is that a cycling control is harder to announce
 * — hence `label`, which says both where you are and where you are going.
 * WCAG 2.4.6 is about the name describing the purpose, and for a control whose
 * purpose changes with its state that means the name has to change too.
 */
const CYCLE: Readonly<
  Record<
    ThemePreference,
    { readonly next: ThemePreference; readonly glyph: string; readonly label: string }
  >
> = {
  system: { next: 'light', glyph: '◐', label: 'Theme: following your device. Switch to light.' },
  light: { next: 'dark', glyph: '☀', label: 'Theme: light. Switch to dark.' },
  dark: { next: 'system', glyph: '☾', label: 'Theme: dark. Follow your device instead.' },
};

/**
 * The header's light/dark control.
 *
 * App-local rather than from the design system, and correctly so: the package
 * has no icon-button surface, and this one is bound to an app-level preference
 * the package knows nothing about. It is chrome, which is what
 * `components/` is for.
 *
 * The glyph is `aria-hidden` and the accessible name comes entirely from
 * `aria-label`, the same rule the home screen's CTA follows — a screen reader
 * announcing "sun" would be describing the picture rather than the action.
 *
 * 44×44 minimum: the WCAG 2.5.5 target-size floor this app enforces
 * everywhere, and the reason the package's `md` button is not used anywhere
 * either.
 */
export function ThemeToggle() {
  const theme = useTheme();
  const { preference, setPreference } = useThemePreference();
  const { next, glyph, label } = CYCLE[preference];

  return (
    <Pressable
      accessibilityRole="button"
      aria-label={label}
      onPress={() => {
        setPreference(next);
      }}
      style={({ pressed }) => [
        styles.button,
        {
          borderColor: theme.colors.line,
          borderRadius: theme.radii.sm,
          backgroundColor: pressed ? theme.colors.surfaceAlt : 'transparent',
        },
      ]}
    >
      <Text aria-hidden style={[styles.glyph, { color: theme.colors.muted }]}>
        {glyph}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    alignItems: 'center',
    borderWidth: 1,
    // The 44dp WCAG 2.5.5 floor. The glyph is small; the target is not.
    height: 44,
    justifyContent: 'center',
    width: 44,
  },
  glyph: {
    fontSize: fontSizes.body,
    // Centres the glyph optically — several of these sit high in their em box.
    lineHeight: fontSizes.body * 1.2,
  },
});
