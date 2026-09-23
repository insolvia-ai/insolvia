import { render, screen, userEvent } from '@testing-library/react-native';

import {
  DEFAULT_OUTPUT_OPTIONS,
  OutputOptionsPanel,
  outputOptionsRequestFrom,
  type OutputOptionsValue,
} from '@/components/output-options-panel';

describe('OutputOptionsPanel', () => {
  it('starts at the plain filing set — every toggle off, every page kept', () => {
    render(<OutputOptionsPanel value={DEFAULT_OUTPUT_OPTIONS} onChange={() => {}} />);

    expect(
      screen.getByRole('checkbox', { name: '"Draft" watermark' }).props.accessibilityState.checked,
    ).toBe(false);
    expect(
      screen.getByRole('checkbox', { name: 'Print date and time' }).props.accessibilityState
        .checked,
    ).toBe(false);
    expect(
      screen.getByRole('checkbox', { name: 'Sign electronically ("/s/")' }).props.accessibilityState
        .checked,
    ).toBe(false);
    expect(screen.getByRole('radio', { name: 'Every page' }).props.accessibilityState.checked).toBe(
      true,
    );
  });

  it('toggling a checkbox flips only that option', async () => {
    let latest: OutputOptionsValue = DEFAULT_OUTPUT_OPTIONS;
    render(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={(next) => {
          latest = next;
        }}
      />,
    );

    await userEvent.press(screen.getByRole('checkbox', { name: '"Draft" watermark' }));

    expect(latest).toEqual({ ...DEFAULT_OUTPUT_OPTIONS, draftWatermark: true });
  });

  it('picking a signature-pages option replaces the previous choice', async () => {
    let latest: OutputOptionsValue = DEFAULT_OUTPUT_OPTIONS;
    render(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={(next) => {
          latest = next;
        }}
      />,
    );

    await userEvent.press(screen.getByRole('radio', { name: 'Omit signature pages' }));

    expect(latest.signaturePages).toBe('omit');
  });

  it('disables every control when asked', () => {
    render(<OutputOptionsPanel value={DEFAULT_OUTPUT_OPTIONS} onChange={() => {}} disabled />);

    expect(
      screen.getByRole('checkbox', { name: '"Draft" watermark' }).props.accessibilityState.disabled,
    ).toBe(true);
    expect(
      screen.getByRole('radio', { name: 'Every page' }).props.accessibilityState.disabled,
    ).toBe(true);
  });

  it('omits no forms subset UI when the caller supplies none', () => {
    render(<OutputOptionsPanel value={DEFAULT_OUTPUT_OPTIONS} onChange={() => {}} />);

    expect(screen.queryByText('Forms to include')).toBeNull();
  });

  it('the forms subset defaults to every form checked', () => {
    render(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={() => {}}
        forms={{
          options: [
            { value: 'b101', label: 'B 101' },
            { value: 'b106ab', label: 'B 106A/B' },
          ],
          selected: undefined,
          onChange: () => {},
        }}
      />,
    );

    expect(screen.getByRole('checkbox', { name: 'B 101' }).props.accessibilityState.checked).toBe(
      true,
    );
    expect(
      screen.getByRole('checkbox', { name: 'B 106A/B' }).props.accessibilityState.checked,
    ).toBe(true);
  });

  it('unchecking one form of a full set reports the remaining subset', async () => {
    let selected: readonly string[] | undefined = undefined;
    render(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={() => {}}
        forms={{
          options: [
            { value: 'b101', label: 'B 101' },
            { value: 'b106ab', label: 'B 106A/B' },
          ],
          selected,
          onChange: (next) => {
            selected = next;
          },
        }}
      />,
    );

    await userEvent.press(screen.getByRole('checkbox', { name: 'B 106A/B' }));

    expect(selected).toEqual(['b101']);
  });

  it('re-checking every form collapses the subset back to undefined ("every form")', async () => {
    let selected: readonly string[] | undefined = ['b101'];
    const onChange = (next: readonly string[] | undefined) => {
      selected = next;
    };
    const { rerender } = render(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={() => {}}
        forms={{
          options: [
            { value: 'b101', label: 'B 101' },
            { value: 'b106ab', label: 'B 106A/B' },
          ],
          selected,
          onChange,
        }}
      />,
    );

    await userEvent.press(screen.getByRole('checkbox', { name: 'B 106A/B' }));
    expect(selected).toBeUndefined();

    rerender(
      <OutputOptionsPanel
        value={DEFAULT_OUTPUT_OPTIONS}
        onChange={() => {}}
        forms={{
          options: [
            { value: 'b101', label: 'B 101' },
            { value: 'b106ab', label: 'B 106A/B' },
          ],
          selected,
          onChange,
        }}
      />,
    );
    expect(
      screen.getByRole('checkbox', { name: 'B 106A/B' }).props.accessibilityState.checked,
    ).toBe(true);
  });
});

describe('outputOptionsRequestFrom', () => {
  it('is empty at the plain filing set', () => {
    expect(outputOptionsRequestFrom(DEFAULT_OUTPUT_OPTIONS)).toEqual({});
  });

  it('carries only the fields that differ from the default', () => {
    expect(outputOptionsRequestFrom({ ...DEFAULT_OUTPUT_OPTIONS, draftWatermark: true })).toEqual({
      draftWatermark: true,
    });
    expect(outputOptionsRequestFrom({ ...DEFAULT_OUTPUT_OPTIONS, signaturePages: 'only' })).toEqual(
      { signaturePages: 'only' },
    );
  });
});
