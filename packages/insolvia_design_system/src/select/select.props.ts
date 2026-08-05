// SHARED — `react` and `../lib/controllable` only. No react-dom, no
// react-native. This is the whole select: its state, its keyboard grammar and
// its typeahead. The leaves render elements and nothing else.
//
// WHY THE OPTIONS ARE A PROP rather than `<Select.Option>` children, which is
// how Tabs and RadioGroup compose. A listbox has to know the WHOLE option list
// before the user touches it — ArrowDown needs the next enabled option, End
// needs the last one, typeahead needs to scan every label — and children only
// announce themselves as they mount. Collecting them through a registry would
// put keyboard order at the mercy of effect order, which is render order only
// while the list is static. Options here are data (the enums in
// docs/reference/case-data-model.md), so passing them as data costs no
// expressiveness and lets this module own every behaviour both leaves share.
// It also puts Select alongside Checkbox and Switch — a single control, not a
// parts object; the parts objects in this package are all containers.
import * as React from 'react';

import { useControllableState } from '../lib/controllable';

export interface SelectOption {
  /** Submitted/stored value. Must be unique within one Select. */
  value: string;
  /** Visible text, and what typeahead matches against. */
  label: string;
  disabled?: boolean;
}

/** `null` is "nothing chosen" — distinct from a valid `''` option value. */
export type SelectValue = string | null;

export function findOption(
  options: readonly SelectOption[],
  value: SelectValue,
): SelectOption | undefined {
  return value === null ? undefined : options.find((option) => option.value === value);
}

function enabledIndexes(options: readonly SelectOption[]): number[] {
  return options.reduce<number[]>((keep, option, index) => {
    if (!option.disabled) keep.push(index);
    return keep;
  }, []);
}

export function firstEnabled(options: readonly SelectOption[]): SelectValue {
  const [index] = enabledIndexes(options);
  return index === undefined ? null : (options[index]?.value ?? null);
}

export function lastEnabled(options: readonly SelectOption[]): SelectValue {
  const enabled = enabledIndexes(options);
  const index = enabled[enabled.length - 1];
  return index === undefined ? null : (options[index]?.value ?? null);
}

/**
 * The next enabled option in `step` direction, skipping disabled ones.
 *
 * DELIBERATELY DOES NOT WRAP. APG's listbox pattern stops at the ends, and for
 * a form control that is the kinder behaviour: holding ArrowDown settles on the
 * last option instead of cycling past it forever. Home/End are the way to jump.
 */
export function stepEnabled(
  options: readonly SelectOption[],
  from: SelectValue,
  step: 1 | -1,
): SelectValue {
  const enabled = enabledIndexes(options);
  if (enabled.length === 0) return null;
  const current = options.findIndex((option) => option.value === from);
  if (current === -1) return step === 1 ? firstEnabled(options) : lastEnabled(options);
  const position = enabled.indexOf(current);
  // `from` may name a DISABLED option (a caller can hand us a value that has
  // since been disabled); walk to the nearest enabled neighbour in `step`'s
  // direction rather than giving up.
  if (position === -1) {
    const candidate =
      step === 1
        ? enabled.find((index) => index > current)
        : [...enabled].reverse().find((index) => index < current);
    return candidate === undefined ? from : (options[candidate]?.value ?? null);
  }
  const next = enabled[position + step];
  return next === undefined ? from : (options[next]?.value ?? null);
}

/**
 * Typeahead: the option whose label starts with `query`, searched from just
 * after `from` and wrapping once, so repeated presses walk through the matches
 * rather than sticking on the first. Case-insensitive, disabled options skipped.
 *
 * Locale note: `toLowerCase()` without a locale argument, matching what the
 * platform does for `<select>`. It is wrong for exactly one pair (Turkish
 * dotted/dotless I) and right for the district and state names this actually
 * has to match.
 */
export function typeaheadMatch(
  options: readonly SelectOption[],
  query: string,
  from: SelectValue,
): SelectValue {
  if (query === '') return null;
  const needle = query.toLowerCase();
  const start = options.findIndex((option) => option.value === from);
  const order = options.map((_, index) => options[(start + 1 + index) % options.length]!);
  // Starting the scan AFTER `from` means an exact re-match of the current
  // option is found last, which is what makes repeated presses advance.
  const hit = order.find(
    (option) => !option.disabled && option.label.toLowerCase().startsWith(needle),
  );
  return hit?.value ?? null;
}

