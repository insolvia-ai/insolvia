import { render, screen } from '@testing-library/react-native';

import { Field } from '@/components/field';

/**
 * The labelling test the plan calls for by name.
 *
 * The component library this codebase rejected rendered its label as a plain
 * container with nothing tying it to the input, leaving the input with a generic
 * `aria-label="Input Field"`. For a bankruptcy petition tool that is a real
 * defect, so `Field` is the only way to render an input — and this asserts the
 * wiring it exists to guarantee.
 */
describe('Field', () => {
  it('wires the label to the input with aria-labelledby / nativeID', () => {
    render(<Field label="Case number" idPrefix="case-number" />);

    const label = screen.getByText('Case number');
    const input = screen.getByLabelText('Case number');

    expect(label.props.nativeID).toBe('case-number-label');
    expect(input.props['aria-labelledby']).toBe('case-number-label');
    // The pair is what names the input; there is no competing aria-label.
    expect(input.props['aria-label']).toBeUndefined();
  });

  it('generates unique ids so two fields on a page cannot collide', () => {
    render(
      <>
        <Field label="Debtor first name" />
        <Field label="Debtor last name" />
      </>,
    );

    const first = screen.getByLabelText('Debtor first name');
    const second = screen.getByLabelText('Debtor last name');

    expect(first.props['aria-labelledby']).toBeTruthy();
    expect(second.props['aria-labelledby']).toBeTruthy();
    expect(first.props['aria-labelledby']).not.toBe(second.props['aria-labelledby']);
  });

  it('associates a hint with aria-describedby, without displacing the label', () => {
    render(<Field label="Filing fee" hint="Dollars, no cents." idPrefix="fee" />);

    const input = screen.getByLabelText('Filing fee');

    expect(input.props['aria-describedby']).toBe('fee-hint');
    expect(screen.getByText('Dollars, no cents.').props.nativeID).toBe('fee-hint');
  });

  it('omits aria-describedby entirely when there is no hint', () => {
    render(<Field label="Filing fee" idPrefix="fee" />);

    expect(screen.getByLabelText('Filing fee').props['aria-describedby']).toBeUndefined();
  });
});
