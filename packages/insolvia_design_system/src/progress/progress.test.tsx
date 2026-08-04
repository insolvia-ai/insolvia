import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Progress } from './progress';

describe('Progress', () => {
  it('exposes role="progressbar" with min/max/now for a determinate value', () => {
    render(
      <Progress.Root value={40} max={80}>
        <Progress.Track>
          <Progress.Indicator />
        </Progress.Track>
      </Progress.Root>,
    );

    const bar = screen.getByRole('progressbar');
    expect(bar).toHaveAttribute('aria-valuemin', '0');
    expect(bar).toHaveAttribute('aria-valuemax', '80');
    expect(bar).toHaveAttribute('aria-valuenow', '40');
  });

  it('omits aria-valuenow entirely when indeterminate (value is null)', () => {
    render(
      <Progress.Root value={null}>
        <Progress.Track>
          <Progress.Indicator />
        </Progress.Track>
      </Progress.Root>,
    );

    const bar = screen.getByRole('progressbar');
    expect(bar).not.toHaveAttribute('aria-valuenow');
  });

  it('defaults to indeterminate when value is omitted', () => {
    render(<Progress.Root />);

    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow');
  });

  it('sizes the indicator width to the value/max percentage', () => {
    render(
      <Progress.Root value={25} max={100}>
        <Progress.Indicator data-testid="indicator" />
      </Progress.Root>,
    );

    expect(screen.getByTestId('indicator')).toHaveStyle({ width: '25%' });
  });
});
