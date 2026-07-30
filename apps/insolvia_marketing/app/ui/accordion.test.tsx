import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import { Accordion } from "./accordion";

function Faq() {
  return (
    <Accordion.Root>
      {["one", "two"].map((v) => (
        <Accordion.Item key={v} value={v}>
          <Accordion.Header>
            <Accordion.Trigger>Question {v}</Accordion.Trigger>
          </Accordion.Header>
          <Accordion.Panel>Answer {v}</Accordion.Panel>
        </Accordion.Item>
      ))}
    </Accordion.Root>
  );
}

describe("Accordion", () => {
  it("starts collapsed with the panel wired to its trigger", () => {
    render(<Faq />);
    const trigger = screen.getByRole("button", { name: "Question one" });
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    const panelId = trigger.getAttribute("aria-controls")!;
    const panel = document.getElementById(panelId)!;
    expect(panel).toHaveAttribute("role", "region");
    expect(panel).toHaveAttribute("aria-labelledby", trigger.id);
    // Collapsed → inert, i.e. out of the tab order and a11y tree.
    expect(panel.hasAttribute("inert")).toBe(true);
  });

  it("opens on click and collapses on a second click", async () => {
    const user = userEvent.setup();
    render(<Faq />);
    const trigger = screen.getByRole("button", { name: "Question one" });
    const panel = document.getElementById(trigger.getAttribute("aria-controls")!)!;

    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");
    expect(panel.hasAttribute("inert")).toBe(false);

    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    expect(panel.hasAttribute("inert")).toBe(true);
  });

  it("allows multiple panels open at once", async () => {
    const user = userEvent.setup();
    render(<Faq />);
    await user.click(screen.getByRole("button", { name: "Question one" }));
    await user.click(screen.getByRole("button", { name: "Question two" }));
    expect(screen.getByRole("button", { name: "Question one" })).toHaveAttribute(
      "aria-expanded",
      "true",
    );
    expect(screen.getByRole("button", { name: "Question two" })).toHaveAttribute(
      "aria-expanded",
      "true",
    );
  });

  it("moves focus between triggers with the arrow keys", async () => {
    const user = userEvent.setup();
    render(<Faq />);
    const first = screen.getByRole("button", { name: "Question one" });
    const second = screen.getByRole("button", { name: "Question two" });
    first.focus();
    await user.keyboard("{ArrowDown}");
    expect(second).toHaveFocus();
    await user.keyboard("{ArrowUp}");
    expect(first).toHaveFocus();
  });
});
