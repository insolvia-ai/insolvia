import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { Field } from "./field";

describe("Field", () => {
  it("associates the label with the control", () => {
    render(
      <Field.Root name="email">
        <Field.Label>Work email</Field.Label>
        <Field.Control type="email" />
      </Field.Root>,
    );
    // getByLabelText only resolves if htmlFor ↔ id is wired correctly.
    const input = screen.getByLabelText("Work email");
    expect(input).toHaveAttribute("name", "email");
  });

  it("points aria-describedby only at the description/error that exist", () => {
    render(
      <Field.Root name="email" invalid>
        <Field.Label>Work email</Field.Label>
        <Field.Control type="email" />
        <Field.Description>Only used for updates.</Field.Description>
        <Field.Error match>Enter a valid email.</Field.Error>
      </Field.Root>,
    );
    const input = screen.getByLabelText("Work email");
    const describedby = input.getAttribute("aria-describedby");
    expect(describedby).toBeTruthy();
    const ids = describedby!.split(" ");
    // Every referenced id must resolve to a real element — no dangling refs.
    for (const id of ids) expect(document.getElementById(id)).toBeInTheDocument();
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByText("Enter a valid email.")).toBeInTheDocument();
  });

  it("has no aria-describedby when neither description nor error is present", () => {
    render(
      <Field.Root name="name">
        <Field.Label>Name</Field.Label>
        <Field.Control />
      </Field.Root>,
    );
    expect(screen.getByLabelText("Name")).not.toHaveAttribute("aria-describedby");
  });

  it("renders as a select via the render prop, still labelled", () => {
    render(
      <Field.Root name="software">
        <Field.Label>Current software</Field.Label>
        <Field.Control
          render={
            <select>
              <option value="">Select one</option>
              <option value="Best Case">Best Case</option>
            </select>
          }
        />
      </Field.Root>,
    );
    const select = screen.getByLabelText("Current software");
    expect(select.tagName).toBe("SELECT");
    expect(select).toHaveAttribute("name", "software");
  });
});
