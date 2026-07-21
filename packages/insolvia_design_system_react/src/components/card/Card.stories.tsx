import type { Meta, StoryObj } from '@storybook/react';

import { Button } from '../button';

import { Card } from './Card';

const meta: Meta<typeof Card.Root> = {
  title: 'Components/Card',
  component: Card.Root,
  argTypes: {
    elevation: {
      control: 'select',
      options: ['flat', 'raised'],
    },
  },
};

export default meta;

type Story = StoryObj<typeof Card.Root>;

export const Default: Story = {
  render: (args) => (
    <Card.Root {...args} className="max-w-sm">
      <Card.Title>One codebase, two platforms</Card.Title>
      <Card.Body>
        A native macOS and Windows app plus the web, shipped from a single Flutter codebase.
      </Card.Body>
      <Card.Footer>
        <Button size="sm">Book a demo</Button>
      </Card.Footer>
    </Card.Root>
  ),
};

export const Raised: Story = {
  ...Default,
  args: { elevation: 'raised' },
};

export const FeatureGrid: Story = {
  render: () => (
    <div className="grid gap-lg sm:grid-cols-3">
      {[
        ['Guided intake', 'Client data captured once, reused across every schedule.'],
        ['Means test built in', 'Current figures applied automatically for the district.'],
        ['Direct e-filing', 'File to CM/ECF from the case you just prepared.'],
      ].map(([title, body]) => (
        <Card.Root key={title} elevation="raised">
          <Card.Title>{title}</Card.Title>
          <Card.Body>{body}</Card.Body>
        </Card.Root>
      ))}
    </div>
  ),
};