/**
 * What a keypress means. Pure, so the grammar is one testable table instead of
 * two hand-written switch statements that drift apart — the leaves differ only
 * in how they hear the key (DOM `onKeyDown`; on native, react-native-web's
 * forwarded `onKeyDown`) and what they do with the answer.
 */
export type SelectIntent =
  | { kind: 'none' }
  /** Show the listbox, with `active` highlighted. */
  | { kind: 'open'; active: SelectValue }
  /** Hide it, keeping the committed value. */
  | { kind: 'close' }
  /** Move the highlight without choosing. */
  | { kind: 'active'; active: string }
  /** Choose `value` and close. */
  | { kind: 'commit'; value: string }
  /** Not a navigation key — the caller should feed it to typeahead. */
  | { kind: 'typeahead'; char: string };

export interface SelectKeyState {
  open: boolean;
  options: readonly SelectOption[];
  value: SelectValue;
  active: SelectValue;
  /** True while a typeahead buffer is still collecting; makes Space a character. */
  typing: boolean;
}

export function selectKeyIntent(key: string, altKey: boolean, state: SelectKeyState): SelectIntent {
  const { open, options, value, active, typing } = state;

  if (!open) {
    if (key === 'ArrowDown' || key === 'ArrowUp' || key === 'Enter' || key === ' ') {
      // Opening highlights the current value, or the first option when there
      // is none — never nothing, so the first ArrowDown after opening moves
      // from a visible starting point.
      return { kind: 'open', active: value ?? firstEnabled(options) };
    }
    if (key === 'Home') return { kind: 'open', active: firstEnabled(options) };
    if (key === 'End') return { kind: 'open', active: lastEnabled(options) };
    if (isCharacter(key)) return { kind: 'typeahead', char: key };
    return { kind: 'none' };
  }

  switch (key) {
    case 'Escape':
      return { kind: 'close' };
    case 'ArrowDown':
    case 'ArrowUp': {
      // Alt+ArrowUp is the documented "close without moving" on an open
      // listbox; Alt+ArrowDown only ever opens, so it cannot arrive here.
      if (altKey && key === 'ArrowUp') return { kind: 'close' };
      const next = stepEnabled(options, active, key === 'ArrowDown' ? 1 : -1);
      return next === null ? { kind: 'none' } : { kind: 'active', active: next };
    }
    case 'Home': {
      const next = firstEnabled(options);
      return next === null ? { kind: 'none' } : { kind: 'active', active: next };
    }
    case 'End': {
      const next = lastEnabled(options);
      return next === null ? { kind: 'none' } : { kind: 'active', active: next };
    }
    case 'Enter':
      return active === null ? { kind: 'close' } : { kind: 'commit', value: active };
    case ' ':
      // Space commits — UNLESS a typeahead buffer is open, where it is the
      // space in "New Jersey". Without this branch every multi-word label in
      // the district list would be unreachable by typing.
      if (typing) return { kind: 'typeahead', char: ' ' };
      return active === null ? { kind: 'close' } : { kind: 'commit', value: active };
    case 'Tab':
      // Moving on commits what is highlighted, matching a native <select>.
      // The leaf must NOT preventDefault here: focus has to keep going.
      return active === null ? { kind: 'close' } : { kind: 'commit', value: active };
    default:
      return isCharacter(key) ? { kind: 'typeahead', char: key } : { kind: 'none' };
  }
}

/**
 * A key that should type rather than navigate. `key.length === 1` is the
 * standard test — it excludes every named key ('Shift', 'ArrowUp', 'F3') and
 * keeps printable ones, including accented characters.
 */
function isCharacter(key: string): boolean {
  return key.length === 1 && key !== ' ';
}

/** How long a typeahead buffer survives without another keystroke. */
export const TYPEAHEAD_RESET_MS = 500;

export interface SelectTypeahead {
  /** Feed a character; returns the option to highlight, or `null` for no match. */
  push: (char: string) => SelectValue;
  /** True while the buffer holds characters — the Space branch above needs it. */
  isTyping: () => boolean;
}

