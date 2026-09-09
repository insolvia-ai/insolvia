import { StyleSheet, Text } from 'react-native';

import { chromeColors, fontSizes, useTheme } from '@/theme';

export interface WordmarkProps {
  /** Font size in dp. Defaults to the app header's size. */
  size?: number;
  /**
   * Drawn on the app's chrome, which is dark in both schemes: takes its ink
   * and its accent from `chromeColors` rather than from the active scheme,
   * so it does not vanish into the header in light mode.
   */
  onChrome?: boolean;
}

/**
 * The Insolvia wordmark: "Insolvia" in brand ink with a brass accent dot.
 *
 * Themed, so it responds to light/dark and to any brand-color change. The
 * nested `Text` is what tints the dot.
 *
 * THE ONE PLACE THE SERIF APPEARS. `typography.wordmark` is Cormorant
 * Garamond, the face brand/wordmark.svg is cut from; every heading in the app
 * is set in the same sans as the text around it. That is a brand decision —
 * brand/fonts.json owns the reasoning — and this component is its only reader,
 * so a heading that wants the serif has to reach past the role that says so.
 *
 * Not a heading: the wordmark is the site identity, and marking it up as one
 * would put it in the document outline ahead of every page's real `<h1>`.
 */
export function Wordmark({ size = fontSizes.wordmark, onChrome = false }: WordmarkProps) {
  const theme = useTheme();
  const ink = onChrome ? chromeColors.ink : theme.colors.brand;
  const accent = onChrome ? chromeColors.accent : theme.colors.accent;

  return (
    <Text
      style={[styles.base, { color: ink, fontFamily: theme.typography.wordmark, fontSize: size }]}
    >
      Insolvia
      <Text style={{ color: accent }}>.</Text>
    </Text>
  );
}

const styles = StyleSheet.create({
  base: {
    // 700, and only 700: Cormorant is drawn for large sizes and thins out fast
    // below it — see brand/fonts.json. It is the one 700 the app loads.
    fontWeight: '700',
    letterSpacing: -0.5,
  },
});
