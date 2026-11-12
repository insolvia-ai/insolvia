/**
 * Exact decimal-string money arithmetic, integer cents underneath.
 *
 * A view's money is a two-decimal STRING; a collection body's is a JSON
 * number (ADR 0032). Either is turned into integer cents here, added as
 * integers and formatted back, so a sum never accumulates float error.
 */

/** A money string ("1200.00", "-45.50") or a body's number to integer cents.
 * Absent, blank, or unparseable is treated as zero — the same "blank box
 * claims nothing" reading the server's own `_entered()` helpers use. */
export function toCents(value: string | number | undefined): number {
  if (value === undefined) return 0;
  if (typeof value === 'number') return Number.isFinite(value) ? Math.round(value * 100) : 0;
  const trimmed = value.trim();
  if (trimmed === '') return 0;
  const negative = trimmed.startsWith('-');
  const unsigned = negative ? trimmed.slice(1) : trimmed;
  const [wholePart = '', fractionPart = ''] = unsigned.split('.');
  if (!/^\d*$/.test(wholePart) || !/^\d*$/.test(fractionPart)) return 0;
  const whole = wholePart === '' ? 0 : Number.parseInt(wholePart, 10);
  const fractionDigits = (fractionPart + '00').slice(0, 2);
  const fraction = Number.parseInt(fractionDigits, 10);
  const cents = whole * 100 + fraction;
  return negative ? -cents : cents;
}

/** Integer cents to a two-decimal money string, matching the server's own
 * `f"{value:f}"` formatting. */
export function fromCents(cents: number): string {
  const negative = cents < 0;
  const absolute = Math.abs(Math.trunc(cents));
  const whole = Math.floor(absolute / 100);
  const remainder = String(absolute % 100).padStart(2, '0');
  return `${negative ? '-' : ''}${whole}.${remainder}`;
}

/** Sums a list of money values exactly. */
export function sumMoney(values: readonly (string | number | undefined)[]): string {
  return fromCents(values.reduce<number>((total, value) => total + toCents(value), 0));
}
