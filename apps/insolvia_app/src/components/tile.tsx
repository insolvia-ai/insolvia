import { StyleSheet, Text, View } from 'react-native';

import { chromeColors, fontSizes, useTheme } from '@/theme';

export interface TileProps {
  /** One character; anything longer is cut to its first. */
  children: string;
  /**
   * `mark` is an identity — the app's own "I", the signed-in person's
   * initial: ivory square, near-black letter, the favicon's tile inverted for
   * a dark ground. `nav` is a row's icon and recedes: a step up from the rail
   * with an ivory letter, so a column of them reads as a list, not a grid of
   * badges.
   */
  tone: 'mark' | 'nav';
  /** The square's side, in dp. */
  size?: number;
}

/**
 * A letter in a small square — the rail's icon, and the app's icon tile in
 * miniature: the letter set in the wordmark's serif, the way `brand/icon.svg`
 * sets the "I".
 *
 * NOT AN ICON SET, on purpose. The app has no icon library and adding one is
 * a dependency decision ADR 0004 asks to be measured first; a tile carrying an
 * initial is what the rail shows instead, and set in the brand's own face it
 * reads as a monogram rather than a missing glyph. Should an icon set arrive,
 * `nav` is the one tone to swap it in for — `mark` is a monogram by design.
 *
 * `accessible={false}` keeps the letter out of every accessible name; the
 * control around a tile names itself in full.
 */
export function Tile({ children, tone, size = 24 }: TileProps) {
  const theme = useTheme();
  const ground = tone === 'mark' ? chromeColors.ink : chromeColors.surfaceAlt;
  const letter = tone === 'mark' ? chromeColors.bg : chromeColors.ink;
  return (
    <View
      accessible={false}
      style={[
        styles.tile,
        { backgroundColor: ground, borderRadius: theme.radii.sm, height: size, width: size },
      ]}
    >
      <Text
        style={[
          styles.letter,
          {
            color: letter,
            fontFamily: theme.typography.wordmark,
            // Two thirds of the square, which is where the serif's caps sit
            // with a little air above and below at every size this is used at.
            fontSize: Math.round(size * 0.66),
            lineHeight: size,
          },
        ]}
      >
        {(children[0] ?? '').toUpperCase()}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  letter: {
    // 700, the wordmark's weight and the one weight of Cormorant the app
    // loads — see brand/fonts.json.
    fontWeight: '700',
    // The scale's caption step is the floor; the size prop overrides above.
    fontSize: fontSizes.caption,
  },
  tile: {
    alignItems: 'center',
    justifyContent: 'center',
  },
});
