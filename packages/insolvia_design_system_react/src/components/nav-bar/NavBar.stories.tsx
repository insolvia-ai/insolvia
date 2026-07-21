import type { Meta, StoryObj } from '@storybook/react';

import { Button } from '../button';

import { NavBar } from './NavBar';

const meta: Meta<typeof NavBar.Root> = {
  title: 'Components/NavBar',
  component: NavBar.Root,
};

export default meta;

type Story = StoryObj<typeof NavBar.Root>;

export const Default: Story = {
  render: (args) => (
    <NavBar.Root {...args}>
      <NavBar.Brand href="/">Insolvia</NavBar.Brand>
      <NavBar.Links>
        <NavBar.Link href="/features" active>
          Features
        </NavBar.Link>
        <NavBar.Link href="/pricing">Pricing</NavBar.Link>
        <NavBar.Link href="/docs">Docs</NavBar.Link>
      </NavBar.Links>
      <NavBar.Actions>
        <Button intent="ghost" size="sm">
          Sign in
        </Button>
        <Button size="sm">Join the waitlist</Button>
      </NavBar.Actions>
    </NavBar.Root>
  ),
};
