import { displayName, sortName } from './client-names';

describe('client names', () => {
  const whole = { given: 'Ada', middle: 'King', surname: 'Lovelace', suffix: 'Jr.' };

  it('reads a whole name in print order as the subject of a page', () => {
    expect(displayName({ name: whole })).toBe('Ada King Lovelace Jr.');
  });

  it('reads surname first in a list or picker, without middle name or suffix', () => {
    expect(sortName({ name: whole })).toBe('Lovelace, Ada');
  });

  it.each([
    [{ surname: 'Lovelace' }, 'Lovelace'],
    [{ given: ' Ada ', surname: '' }, 'Ada'],
    [{ given: '  ', surname: '' }, 'Unnamed client'],
  ])('falls back to whichever half exists, trimmed: %j', (name, expected) => {
    expect(displayName({ name })).toBe(expected);
    expect(sortName({ name })).toBe(expected);
  });
});