/**
 * The typeahead buffer. A ref rather than state on purpose: it is scratch input
 * that must be readable synchronously inside the same keydown that appends to
 * it, and re-rendering on every keystroke would buy nothing visible.
 */
export function useTypeahead(
  options: readonly SelectOption[],
  from: () => SelectValue,
): SelectTypeahead {
  const buffer = React.useRef('');
  const timer = React.useRef<ReturnType<typeof setTimeout> | null>(null);

  React.useEffect(
    () => () => {
      if (timer.current !== null) clearTimeout(timer.current);
    },
    [],
  );

  const push = React.useCallback(
    (char: string): SelectValue => {
      const previous = buffer.current;
      buffer.current = previous + char;
      if (timer.current !== null) clearTimeout(timer.current);
      timer.current = setTimeout(() => {
        buffer.current = '';
        timer.current = null;
      }, TYPEAHEAD_RESET_MS);

      // Repeating ONE character cycles through the options starting with it —
      // "aaa" means "the third A", not "an option labelled aaa". Same rule a
      // native <select> follows.
      const repeated = buffer.current.length > 1 && /^(.)\1+$/.test(buffer.current);
      const query = repeated ? char : buffer.current;
      // A repeat search starts from the current option so it advances; a
      // growing query re-searches from before it so it can narrow onto the
      // option it already found.
      const anchor = repeated || previous === '' ? from() : stepEnabled(options, from(), -1);
      return typeaheadMatch(options, query, anchor);
    },
    [options, from],
  );

  const isTyping = React.useCallback(() => buffer.current !== '', []);

  return React.useMemo(() => ({ push, isTyping }), [push, isTyping]);
}

export interface SelectOwnProps {
  /** The options, in the order they are shown and navigated. */
  options: readonly SelectOption[];
  // `| undefined` on every optional is required, not noise:
  // `exactOptionalPropertyTypes` is on, and without it a leaf cannot spread a
  // possibly-undefined prop through.
  /** Controlled value. Pair with `onValueChange`. */
  value?: SelectValue | undefined;
  /** Uncontrolled starting value. Defaults to nothing chosen. */
  defaultValue?: SelectValue | undefined;
  onValueChange?: ((next: string) => void) | undefined;
  /** Shown on the trigger while nothing is chosen. */
  placeholder?: string | undefined;
  disabled?: boolean | undefined;
  /** Submitted name. The web leaf renders a hidden input so a plain form post
   * carries the value; the native leaf has no form to post to and ignores it. */
  name?: string | undefined;
}

export interface SelectState {
  value: SelectValue;
  commit: (next: string) => void;
  open: boolean;
  setOpen: (next: boolean) => void;
  active: SelectValue;
  setActive: (next: SelectValue) => void;
  rootId: string;
  selected: SelectOption | undefined;
}

export function useSelectState({
  options,
  value,
  defaultValue = null,
  onValueChange,
}: Pick<SelectOwnProps, 'options' | 'value' | 'defaultValue' | 'onValueChange'>): SelectState {
  const [current, setCurrent] = useControllableState<SelectValue>(
    value,
    defaultValue,
    onValueChange as ((next: SelectValue) => void) | undefined,
  );
  const [open, setOpenState] = React.useState(false);
  const [active, setActive] = React.useState<SelectValue>(null);
  const rootId = React.useId();

  const setOpen = React.useCallback((next: boolean) => {
    setOpenState(next);
    // Closing drops the highlight so the next open starts from the committed
    // value rather than wherever the user wandered before pressing Escape.
    if (!next) setActive(null);
  }, []);

  const commit = React.useCallback(
    (next: string) => {
      setCurrent(next);
      setOpenState(false);
      setActive(null);
    },
    [setCurrent],
  );

  const selected = findOption(options, current);

  return React.useMemo(
    () => ({ value: current, commit, open, setOpen, active, setActive, rootId, selected }),
    [current, commit, open, setOpen, active, rootId, selected],
  );
}

/** Ids the web leaf needs; derived here because both leaves agree on the shape. */
export function getOptionId(rootId: string, value: string): string {
  return `${rootId}-option-${value}`;
}

export function getListboxId(rootId: string): string {
  return `${rootId}-listbox`;
}
