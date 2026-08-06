// SHARED — `react` only. No react-dom, no react-native. The whole date input
// apart from the elements: the mask, the calendar check, the range check, and
// the status a leaf renders from.
//
// WHY A MASKED TEXT INPUT AND NOT A CALENDAR. The dates this has to collect are
// mostly historical and mostly far from today — "date debt incurred" on a 2016
// credit card, a pay date three months back, a prior filing in 2011. A calendar
// is the slowest possible way to enter those, and it is the single largest
// accessibility surface a form can grow. Typing eight digits is faster for a
// sighted user and unambiguous for a screen-reader user, and it needs no popup,
// no positioning and no roving focus. A picker is worth revisiting only if the
// intake grows a field where browsing dates is the point (scheduling), which
// none of the 8.1 field map is.
//
// WHY THE VALUE IS A STRING. docs/reference/case-data-model.md: a form date is
// `YYYY-MM-DD`, no time and no zone, because "date debt incurred" is a calendar
// fact rather than an instant. A `Date` is an instant — constructing one puts
// the value in a timezone it does not have, and the classic result is a date
// that shifts by a day depending on where the browser is. So no `Date` is
// constructed anywhere in this component, including for the leap-year check.
import * as React from 'react';

export const DATE_FORMAT = 'YYYY-MM-DD';

/** Digits only, capped at the eight a date has. */
function digitsOf(text: string): string {
  return text.replace(/\D/g, '').slice(0, 8);
}

function group(digits: string): string {
  // Separators appear only once there is a digit after them. Appending a
  // trailing '-' the moment the year is complete looks tidy and makes the very
  // next Backspace delete a character the user never typed.
  const year = digits.slice(0, 4);
  const month = digits.slice(4, 6);
  const day = digits.slice(6, 8);
  return [year, month, day].filter((part) => part !== '').join('-');
}

/**
 * Re-mask arbitrary text to `YYYY-MM-DD`: keep the digits, regroup, done.
 *
 * It is this short because `group` never emits a trailing separator. The usual
 * reason a masked input needs to compare against the previous text is that
 * Backspace on a value ending in '-' deletes only the separator, regrouping
 * puts it straight back, and the key looks dead. Here "2019-" is never a state
 * the field can be in, so Backspace always removes a digit and the special case
 * has nothing to fire on. An earlier version carried one anyway; it turned out
 * to be unreachable by typing AND wrong when reached — deleting a separator
 * mid-string would have dropped the LAST digit rather than the one beside the
 * caret. Deleting a separator mid-string is now simply a no-op.
 */
export function maskDate(next: string): string {
  return group(digitsOf(next));
}

function isLeapYear(year: number): boolean {
  return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
}

export function daysInMonth(year: number, month: number): number {
  if (month === 2) return isLeapYear(year) ? 29 : 28;
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

/**
 * Is this a date the calendar actually has? Computed from the numbers, never by
 * constructing a `Date` — `new Date('2019-02-30')` does not throw, it rolls
 * over to March 2nd, which is precisely the silent corruption to avoid.
 */
export function isRealDate(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12) return false;
  if (day < 1) return false;
  return day <= daysInMonth(year, month);
}

/** The ISO date for complete, real text; `null` for anything else. */
export function toIsoDate(text: string): string | null {
  const digits = digitsOf(text);
  if (digits.length !== 8) return null;
  const year = Number(digits.slice(0, 4));
  const month = Number(digits.slice(4, 6));
  const day = Number(digits.slice(6, 8));
  return isRealDate(year, month, day) ? group(digits) : null;
}

export type DateStatus =
  /** Nothing typed. Not an error — required-ness is the caller's rule. */
  | 'empty'
  /** Fewer than eight digits. Not an error *yet*; the user is still typing. */
  | 'incomplete'
  /** Eight digits that the calendar does not have — 2019-02-30, month 13. */
  | 'invalid'
  /** A real date outside `min`/`max`. */
  | 'out-of-range'
  | 'valid';

