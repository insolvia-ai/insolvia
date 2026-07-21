import type { Meta, StoryObj } from '@storybook/react';

import { Footer } from './Footer';

const meta: Meta<typeof Footer.Root> = {
  title: 'Components/Footer',
  component: Footer.Root,
};

export default meta;

type Story = StoryObj<typeof Footer.Root>;

export const Default: Story = {
  render: (args) => (
    <Footer.Root {...args}>
      <div className="flex flex-wrap gap-xxl">
        <Footer.Group title="Product">
          <Footer.Link href="/features">Features</Footer.Link>
          <Footer.Link href="/pricing">Pricing</Footer.Link>
          <Footer.Link href="/security">Security</Footer.Link>
        </Footer.Group>
        <Footer.Group title="Company">
          <Footer.Link href="/about">About</Footer.Link>
          <Footer.Link href="/contact">Contact</Footer.Link>
        </Footer.Group>
        <Footer.Group title="Legal">
          <Footer.Link href="/privacy">Privacy</Footer.Link>
          <Footer.Link href="/terms">Terms</Footer.Link>
        </Footer.Group>
      </div>
      <Footer.Note>© 2026 Insolvia. Not a law firm; not legal advice.</Footer.Note>
    </Footer.Root>
  ),
};
