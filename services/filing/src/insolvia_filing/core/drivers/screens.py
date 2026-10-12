"""Reading a court's HTML screen, and recognising it (ADR 0024: "Every screen
a driver will act on is fingerprinted; a screen it does not recognise is a
stop, never a guess").

A screen's FINGERPRINT is its structure, not its words: the page title and,
for every form on it, the action and the sorted names of its fields. Copy
changes (a new banner, reworded help) leave it alone; a new field, a moved
form or a different page does not. A driver declares the screens it acts on
as `ScreenSpec`s and `recognise` answers which one a page is — or None.

Standard library only (`html.parser`): the screens are forms and a handful
of marked elements, and a parser with no network or script engine is one
less thing that could fetch something.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from html.parser import HTMLParser


@dataclass(frozen=True)
class Form:
    action: str
    method: str
    fields: tuple[str, ...]
    # Hidden inputs' values, which a driver posts back unchanged (a token).
    hidden: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class Screen:
    title: str
    forms: tuple[Form, ...]
    # Text of every element carrying an id, by id — where the court puts the
    # facts a driver reads (a case number, an error message).
    by_id: Mapping[str, str]
    # Text of every element with class "message" — the court's own messages.
    messages: tuple[str, ...]
    # Items of the list with id="entries", in order.
    entries: tuple[str, ...]

    @property
    def fingerprint(self) -> ScreenSpec:
        return ScreenSpec(
            title=self.title,
            forms=tuple(
                (form.action, tuple(sorted(form.fields))) for form in self.forms
            ),
        )


@dataclass(frozen=True)
class ScreenSpec:
    """A screen a driver knows: its title, and each form's action and field
    names (sorted)."""

    title: str
    forms: tuple[tuple[str, tuple[str, ...]], ...]


class _Parser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.title = ""
        self._in_title = False
        self.forms: list[Form] = []
        self._form: dict[str, object] | None = None
        self.by_id: dict[str, str] = {}
        self._open_ids: list[tuple[str, str, list[str]]] = []
        self.messages: list[str] = []
        self._message_depth: list[tuple[str, list[str]]] = []
        self.entries: list[str] = []
        self._in_entries = False
        self._entry: list[str] | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {name: (value or "") for name, value in attrs}
        if tag == "title":
            self._in_title = True
        if tag == "form":
            self._form = {
                "action": values.get("action", ""),
                "method": values.get("method", "get").lower(),
                "fields": [],
                "hidden": {},
            }
        if tag in ("input", "select", "textarea") and self._form is not None:
            name = values.get("name")
            if name:
                fields = self._form["fields"]
                assert isinstance(fields, list)
                if name not in fields:
                    fields.append(name)
                if tag == "input" and values.get("type") == "hidden":
                    hidden = self._form["hidden"]
                    assert isinstance(hidden, dict)
                    hidden[name] = values.get("value", "")
        element_id = values.get("id")
        if element_id:
            self._open_ids.append((tag, element_id, []))
            if element_id == "entries":
                self._in_entries = True
        if "message" in values.get("class", "").split():
            self._message_depth.append((tag, []))
        if tag == "li" and self._in_entries:
            self._entry = []

    def handle_endtag(self, tag: str) -> None:
        if tag == "title":
            self._in_title = False
        if tag == "form" and self._form is not None:
            fields = self._form["fields"]
            hidden = self._form["hidden"]
            assert isinstance(fields, list)
            assert isinstance(hidden, dict)
            self.forms.append(
                Form(
                    action=str(self._form["action"]),
                    method=str(self._form["method"]),
                    fields=tuple(str(f) for f in fields),
                    hidden={str(k): str(v) for k, v in hidden.items()},
                )
            )
            self._form = None
        if tag == "li" and self._entry is not None:
            self.entries.append(" ".join("".join(self._entry).split()))
            self._entry = None
        if self._open_ids and self._open_ids[-1][0] == tag:
            _, element_id, text = self._open_ids.pop()
            self.by_id[element_id] = " ".join("".join(text).split())
            if element_id == "entries":
                self._in_entries = False
        if self._message_depth and self._message_depth[-1][0] == tag:
            _, text = self._message_depth.pop()
            message = " ".join("".join(text).split())
            if message:
                self.messages.append(message)

    def handle_data(self, data: str) -> None:
        if self._in_title:
            self.title += data
        for _, _, text in self._open_ids:
            text.append(data)
        for _, text in self._message_depth:
            text.append(data)
        if self._entry is not None:
            self._entry.append(data)


def parse_screen(html: str) -> Screen:
    parser = _Parser()
    parser.feed(html)
    parser.close()
    return Screen(
        title=" ".join(parser.title.split()),
        forms=tuple(parser.forms),
        by_id=dict(parser.by_id),
        messages=tuple(parser.messages),
        entries=tuple(parser.entries),
    )


def recognise(screen: Screen, known: Mapping[str, ScreenSpec]) -> str | None:
    """Which known screen this is, by fingerprint, or None."""
    fingerprint = screen.fingerprint
    for name, spec in known.items():
        if spec == fingerprint:
            return name
    return None


def court_text(text: str, *, limit: int = 300) -> str:
    """A court message as the record keeps it: whitespace-collapsed and
    bounded, so a hostile or broken page cannot fill the record."""
    collapsed = " ".join(text.split())
    return collapsed if len(collapsed) <= limit else collapsed[: limit - 1] + "…"
