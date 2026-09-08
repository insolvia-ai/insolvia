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
    fontWeight: '700',
  },
  // TRACKING BELONGS TO THE SIZE, NOT TO THE COMPONENT. Cormorant is drawn for
  // large sizes (brand/fonts.json), so it arrives already tight; the amount of
  // correction that flatters it at 34px closes the letters up at 16px, where it
  // is being used far below its optical size. A single value in `base` applied
  // display-scale tightening to every heading — worst on `body`, where a case
  // name like "Probemtpvbjkj" ran its letters together.
  display: {
    fontSize: fontSizes.display,
    letterSpacing: -0.5,
  },
  section: {
    fontSize: fontSizes.section,
    letterSpacing: -0.2,
  },
  body: {
    fontSize: fontSizes.body,
    // POSITIVE, and not a typo: below about 20px this face needs opening up
    // rather than tightening.
    letterSpacing: 0.2,
  },
});
