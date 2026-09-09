import { StyleSheet, Text, View } from 'react-native';

import { chromeColors, fontSizes, useTheme } from '@/theme';

export interface TileProps {
  /** One character; anything longer is cut to its first. */
  children: string;
  /** The square's side, in dp. */
  size?: number;
}

/**
 * A letter on a small ivory square — an IDENTITY mark: the app's own "I" at
 * the head of the collapsed rail, the signed-in person's initial on the
 * account row. It is the favicon's tile inverted for a dark ground, with the
 * letter set in the wordmark's serif, the way `brand/icon.svg` sets the "I".
 *
 * For identities only. A row's icon is an `Icon` — a line glyph — and a
 * letter in a square was tried there first: a column of monograms read as a
 * grid of badges, and "H" says less about Home than a house does.
 *
 * `accessible={false}` keeps the letter out of every accessible name; the
 * control around a tile names itself in full.
 */
export function Tile({ children, size = 24 }: TileProps) {
  const theme = useTheme();
  return (
    <View
      accessible={false}
      style={[
        styles.tile,
        {
          backgroundColor: chromeColors.ink,
          borderRadius: theme.radii.sm,
          height: size,
          width: size,
        },
      ]}
    >
      <Text
        style={[
          styles.letter,
          {
            color: chromeColors.bg,
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
