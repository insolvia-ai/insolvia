import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { Button, buttonClass } from "./button";

describe("Button", () => {
  it("renders a native button that defaults to type=button", () => {
    render(<Button>Go</Button>);
    const btn = screen.getByRole("button", { name: "Go" });
    expect(btn).toHaveAttribute("type", "button");
  });

  it("keeps an explicit type=submit", () => {
    render(<Button type="submit">Send</Button>);
    expect(screen.getByRole("button", { name: "Send" })).toHaveAttribute("type", "submit");
  });

  it("fires onClick", async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Button onClick={onClick}>Go</Button>);
    await user.click(screen.getByRole("button", { name: "Go" }));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it("buttonClass composes intent + size so a link can wear it", () => {
    const cls = buttonClass({ intent: "primary", size: "sm" });
    expect(cls).toContain("bg-primary");
    expect(cls).toContain("h-8");
  });
});
