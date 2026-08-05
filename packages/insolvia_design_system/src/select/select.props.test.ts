import { describe, expect, it } from 'vitest';

import {
  findOption,
  firstEnabled,
  getOptionId,
  lastEnabled,
  selectKeyIntent,
  stepEnabled,
  typeaheadMatch,
  type SelectKeyState,
  type SelectOption,
} from './select.props';

const OPTIONS: SelectOption[] = [
  { value: 'ak', label: 'Alaska' },
  { value: 'az', label: 'Arizona' },
  { value: 'ca', label: 'California', disabled: true },
  { value: 'nj', label: 'New Jersey' },
  { value: 'ny', label: 'New York' },
];

const state = (over: Partial<SelectKeyState> = {}): SelectKeyState => ({
  open: true,
  options: OPTIONS,
  value: null,
  active: null,
  typing: false,
  ...over,
});

describe('findOption', () => {
  it('returns undefined for the null value rather than matching anything', () => {
    expect(findOption(OPTIONS, null)).toBeUndefined();
  });

  it('does not confuse a valid empty-string option with nothing chosen', () => {
    const withEmpty: SelectOption[] = [{ value: '', label: 'Not stated' }, ...OPTIONS];
    expect(findOption(withEmpty, '')?.label).toBe('Not stated');
    expect(findOption(withEmpty, null)).toBeUndefined();
  });
});

describe('firstEnabled / lastEnabled', () => {
  it('skips disabled options at either end', () => {
    const edged: SelectOption[] = [
      { value: 'a', label: 'A', disabled: true },
      { value: 'b', label: 'B' },
      { value: 'c', label: 'C', disabled: true },
    ];
    expect(firstEnabled(edged)).toBe('b');
    expect(lastEnabled(edged)).toBe('b');
  });

  it('returns null when every option is disabled', () => {
    const none: SelectOption[] = [{ value: 'a', label: 'A', disabled: true }];
    expect(firstEnabled(none)).toBeNull();
    expect(lastEnabled(none)).toBeNull();
  });
});

describe('stepEnabled', () => {
  it('skips over a disabled option', () => {
    expect(stepEnabled(OPTIONS, 'az', 1)).toBe('nj');
    expect(stepEnabled(OPTIONS, 'nj', -1)).toBe('az');
  });

  it('stops at the ends instead of wrapping', () => {
    expect(stepEnabled(OPTIONS, 'ny', 1)).toBe('ny');
    expect(stepEnabled(OPTIONS, 'ak', -1)).toBe('ak');
  });

  it('enters from the matching end when nothing is highlighted', () => {
    expect(stepEnabled(OPTIONS, null, 1)).toBe('ak');
    expect(stepEnabled(OPTIONS, null, -1)).toBe('ny');
  });

  it('walks off a value that is itself disabled', () => {
    expect(stepEnabled(OPTIONS, 'ca', 1)).toBe('nj');
    expect(stepEnabled(OPTIONS, 'ca', -1)).toBe('az');
  });
});

describe('typeaheadMatch', () => {
  it('matches a case-insensitive label prefix', () => {
    expect(typeaheadMatch(OPTIONS, 'new j', null)).toBe('nj');
    expect(typeaheadMatch(OPTIONS, 'NEW J', null)).toBe('nj');
  });

  it('advances to the next match rather than sticking on the current one', () => {
    expect(typeaheadMatch(OPTIONS, 'a', null)).toBe('ak');
    expect(typeaheadMatch(OPTIONS, 'a', 'ak')).toBe('az');
  });

  it('wraps around once so the last match leads back to the first', () => {
    expect(typeaheadMatch(OPTIONS, 'a', 'az')).toBe('ak');
  });

  it('never lands on a disabled option', () => {
    expect(typeaheadMatch(OPTIONS, 'c', null)).toBeNull();
  });

  it('returns null for an empty query and for no match', () => {
    expect(typeaheadMatch(OPTIONS, '', null)).toBeNull();
    expect(typeaheadMatch(OPTIONS, 'zz', null)).toBeNull();
  });
});

