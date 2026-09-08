import { render, screen } from '@testing-library/react-native';
import { StyleSheet } from 'react-native';

import { Heading } from '@/components/heading';

describe('Heading', () => {
  it('carries the level it was given, which is what becomes <h1>–<h6>', () => {
    render(<Heading level={2}>Your case workspace</Heading>);

    const heading = screen.getByRole('heading', { name: 'Your case workspace' });
    expect(heading.props['aria-level']).toBe(2);
  });

  it('never derives the level from the visual size', () => {
    // Mapping size→tag would let choosing a smaller heading for looks silently
    // break the document outline (`heading-order`). Here the two are
    // independent: a level-1 heading rendered at body size is still an <h1>.
    render(
      <Heading level={1} size="body">
        Small but still the page heading
      </Heading>,
    );

    expect(screen.getByRole('heading').props['aria-level']).toBe(1);
  });

  // The RELATIONSHIP, not the numbers. Cormorant arrives tight because it is
  // drawn for large sizes, so the tightening that flatters a 34px display
  // heading closes the letters up at 16px. This once shared one value across
  // every size, which read worst on a case name — an arbitrary string, where a
  // run of narrow letters became a smear. Asserting the ordering rather than
  // the values leaves the tuning free to move.
  it('loosens the tracking as the heading gets smaller', () => {
    const trackingAt = (size: 'display' | 'section' | 'body') => {
      render(
        <Heading level={2} size={size}>
          Probemtpvbjkj
        </Heading>,
      );
      const { letterSpacing } = StyleSheet.flatten(screen.getByRole('heading').props.style);
      screen.unmount();
      return letterSpacing as number;
    };

    expect(trackingAt('display')).toBeLessThan(trackingAt('section'));
    expect(trackingAt('section')).toBeLessThan(trackingAt('body'));
  });
});
