import * as React from "react";

import { cn } from "~/lib/cn";
import { disabledStyles, focusRing } from "./styles";

export type ButtonIntent = "primary" | "secondary" | "ghost";
export type ButtonSize = "sm" | "md" | "lg";

// Marketing needs exactly three weights of call-to-action. There is no `danger`
// intent because the semantic token set has no `danger-text` pair, and a
// marketing page has nothing destructive to offer.
const intentStyles: Record<ButtonIntent, string> = {
  primary: "bg-primary text-primary-text hover:bg-primary-hover active:bg-primary-active",
  secondary: "bg-surface-alt text-ink hover:bg-line active:bg-line",
  ghost: "bg-transparent text-ink hover:bg-surface-alt active:bg-line",
};

const sizeStyles: Record<ButtonSize, string> = {
  sm: "h-8 gap-1.5 px-3 text-sm",
  md: "h-10 gap-2 px-4 text-sm",
  lg: "h-12 gap-2 px-6 text-base",
};

export interface ButtonClassOptions {
  intent?: ButtonIntent;
  size?: ButtonSize;
  className?: string;
}

/**
 * The button's Tailwind classes, exported so a link can be styled as a button
 * without a polymorphic component: `<Link className={buttonClass({ intent })}>`.
 * That is how every call-to-action that navigates is built — the previous
 * design system reached for Base UI's `render`/`nativeButton` escape hatch for
 * the same cases, which is exactly the machinery this drops.
 */
export function buttonClass({ intent = "primary", size = "md", className }: ButtonClassOptions = {}) {
  return cn(
    "inline-flex cursor-pointer items-center justify-center whitespace-nowrap rounded-md font-body font-medium no-underline transition-colors",
    focusRing,
    disabledStyles,
    intentStyles[intent],
    sizeStyles[size],
    className,
  );
}

export interface ButtonProps extends React.ComponentPropsWithoutRef<"button"> {
  intent?: ButtonIntent;
  size?: ButtonSize;
}

// A plain `<button>` for real buttons (form submits). Anything that *navigates*
// is a `<Link>`/`<a>` styled with `buttonClass`, not this. `type` defaults to
// "button" so a button inside a form never submits by accident.
export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, intent = "primary", size = "md", type, ...props }, ref) => {
    return (
      <button
        ref={ref}
        type={type ?? "button"}
        className={buttonClass({ intent, size, className })}
        {...props}
      />
    );
  },
);

Button.displayName = "Button";
