import type { Meta, StoryObj } from '@storybook/react';

import { Button } from '../button';

import { Field } from './Field';

const meta: Meta<typeof Field.Root> = {
  title: 'Components/Field',
  component: Field.Root,
};

export default meta;

type Story = StoryObj<typeof Field.Root>;

export const Default: Story = {
  render: (args) => (
    <Field.Root {...args} className="max-w-sm">
      <Field.Label>Work email</Field.Label>
      <Field.Control type="email" placeholder="you@firm.com" />
      <Field.Description>We only use this to send your invite.</Field.Description>
    </Field.Root>
  ),
};

export const Waitlist: Story = {
  render: () => (
    <form className="flex max-w-md items-end gap-sm" onSubmit={(event) => event.preventDefault()}>
      <Field.Root className="flex-1">
        <Field.Label>Work email</Field.Label>
        <Field.Control type="email" placeholder="you@firm.com" required />
      </Field.Root>
      <Button type="submit">Join the waitlist</Button>
    </form>
  ),
};

export const Disabled: Story = {
  render: () => (
    <Field.Root disabled className="max-w-sm">
      <Field.Label>Work email</Field.Label>
      <Field.Control type="email" placeholder="you@firm.com" />
    </Field.Root>
  ),
};