export function dateStatus(
  text: string,
  min?: string | undefined,
  max?: string | undefined,
): DateStatus {
  const digits = digitsOf(text);
  if (digits.length === 0) return 'empty';
  if (digits.length < 8) return 'incomplete';
  const iso = toIsoDate(text);
  if (iso === null) return 'invalid';
  // `YYYY-MM-DD` is fixed-width with the most significant field first, so
  // string order IS date order. No parsing, no timezone, no Date.
  if (min !== undefined && iso < min) return 'out-of-range';
  if (max !== undefined && iso > max) return 'out-of-range';
  return 'valid';
}

/**
 * Whether a status should show as an error. `incomplete` deliberately does NOT:
 * marking a field invalid while someone is still typing the year is the
 * behaviour that trains people to ignore error styling.
 */
export function isErrorStatus(status: DateStatus): boolean {
  return status === 'invalid' || status === 'out-of-range';
}

export interface DateInputOwnProps {
  // `| undefined` on each optional because `exactOptionalPropertyTypes` is on.
  /** Controlled ISO value (`YYYY-MM-DD`), or `''` for none. */
  value?: string | undefined;
  /** Uncontrolled starting ISO value. */
  defaultValue?: string | undefined;
  /**
   * Fires with a complete, real, in-range ISO date — or `''` for anything else,
   * including a half-typed one. A caller therefore never has to parse, and
   * cannot accidentally store `2019-02-3`.
   *
   * THE SECOND ARGUMENT IS NOT OPTIONAL DETAIL. `''` means two different
   * things: the field is empty, or the user is still typing. A caller that
   * autosaves cannot tell them apart from the value alone, and treating
   * "still typing" as "cleared" wipes a date the moment someone edits it —
   * one backspace on a saved `2020-01-15` reports `''`, and the save lands.
   * `status` is what distinguishes them: persist on `valid` and `empty`,
   * ignore `incomplete`.
   */
  onValueChange?: ((next: string, status: DateStatus) => void) | undefined;
  /** Earliest allowed ISO date, inclusive. */
  min?: string | undefined;
  /** Latest allowed ISO date, inclusive. */
  max?: string | undefined;
  disabled?: boolean | undefined;
  name?: string | undefined;
}

export interface DateInputState {
  /** What is on screen, masked. */
  text: string;
  setText: (next: string) => void;
  status: DateStatus;
  /** The last complete, valid, in-range date — `''` when there is none. */
  iso: string;
}

export function useDateInputState({
  value,
  defaultValue = '',
  onValueChange,
  min,
  max,
}: Pick<
  DateInputOwnProps,
  'value' | 'defaultValue' | 'onValueChange' | 'min' | 'max'
>): DateInputState {
  // The TEXT is the state, not the value — they are not the same thing while
  // someone is mid-keystroke, and a controlled `value` that only accepts whole
  // dates cannot represent "2019-0". So text lives here, and a controlled
  // `value` seeds it and overrides it whenever the parent supplies a different
  // date than the one on screen.
  const [text, setTextState] = React.useState(defaultValue);
  const controlled = value !== undefined;
  const shownText = controlled && toIsoDate(text) !== value ? value : text;

  const onChangeRef = React.useRef(onValueChange);
  React.useEffect(() => {
    onChangeRef.current = onValueChange;
  });

  const setText = React.useCallback(
    (next: string) => {
      const masked = maskDate(next);
      setTextState(masked);
      const status = dateStatus(masked, min, max);
      onChangeRef.current?.(status === 'valid' ? masked : '', status);
    },
    [min, max],
  );

  const status = dateStatus(shownText, min, max);

  return React.useMemo(
    () => ({
      text: shownText,
      setText,
      status,
      iso: status === 'valid' ? shownText : '',
    }),
    [shownText, setText, status],
  );
}
