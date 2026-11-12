import { Input } from '@insolvia-ai/design-system';
import { useEffect, useState } from 'react';

/**
 * A money box whose value is a NUMBER — what a case collection's body carries
 * on the wire (ADR 0032) — while the box itself keeps the text being typed,
 * so "12." on the way to "12.50" is not reprinted as "12".
 *
 * "$1,200" is how people write it; it reports 1200. Blank is absent, and so
 * is text that is not an amount — the box is then marked invalid.
 */
export function MoneyInput({
  value,
  onValueChange,
  invalid,
}: {
  readonly value: number | undefined;
  readonly onValueChange: (next: number | undefined) => void;
  readonly invalid?: boolean | undefined;
}) {
  const [text, setText] = useState(value === undefined ? '' : String(value));
  // A value changed from outside (a load, a save's answer) reprints the box;
  // the value this box itself just reported does not.
  useEffect(() => {
    setText((current) =>
      moneyOf(current) === value ? current : value === undefined ? '' : String(value),
    );
  }, [value]);
  return (
    <Input
      value={text}
      onValueChange={(next) => {
        setText(next);
        onValueChange(moneyOf(next));
      }}
      invalid={invalid === true || (text.trim() !== '' && moneyOf(text) === undefined)}
      autoCorrect={false}
    />
  );
}

/** Typed money text as a number, or `undefined` when blank or not an amount. */
export function moneyOf(text: string): number | undefined {
  const plain = text.replace(/[$,\s]/gu, '');
  return /^\d*\.?\d*$/u.test(plain) && /\d/u.test(plain) ? Number(plain) : undefined;
}
