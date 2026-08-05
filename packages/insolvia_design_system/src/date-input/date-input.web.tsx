// WEB LEAF — plain React DOM + Tailwind. A masked text input, not
// `<input type="date">`: the browser control renders a calendar the native
// leaf cannot match, formats to the browser locale rather than the one the
// form asks for, and is the one input whose appearance cannot be styled
// consistently. The mask, the calendar check and the range check are all in
// date-input.props, shared verbatim with the native leaf.
//
// Reads Field's context when there is one, exactly as Select does.
import * as React from 'react';

import { FieldContext } from '../field/field.props';
import { cn } from '../lib/cn';
import { focusRing } from '../lib/styles';
import {
  DATE_FORMAT,
  isErrorStatus,
  useDateInputState,
  type DateInputOwnProps,
} from './date-input.props';

// `min`/`max` are Omit-ed from the input props because React declares them as
// `string | number` for numeric and native-date inputs, and here they are ISO
// date strings that the shared range check compares lexicographically — the
// date meaning has to win. Same reasoning as Tabs.Root's `defaultValue`.
export interface DateInputProps
  extends
    Omit<
      React.ComponentPropsWithoutRef<'input'>,
      'value' | 'defaultValue' | 'onChange' | 'type' | 'min' | 'max'
    >,
    DateInputOwnProps {}

export const DateInput = React.forwardRef<HTMLInputElement, DateInputProps>(
  (
    {
      className,
      value,
      defaultValue,
      onValueChange,
      min,
      max,
      disabled = false,
      name: nameProp,
      placeholder = DATE_FORMAT,
      ...props
    },
    ref,
  ) => {
    const field = React.useContext(FieldContext);
    const { text, setText, status, iso } = useDateInputState({
      value,
      defaultValue,
      onValueChange,
      min,
      max,
    });

    const invalid = isErrorStatus(status) || (field?.invalid ?? false);
    const name = nameProp ?? field?.name;

    return (
      <>
        <input
          ref={ref}
          type="text"
          id={field?.controlId}
          // `numeric` rather than `decimal` or `tel`: it puts a digit keypad on
          // a phone without offering a decimal point this can never accept.
          inputMode="numeric"
          // A date is not a thing browsers should be filling from a saved
          // address, and an autofilled value would bypass the mask entirely.
          autoComplete="off"
          value={text}
          placeholder={placeholder}
          disabled={disabled}
          aria-describedby={field?.describedBy}
          aria-invalid={invalid ? true : undefined}
          onChange={(event) => setText(event.target.value)}
          className={cn(
            'h-10 w-full rounded-md border border-line bg-card px-sm font-body text-sm text-ink',
            'placeholder:text-muted',
            focusRing,
            'disabled:cursor-not-allowed disabled:bg-surface-alt disabled:text-muted',
            invalid && 'border-danger',
            className,
          )}
          {...props}
        />
        {/* The visible input carries no name on purpose: mid-typing its value
            is "2019-0", and a form post should never receive that. What is
            submitted is the ISO date or nothing at all. */}
        {name === undefined ? null : <input type="hidden" name={name} value={iso} />}
      </>
    );
  },
);
DateInput.displayName = 'DateInput';
