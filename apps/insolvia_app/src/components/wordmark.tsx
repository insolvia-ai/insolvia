import { StyleSheet, Text } from 'react-native';

import { fontSizes, useTheme } from '@/theme';

export interface WordmarkProps {
  /** Font size in dp. Defaults to the app header's size. */
  size?: number;
}

/**
 * The Insolvia wordmark: "Insolvia" in brand ink with a brass accent dot.
 *
 * A placeholder identity until the real logo lands, but themed, so it already
 * responds to light/dark and to any brand-color change. Ported from the Flutter
 * `BrandWordmark`; the nested `Text` is React Native's equivalent of the
 * `TextSpan` that tinted the dot.
 *
 * Not a heading: the wordmark is the site identity, and marking it up as one
 * would put it in the document outline ahead of every page's real `<h1>`.
 */
export function Wordmark({ size = fontSizes.wordmark }: WordmarkProps) {
  const theme = useTheme();

  return (
    <Text
      style={[
        styles.base,
        { color: theme.colors.brand, fontFamily: theme.typography.heading, fontSize: size },
      ]}
    >
      Insolvia
      <Text style={{ color: theme.colors.accent }}>.</Text>
    </Text>
  );
}

const styles = StyleSheet.create({
  base: {
    fontWeight: '700',
    letterSpacing: -0.5,
  },
});