describe('selectKeyIntent — closed', () => {
  it.each(['ArrowDown', 'ArrowUp', 'Enter', ' '])('opens on %s', (key) => {
    expect(selectKeyIntent(key, false, state({ open: false }))).toEqual({
      kind: 'open',
      active: 'ak',
    });
  });

  it('opens highlighting the committed value, not the first option', () => {
    expect(selectKeyIntent('ArrowDown', false, state({ open: false, value: 'ny' }))).toEqual({
      kind: 'open',
      active: 'ny',
    });
  });

  it('opens at either end for Home and End', () => {
    expect(selectKeyIntent('Home', false, state({ open: false }))).toEqual({
      kind: 'open',
      active: 'ak',
    });
    expect(selectKeyIntent('End', false, state({ open: false }))).toEqual({
      kind: 'open',
      active: 'ny',
    });
  });

  it('treats a printable key as typeahead', () => {
    expect(selectKeyIntent('n', false, state({ open: false }))).toEqual({
      kind: 'typeahead',
      char: 'n',
    });
  });

  it('ignores modifier and function keys', () => {
    expect(selectKeyIntent('Shift', false, state({ open: false }))).toEqual({ kind: 'none' });
    expect(selectKeyIntent('F3', false, state({ open: false }))).toEqual({ kind: 'none' });
  });
});

describe('selectKeyIntent — open', () => {
  it('moves the highlight with the arrows, skipping disabled options', () => {
    expect(selectKeyIntent('ArrowDown', false, state({ active: 'az' }))).toEqual({
      kind: 'active',
      active: 'nj',
    });
    expect(selectKeyIntent('ArrowUp', false, state({ active: 'nj' }))).toEqual({
      kind: 'active',
      active: 'az',
    });
  });

  it('jumps to the ends with Home and End', () => {
    expect(selectKeyIntent('Home', false, state({ active: 'ny' }))).toEqual({
      kind: 'active',
      active: 'ak',
    });
    expect(selectKeyIntent('End', false, state({ active: 'ak' }))).toEqual({
      kind: 'active',
      active: 'ny',
    });
  });

  it('commits the highlighted option on Enter', () => {
    expect(selectKeyIntent('Enter', false, state({ active: 'nj' }))).toEqual({
      kind: 'commit',
      value: 'nj',
    });
  });

  it('closes on Escape without committing', () => {
    expect(selectKeyIntent('Escape', false, state({ active: 'nj' }))).toEqual({ kind: 'close' });
  });

  it('closes on Alt+ArrowUp', () => {
    expect(selectKeyIntent('ArrowUp', true, state({ active: 'nj' }))).toEqual({ kind: 'close' });
  });

  it('commits on Tab so moving on keeps the highlighted option', () => {
    expect(selectKeyIntent('Tab', false, state({ active: 'nj' }))).toEqual({
      kind: 'commit',
      value: 'nj',
    });
  });

  it('commits on Space when no typeahead is in progress', () => {
    expect(selectKeyIntent(' ', false, state({ active: 'nj' }))).toEqual({
      kind: 'commit',
      value: 'nj',
    });
  });

  it('types a space instead of committing while typeahead is collecting', () => {
    // Without this, "New Jersey" is unreachable by keyboard: the space after
    // "New" would choose whatever happened to be highlighted.
    expect(selectKeyIntent(' ', false, state({ active: 'nj', typing: true }))).toEqual({
      kind: 'typeahead',
      char: ' ',
    });
  });

  it('closes rather than committing when nothing is highlighted', () => {
    expect(selectKeyIntent('Enter', false, state({ active: null }))).toEqual({ kind: 'close' });
  });
});

describe('getOptionId', () => {
  it('namespaces by root so two Selects on one page cannot collide', () => {
    expect(getOptionId('r1', 'nj')).not.toBe(getOptionId('r2', 'nj'));
  });
});
