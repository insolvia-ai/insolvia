import { render, screen } from '@testing-library/react-native';

import { Heading } from '@/components/heading';

describe('Heading', () => {
  it('carries the level it was given, which is what becomes <h1>–<h6>', () => {
    render(<Heading level={2}>Your case workspace</Heading>);

    const heading = screen.getByRole('heading', { name: 'Your case workspace' });
    expect(heading.props['aria-level']).toBe(2);
  });

  it('never derives the level from the visual size', () => {
    // The rejected library mapped size→tag, so choosing a smaller heading for
    // looks silently broke the document outline (`heading-order`). Here the two
    // are independent: a level-1 heading rendered at body size is still an <h1>.
    render(
      <Heading level={1} size="body">
        Small but still the page heading
      </Heading>,
    );

    expect(screen.getByRole('heading').props['aria-level']).toBe(1);
  });
});
