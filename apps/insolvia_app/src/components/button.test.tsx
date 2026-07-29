import { render, screen, userEvent } from '@testing-library/react-native';

import { Button } from '@/components/button';

describe('Button', () => {
  it('is announced as a button, named by its label', () => {
    // `role="button"` is what react-native-web turns into a real
    // <button type="button"> — keyboard-focusable and Enter/Space activated,
    // which a Pressable without it is not.
    render(<Button label="Start a case" onPress={() => {}} />);

    expect(screen.getByRole('button', { name: 'Start a case' })).toBeTruthy();
  });

  it('calls onPress when pressed', async () => {
    const onPress = jest.fn();
    render(<Button label="Start a case" onPress={onPress} />);

    await userEvent.press(screen.getByRole('button', { name: 'Start a case' }));

    expect(onPress).toHaveBeenCalledTimes(1);
  });

  it('keeps the decorative icon out of the accessible name', () => {
    // The glyph is presentation only; if it leaked into the name, a screen
    // reader would announce "Start a case right arrow".
    render(<Button label="Start a case" icon="→" onPress={() => {}} />);

    expect(screen.getByRole('button', { name: 'Start a case' })).toBeTruthy();
  });

  it('does not fire, and reports itself disabled, when disabled', async () => {
    const onPress = jest.fn();
    render(<Button label="Open a case" disabled onPress={onPress} />);

    const button = screen.getByRole('button', { name: 'Open a case' });
    expect(button).toBeDisabled();

    await userEvent.press(button);
    expect(onPress).not.toHaveBeenCalled();
  });
});
