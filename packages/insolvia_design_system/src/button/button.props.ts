// SHARED — no react-native / react-dom / base-ui import. Pure TS + cn().
import { cn } from '../lib/cn';
import { disabledStyles, focusRing } from '../lib/styles';

export type ButtonIntent = 'primary' | 'secondary' | 'ghost';
export type ButtonSize = 'sm' | 'md' | 'lg';

// Marketing needs exactly three weights of call-to-action. There is no `danger`
// intent because the semantic token set has no `danger-text` pair, and a
// marketing page has nothing destructive to offer.
export const intentStyles: Record<ButtonIntent, string> = {
  primary: 'bg-primary text-primary-text hover:bg-primary-hover active:bg-primary-active',
  secondary: 'bg-surface-alt text-ink hover:bg-line active:bg-line',
  ghost: 'bg-transparent text-ink hover:bg-surface-alt active:bg-line',
};

export const sizeStyles: Record<ButtonSize, string> = {
  sm: 'h-8 gap-1.5 px-3 text-sm',
  md: 'h-10 gap-2 px-4 text-sm',
  lg: 'h-12 gap-2 px-6 text-base',
};

// The `| undefined` on each member is for `exactOptionalPropertyTypes` (on in
// tsconfig.base.json): the web leaf forwards `className` straight from its own
// props, where it is `string | undefined`, and that assignment is an error
// against a bare optional.
export interface ButtonClassOptions {
  intent?: ButtonIntent | undefined;
  size?: ButtonSize | undefined;
  className?: string | undefined;
}

/**
 * The button's Tailwind classes, exported so a link can be styled as a button
 * without a polymorphic component: `<Link className={buttonClass({ intent })}>`.
 * Web-only (Tailwind), but the string composition is platform-agnostic, so it
 * lives in the shared props module.
 */
export function buttonClass({
  intent = 'primary',
  size = 'md',
  className,
}: ButtonClassOptions = {}) {
  return cn(
    'inline-flex cursor-pointer items-center justify-center whitespace-nowrap rounded-md font-body font-medium no-underline transition-colors',
    focusRing,
    disabledStyles,
    intentStyles[intent],
    sizeStyles[size],
    className,
  );
}
