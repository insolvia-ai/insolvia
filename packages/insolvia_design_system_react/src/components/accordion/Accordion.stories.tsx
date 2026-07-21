import type { Meta, StoryObj } from '@storybook/react';

import { Accordion } from './Accordion';

const meta: Meta<typeof Accordion.Root> = {
  title: 'Components/Accordion',
  component: Accordion.Root,
};

export default meta;

type Story = StoryObj<typeof Accordion.Root>;

const faqs = [
  {
    value: 'migrate',
    question: 'Can I migrate my cases off Best Case?',
    answer: 'Yes — we import your existing case files so nothing is re-keyed.',
  },
  {
    value: 'desktop',
    question: 'Is there a real desktop app?',
    answer: 'A native macOS and Windows app, not a browser window in a wrapper.',
  },
  {
    value: 'efile',
    question: 'Do you e-file directly to CM/ECF?',
    answer: 'Filing goes straight from the case you prepared — no export step.',
  },
];

export const Faq: Story = {
  render: (args) => (
    <Accordion.Root {...args} className="w-full max-w-xl">
      {faqs.map((faq) => (
        <Accordion.Item key={faq.value} value={faq.value}>
          <Accordion.Header>
            <Accordion.Trigger>{faq.question}</Accordion.Trigger>
          </Accordion.Header>
          <Accordion.Panel>
            <p className="pb-md">{faq.answer}</p>
          </Accordion.Panel>
        </Accordion.Item>
      ))}
    </Accordion.Root>
  ),
};

export const OpenByDefault: Story = {
  ...Faq,
  args: { defaultValue: ['migrate'] },
};
