"""The scalar and structured field parsers every case entity shares.

Extracted from core/debtors.py the moment a second entity module needed them
(issue #249 adds ten), for the same reason insolvia_core exists: one owner per
rule. "What is a well-formed form date" must have exactly one answer, because a
creditor's `date_incurred` and a debtor's `signed_at` are the same kind of fact
and a divergence between them would be invisible until an audit.

Every parser here follows the storage-validation contract from
docs/reference/case-data-model.md: SHAPE AND TYPE ONLY, absent values accepted
everywhere. Intake is progressive — a half-finished questionnaire must persist
— so `None` is always a valid value and nothing here is "required".
Completeness against a chapter's forms is the forms engine's check, not these
functions'.

Errors accumulate into a caller-owned dict keyed by field path, so one request
reports every malformed field at once rather than one per round trip.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import UTC, date, datetime
from decimal import Decimal, InvalidOperation
from typing import Final

# The default cap for a single-line free-text field. Individual call sites
# override it where the form itself is narrower (a name suffix) or wider (a
# venue explanation).
MAX_TEXT: Final = 200

# Money is a fixed-scale decimal carried as a string — NEVER a float, and never
# accepted as a JSON number, because a number has already been through the
# caller's floating-point representation by the time it gets here. The cap is
# far above any schedule a consumer case will carry and far below anything
# Decimal handles awkwardly.
_MAX_MONEY: Final = Decimal("999999999999.99")

# The "which debtor" column that recurs across the schedules — who incurred a
# claim (106D/E/F), who owns an asset (106A/B). One vocabulary because the
# forms print one checkbox set, including the awkward fourth option ("at least
# one of the debtors and another") verbatim from the forms.
DEBTOR_ATTRIBUTION: Final = (
    "debtor_1",
    "debtor_2",
    "both",
    "at_least_one_plus_another",
)


@dataclass(frozen=True)
class PersonName:
    """Four discrete parts, never one string — the IEPD has no single-string
    fallback for names, and neither do we."""

    given: str | None = None
    middle: str | None = None
    surname: str | None = None
    suffix: str | None = None


@dataclass(frozen=True)
class Address:
    """Structured parts AND a raw fallback.

    The `raw` member follows the IEPD, which itself carries a free-text
    fallback for addresses that will not parse — a credit report's creditor
    block often arrives as an unparseable blob, and refusing it would refuse
    the creditor.
    """

    line1: str | None = None
    line2: str | None = None
    city: str | None = None
    state: str | None = None
    postal_code: str | None = None
    raw: str | None = None


def timestamp() -> str:
    """The server's write-time instant, RFC 3339, UTC, microseconds, Z-suffixed
    — the one shape every stored `created_at`/`updated_at` uses."""
    return datetime.now(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def text(
    value: object, path: str, errors: dict[str, str], *, limit: int = MAX_TEXT
) -> str | None:
    """A single-line free-text field, or None when absent.

    An empty or whitespace-only string collapses to None rather than being
    stored: "the user cleared this box" and "the user never filled it in" are
    the same state on a form, and keeping them distinct would mean provenance
    for the act of deleting (see populated_paths in core/provenance.py).
    """
    if value is None:
        return None
    if not isinstance(value, str):
        errors[path] = "Must be text."
        return None
    stripped = value.strip()
    if not stripped:
        return None
    if len(stripped) > limit:
        errors[path] = f"Must be {limit} characters or fewer."
        return None
    if "\n" in stripped or "\r" in stripped:
        errors[path] = "Must be a single line."
        return None
    return stripped


def narrative(
    value: object, path: str, errors: dict[str, str], *, limit: int = 2000
) -> str | None:
    """A multi-line free-text field — the "describe", "explain" and "specify"
    boxes the forms are full of. Same collapse-to-None rule as `text`; newlines
    allowed because the box on the form has more than one line."""
    if value is None:
        return None
    if not isinstance(value, str):
        errors[path] = "Must be text."
        return None
    stripped = value.strip()
    if not stripped:
        return None
    if len(stripped) > limit:
        errors[path] = f"Must be {limit} characters or fewer."
        return None
    return stripped


def form_date(value: object, path: str, errors: dict[str, str]) -> str | None:
    """A calendar date, `YYYY-MM-DD`, or None when absent.

    Checked rather than taken as free text. docs/reference/case-data-model.md:
    a form date has no time and no zone because it is a calendar fact, not an
    instant. Parsed rather than pattern-matched: `2019-02-30` matches every
    plausible regex and is not a day.
    """
    if value is None:
        return None
    if not isinstance(value, str):
        errors[path] = "Must be a date."
        return None
    stripped = value.strip()
    if not stripped:
        return None
    try:
        parsed = date.fromisoformat(stripped)
    except ValueError:
        errors[path] = "Must be a date in YYYY-MM-DD form."
        return None
    # `date.fromisoformat` also accepts "20190214"; the stored form is one shape.
    if parsed.isoformat() != stripped:
        errors[path] = "Must be a date in YYYY-MM-DD form."
        return None
    return stripped


def money(value: object, path: str, errors: dict[str, str]) -> str | None:
    """A dollar amount: fixed-scale decimal, two places, carried as a string.

    NEVER a float, and never accepted as a JSON number — a number has been
    through the sender's binary floating point before it arrives, which is
    exactly the corruption the string representation exists to prevent. The
    stored form is canonical (`"1200.00"`, always two places) so that two
    records holding the same amount hold the same string.

    Non-negative on purpose: every amount box on these schedules is a magnitude
    ("amount of claim", "current value"), and a negative one is a data-entry
    error, not a fact.
    """
    if value is None:
        return None
    if not isinstance(value, str):
        errors[path] = 'Must be an amount carried as a string, like "1200.00".'
        return None
    stripped = value.strip()
    if not stripped:
        return None
    try:
        parsed = Decimal(stripped)
    except InvalidOperation:
        errors[path] = 'Must be a dollar amount, like "1200.00".'
        return None
    if not parsed.is_finite() or parsed < 0:
        errors[path] = "Must be a non-negative dollar amount."
        return None
    if parsed > _MAX_MONEY:
        errors[path] = "Amount is too large."
        return None
    exponent = parsed.as_tuple().exponent
    if isinstance(exponent, int) and exponent < -2:
        errors[path] = "Must have at most two decimal places."
        return None
    return f"{parsed.quantize(Decimal('0.01')):f}"


def boolean(value: object, path: str, errors: dict[str, str]) -> bool | None:
    """A yes/no answer, or None when the question has not been answered.

    `False` is an answer, not an absence — core/provenance.populated_paths says
    why at length — so nothing here collapses it.
    """
    if value is None:
        return None
    if not isinstance(value, bool):
        errors[path] = "Must be true or false."
        return None
    return value


def whole_number(
    value: object, path: str, errors: dict[str, str], *, maximum: int = 1000
) -> int | None:
    """A small non-negative integer — an age, a count. `bool` is checked first
    because it IS an int in Python, and `True` stored as a count of 1 is the
    kind of bug that survives every test that only sends real numbers."""
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int):
        errors[path] = "Must be a whole number."
        return None
    if value < 0 or value > maximum:
        errors[path] = f"Must be between 0 and {maximum}."
        return None
    return value


def choice(
    value: object, allowed: Sequence[str], path: str, errors: dict[str, str]
) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or value not in allowed:
        errors[path] = "Must be one of " + ", ".join(allowed) + "."
        return None
    return value


def choice_list(
    value: object, allowed: Sequence[str], path: str, errors: dict[str, str]
) -> tuple[str, ...]:
    """A "check all that apply" answer: a list of distinct members of `allowed`.

    Order is preserved (it is the order the boxes were ticked in, which nothing
    reads) and duplicates are rejected rather than collapsed — a duplicate is a
    client bug worth hearing about, not a preference to honour.
    """
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors[path] = "Must be a list."
        return ()
    chosen: list[str] = []
    for index, raw in enumerate(value):
        member = choice(raw, allowed, f"{path}[{index}]", errors)
        if member is None:
            continue
        if member in chosen:
            errors[f"{path}[{index}]"] = "Duplicate choice."
            continue
        chosen.append(member)
    return tuple(chosen)


def string_list(
    value: object,
    path: str,
    errors: dict[str, str],
    *,
    limit: int = MAX_TEXT,
) -> tuple[str, ...]:
    """A list of single-line strings, attributed whole by provenance (the
    elements carry no ids, so populated_paths addresses the list as one path —
    the `employer_ids` precedent)."""
    if value is None:
        return ()
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        errors[path] = "Must be a list."
        return ()
    members: list[str] = []
    for index, raw in enumerate(value):
        member = text(raw, f"{path}[{index}]", errors, limit=limit)
        if member is not None:
            members.append(member)
    return tuple(members)


def mapping(value: object, path: str, errors: dict[str, str]) -> Mapping[str, object]:
    if value is None:
        return {}
    if not isinstance(value, Mapping):
        errors[path] = "Must be an object."
        return {}
    return value


def parse_name(value: object, path: str, errors: dict[str, str]) -> PersonName:
    raw = mapping(value, path, errors)
    return PersonName(
        given=text(raw.get("given"), f"{path}.given", errors),
        middle=text(raw.get("middle"), f"{path}.middle", errors),
        surname=text(raw.get("surname"), f"{path}.surname", errors),
        suffix=text(raw.get("suffix"), f"{path}.suffix", errors, limit=20),
    )


def parse_address(value: object, path: str, errors: dict[str, str]) -> Address:
    raw = mapping(value, path, errors)
    return Address(
        line1=text(raw.get("line1"), f"{path}.line1", errors),
        line2=text(raw.get("line2"), f"{path}.line2", errors),
        city=text(raw.get("city"), f"{path}.city", errors),
        state=text(raw.get("state"), f"{path}.state", errors, limit=40),
        postal_code=text(
            raw.get("postal_code"), f"{path}.postal_code", errors, limit=12
        ),
        raw=text(raw.get("raw"), f"{path}.raw", errors, limit=500),
    )


def prune_body(body: Mapping[str, object]) -> dict[str, object]:
    """`prune` over a record body, typed as the mapping it always is — mypy
    cannot see through the generic recursion, and a `cast` here would be the
    same claim with less checking."""
    pruned = prune(dict(body))
    return pruned if isinstance(pruned, dict) else {}


def prune(value: object) -> object:
    """Drop absent members from a record, recursively.

    Absent means None, and — inside a MAP — an empty string, list, tuple or
    map, matching populated_paths so that what is stored and what invariant 1
    validated agree. Two limits worth stating rather than discovering: a None
    INSIDE a list survives (lists here hold records, never holes), and an empty
    container nested in a list is not dropped.

    `False` and `0` survive everywhere. They are answers, the same rule
    populated_paths states at length.
    """
    if isinstance(value, Mapping):
        pruned = {
            key: prune(member) for key, member in value.items() if member is not None
        }
        return {
            key: member
            for key, member in pruned.items()
            if not (isinstance(member, (dict, list, tuple)) and not member)
        }
    if isinstance(value, (list, tuple)):
        return [prune(member) for member in value]
    return value
