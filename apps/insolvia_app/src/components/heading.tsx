import type { ReactNode } from 'react';
import { StyleSheet, Text } from 'react-native';
import type { StyleProp, TextProps, TextStyle } from 'react-native';

import { fontSizes, useTheme } from '@/theme';

/** Document-structure level. Emits `<h1>`–`<h6>` on web. */
export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6;

/** Visual size, deliberately independent of {@link HeadingLevel}. */
export type HeadingSize = 'display' | 'section' | 'body';

export interface HeadingProps {
  /**
   * The heading level, chosen for **document structure** and nothing else.
   *
   * Required, and never inferred from `size`: deriving the tag from the visual
   * size lets picking a smaller heading for looks silently break the document
   * outline (`heading-order`). Skipping a level (an `<h1>` followed by an
   * `<h3>`) is still a defect this component cannot catch for you; the axe
   * assertion in CI is what catches it.
   */
  level: HeadingLevel;

  /**
   * How large the heading looks. Defaults from `level`, which is the safe
   * direction — structure choosing appearance, never the reverse.
   */
  size?: HeadingSize;

  children: ReactNode;
  style?: StyleProp<TextStyle>;
}

function defaultSize(level: HeadingLevel): HeadingSize {
  if (level === 1) return 'display';
  if (level === 2) return 'section';
  return 'body';
}

/**
 * A heading. `role="heading"` + `aria-level` is what react-native-web maps to a
 * real `<h1>`–`<h6>` element (`propsToAccessibilityComponent.js`), so the
 * document outline a screen reader and Lighthouse see is the one written here.
 *
 * SET IN THE SAME FACE AS THE TEXT AROUND IT. `typography.heading` is Open
 * Sans, like `body`, and that is the brand's decision rather than an accident
 * of the generator (brand/fonts.json says why). What makes this a heading is
 * therefore weight, size and the space around it — not a change of family —
 * which is how every platform's own UI type builds hierarchy, and what stops
 * a card title reading as a decoration.
 */
export function Heading({ level, size, children, style }: HeadingProps) {
  const theme = useTheme();
  const resolved = size ?? defaultSize(level);

  // react-native's TypeScript definitions declare `role` but not `aria-level`,
  // which is web-only — react-native-web reads it at runtime and emits
  // `<h1 aria-level="1" role="heading">` (verified on the built export). One
  // documented assertion here is better than loosening the props of every call
  // site, and it is contained to the component that owns the semantics.
  const headingProps = { role: 'heading', 'aria-level': level } as TextProps;

  return (
    <Text
      {...headingProps}
      style={[
        styles.base,
        styles[resolved],
        { color: theme.colors.brand, fontFamily: theme.typography.heading },
        style,
      ]}
    >
      {children}
    </Text>
  );
}

// Sizes come from the type scale in `@/theme`, not from literals: a
// `StyleSheet.create` block runs once at module load and so cannot call
// `useTheme()`, but the scale does not vary by color scheme — only the colors
// do, and those are applied above.
const styles = StyleSheet.create({
  base: {
    // 600, not 700. Semibold is the heaviest weight the app loads
    // (public/index.html), and at UI sizes it is the right one: bold over a
    // 400 body adds bulk without adding hierarchy, and a 600 title beside a
    // 600 button label reads as one system. Every emphasised label in the app
    // uses the same value, so a heading is distinguished by size and space.
    fontWeight: '600',
  },
  // TRACKING BELONGS TO THE SIZE, NOT TO THE COMPONENT. Type is tightened as it
  // grows and left alone as it shrinks: at display size the letters of a sans
  // drift apart and want pulling in (about -0.01em), at body size Open Sans's
  // own spacing is already right for reading, and a single value for all
  // sizes is wrong at one end or the other.
  //
  // LEADING RUNS THE OTHER WAY. Without a `lineHeight` react-native-web falls
  // back to `line-height: normal`, about 1.36 of the size for Open Sans at
  // every size. Large text wants its lines close — the eye reads a two-line
  // title as one shape — and small text wants them open. Body-size headings
  // take body copy's 1.5, so a card title shares the rhythm of the rows under
  // it instead of sitting a few pixels short of them.
  display: {
    fontSize: fontSizes.display,
    letterSpacing: -0.3,
    lineHeight: 34,
  },
  section: {
    fontSize: fontSizes.section,
    letterSpacing: -0.15,
    lineHeight: 28,
  },
  body: {
    fontSize: fontSizes.body,
    letterSpacing: 0,
    lineHeight: fontSizes.body * 1.5,
  },
});
