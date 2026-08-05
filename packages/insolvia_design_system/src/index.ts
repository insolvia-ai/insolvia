// @insolvia-ai/design-system — owned, platform-split UI primitives. The
// original six component names the outgoing web-only package exported, plus
// the 0.3.0 wave of owned Base-UI-equivalent primitives; the CONSUMER'S
// bundler picks each component's .web / .native leaf by extension (see
// README.md). Keep this barrel the source of truth for what the package
// exports.
export { Accordion } from './accordion';
export { AlertDialog } from './alert-dialog';
export { Avatar } from './avatar';
export { Button, buttonClass } from './button';
export type { ButtonIntent, ButtonSize, ButtonClassOptions } from './button';
export { Card } from './card';
export { Checkbox } from './checkbox';
export { CheckboxGroup } from './checkbox-group';
export { Collapsible } from './collapsible';
export { DateInput } from './date-input';
export type { DateStatus } from './date-input';
export { Dialog } from './dialog';
export { Field } from './field';
export { Footer } from './footer';
export { Meter } from './meter';
export { NavBar } from './nav-bar';
export { Progress } from './progress';
export { RadioGroup } from './radio-group';
export { Select } from './select';
export type { SelectOption, SelectValue } from './select';
export { Separator } from './separator';
export { Switch } from './switch';
export { Tabs } from './tabs';
export { Toggle } from './toggle';
export { ToggleGroup } from './toggle-group';
