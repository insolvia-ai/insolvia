// jsdom never actually loads an <img> (no network stack), so the only way to
// exercise the load/error branches of the status state machine is to fire the
// events on the element directly, as if the browser had.
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { Avatar } from './avatar';

describe('Avatar', () => {
  it('shows the fallback before the image has resolved', () => {
    render(
      <Avatar.Root>
        <Avatar.Image src="https://example.com/andreas.jpg" alt="Andreas Savva" />
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    expect(screen.getByText('AS')).toBeVisible();
  });

  it('hides the fallback once the image reports it loaded', () => {
    render(
      <Avatar.Root>
        <Avatar.Image src="https://example.com/andreas.jpg" alt="Andreas Savva" />
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    fireEvent.load(screen.getByAltText('Andreas Savva'));

    expect(screen.queryByText('AS')).not.toBeInTheDocument();
  });

  it('keeps showing the fallback when the image fails to load', () => {
    render(
      <Avatar.Root>
        <Avatar.Image src="https://example.com/broken.jpg" alt="Andreas Savva" />
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    fireEvent.error(screen.getByAltText('Andreas Savva'));

    expect(screen.getByText('AS')).toBeVisible();
  });

  it('shows the fallback when there is no image at all', () => {
    render(
      <Avatar.Root>
        <Avatar.Fallback>AS</Avatar.Fallback>
      </Avatar.Root>,
    );

    expect(screen.getByText('AS')).toBeVisible();
  });
});
