import type { Meta, StoryObj } from '@storybook/react';

import { Accordion } from '../components/accordion';
import { Button } from '../components/button';
import { Card } from '../components/card';
import { Field } from '../components/field';
import { Footer } from '../components/footer';
import { NavBar } from '../components/nav-bar';

/**
 * Every component on one surface, so the semantic tokens can be eyeballed as a
 * set. `Dark` pins `data-theme="dark"` on its own wrapper — the exact selector
 * the generated theme.css keys its dark palette off — so it stays dark no
 * matter where the toolbar theme switch is left.
 */
const meta: Meta = {
  title: 'Foundations/Theme',
  parameters: {
    layout: 'fullscreen',
  },
};

export default meta;

function Surface() {
  return (
    <div className="flex flex-col gap-xl">
      <NavBar.Root>
        <NavBar.Brand href="/">Insolvia</NavBar.Brand>
        <NavBar.Links>
          <NavBar.Link href="/features" active>
            Features
          </NavBar.Link>
          <NavBar.Link href="/pricing">Pricing</NavBar.Link>
        </NavBar.Links>
        <NavBar.Actions>
          <Button size="sm">Join the waitlist</Button>
        </NavBar.Actions>
      </NavBar.Root>

      <div className="grid gap-lg px-lg sm:grid-cols-2">
        <Card.Root elevation="raised">
          <Card.Title>Desktop and web, one codebase</Card.Title>
          <Card.Body>A native macOS and Windows app plus the web, shipped together.</Card.Body>
          <Card.Footer>
            <Button intent="secondary" size="sm">
              Book a demo
            </Button>
          </Card.Footer>
        </Card.Root>

        <Card.Root>
          <Card.Title>Get early access</Card.Title>
          <form className="flex items-end gap-sm" onSubmit={(event) => event.preventDefault()}>
            <Field.Root className="flex-1">
              <Field.Label>Work email</Field.Label>
              <Field.Control type="email" placeholder="you@firm.com" />
            </Field.Root>
            <Button type="submit">Join</Button>
          </form>
        </Card.Root>
      </div>

      <div className="px-lg">
        <Accordion.Root className="max-w-xl" defaultValue={['migrate']}>
          <Accordion.Item value="migrate">
            <Accordion.Header>
              <Accordion.Trigger>Can I migrate off Best Case?</Accordion.Trigger>
            </Accordion.Header>
            <Accordion.Panel>
              <p className="pb-md">We import your existing case files so nothing is re-keyed.</p>
            </Accordion.Panel>
          </Accordion.Item>
          <Accordion.Item value="efile">
            <Accordion.Header>
              <Accordion.Trigger>Do you e-file to CM/ECF?</Accordion.Trigger>
            </Accordion.Header>
            <Accordion.Panel>
              <p className="pb-md">Filing goes straight from the case you just prepared.</p>
            </Accordion.Panel>
          </Accordion.Item>
        </Accordion.Root>
      </div>

      <Footer.Root>
        <div className="flex flex-wrap gap-xxl">
          <Footer.Group title="Product">
            <Footer.Link href="/features">Features</Footer.Link>
            <Footer.Link href="/pricing">Pricing</Footer.Link>
          </Footer.Group>
          <Footer.Group title="Company">
            <Footer.Link href="/about">About</Footer.Link>
          </Footer.Group>
        </div>
        <Footer.Note>© 2026 Insolvia. Not a law firm; not legal advice.</Footer.Note>
      </Footer.Root>
    </div>
  );
}

type Story = StoryObj;

export const Light: Story = {
  render: () => (
    <div data-theme="light" className="min-h-screen bg-bg py-xl text-ink">
      <Surface />
    </div>
  ),
};

export const Dark: Story = {
  render: () => (
    <div data-theme="dark" className="min-h-screen bg-bg py-xl text-ink">
      <Surface />
    </div>
  ),
};
