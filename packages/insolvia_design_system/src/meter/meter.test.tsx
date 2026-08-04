import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Meter } from './meter';

describe('Meter', () => {
  it('exposes role="meter" with min/max/now', () => {
    render(
      <Meter.Root value={30} min={0} max={60}>
        <Meter.Track>
          <Meter.Indicator />
        </Meter.Track>
      </Meter.Root>,
    );

    const meter = screen.getByRole('meter');
    expect(meter).toHaveAttribute('aria-valuemin', '0');
    expect(meter).toHaveAttribute('aria-valuemax', '60');
    expect(meter).toHaveAttribute('aria-valuenow', '30');
  });

  it('defaults min to 0 when omitted', () => {
    render(<Meter.Root value={10} />);

    expect(screen.getByRole('meter')).toHaveAttribute('aria-valuemin', '0');
  });

  it('sizes the indicator width to the value/range percentage', () => {
    render(
      <Meter.Root value={45} min={0} max={90}>
        <Meter.Indicator data-testid="indicator" />
      </Meter.Root>,
    );

    expect(screen.getByTestId('indicator')).toHaveStyle({ width: '50%' });
  });
});
