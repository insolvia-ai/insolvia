"""The client questionnaire's sections, and which of them a firm shows its
clients (ADR 0023 PR 3 / issue #362).

Two things live here, and they are different kinds of fact:

THE CATALOGUE is ours and ships in the code: six sections that follow the
schedules a debtor's answers end up on — personal information, property,
debts, income, expenses, other. Each has a stable `id`, a title, a line
saying what it covers (for staff), and default instructions (for the
client). THE IDS ARE A CONTRACT: issue #363 hangs every question off one,
and a candidate written from an answer records the section it came from, so
renaming one would orphan answers already given. Add a section; never rename
or reuse an id.

THE CONFIG is the firm's: per section, whether its clients see it and what
the firm wants to tell them about it. `personal_information` is ALWAYS ON —
a petition cannot be prepared without the debtor's name, address and
identity, so a firm that switched it off would have a portal that collects
nothing a case can start from. The other five are switchable.

WHAT SWITCHING OFF MEANS, AND WHAT IT DOES NOT. A section switched off is
hidden from the firm's CLIENTS: the portal's questionnaire omits it. It is
not removed from anything staff see — the catalogue, the firm's config
screen, and (from #363) the review queue all keep every section, because a
firm that takes a section in-house still needs it at review. Switching never
touches case data; this module writes nothing to a case.

STORAGE. One item per firm, in the firm's own partition beside `META`,
`USER#` and `LIBCREDITOR#` (`firms.partition_key`): `SK = QUESTIONNAIRE`.
Its own item rather than attributes on the firm's META row because META is
read on every authenticated request (`FirmStore.find_user`'s firm half) and
up to six blocks of instructions have no business riding along on all of
them. ABSENT MEANS DEFAULTS — every firm that predates this feature, and
every firm that pressed "reset to defaults", which deletes the item rather
than writing a copy of today's defaults: a copy would freeze them, and a
later improvement to a default instruction would never reach the firms that
never customised it.

A SAVE IS THE WHOLE RECORD, like `library_creditors`: every section, each
with its switch and its instructions, because "None means leave unchanged"
and "None means use the default" cannot both be true of the same field.
Instructions sent blank — or identical to the default — are stored as no
instructions at all, so the default keeps following the catalogue.
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Final

from insolvia_core.errors import FieldValidationError, ValidationError

from .fields import boolean, narrative, timestamp
from .firms import partition_key

# The DynamoDB sort key, alongside firms.py's "META" and "USER#" and
# library_creditors' "LIBCREDITOR#" in the same partition. One per firm.
SORT_KEY: Final = "QUESTIONNAIRE"

# The library creditor's `notes` limit, for the same kind of field: firm
# prose shown on one screen. Long enough for a paragraph and a list.
MAX_INSTRUCTIONS: Final = 2000

PERSONAL_INFORMATION: Final = "personal_information"
PROPERTY: Final = "property"
DEBTS: Final = "debts"
INCOME: Final = "income"
EXPENSES: Final = "expenses"
OTHER: Final = "other"


@dataclass(frozen=True)
class Section:
    """One catalogue entry. `covers` is for staff — what the section maps
    to; `default_instructions` is what a client reads when the firm has
    written nothing of its own."""

    id: str
    title: str
    covers: str
    default_instructions: str
    switchable: bool


# In the order a client works through them — the order of the petition and
# its schedules, which is also the order a paralegal reviews in.
CATALOGUE: Final = (
    Section(
        id=PERSONAL_INFORMATION,
        title="Personal information",
        covers="The voluntary petition: names, addresses, contact details, "
        "household and prior filings.",
        default_instructions=(
            "Tell us who you are and where you live. Use your full legal name "
            "as it appears on your identification, and list any other names "
            "you have used in the last eight years."
        ),
        switchable=False,
    ),
    Section(
        id=PROPERTY,
        title="Property",
        covers="Schedule A/B and the exemptions on Schedule C: real estate, "
        "vehicles, accounts, household goods and other assets.",
        default_instructions=(
            "List everything you own or have an interest in, wherever it is: "
            "your home, vehicles, bank accounts, retirement accounts, "
            "household goods and anything else of value. An estimate of what "
            "each is worth today is enough."
        ),
        switchable=True,
    ),
    Section(
        id=DEBTS,
        title="Debts",
        covers="Schedules D and E/F: secured, priority and unsecured "
        "creditors, and the codebtors on Schedule H.",
        default_instructions=(
            "List everyone you owe money to, including debts you dispute and "
            "debts someone else also owes. Recent statements or collection "
            "letters are the best source for names, addresses and balances."
        ),
        switchable=True,
    ),
    Section(
        id=INCOME,
        title="Income",
        covers="Schedule I and the means test: employment, wages, benefits "
        "and other income for you and your household.",
        default_instructions=(
            "Tell us about your work and every source of income your household "
            "receives. Recent pay stubs and benefit letters will help you give "
            "exact amounts."
        ),
        switchable=True,
    ),
    Section(
        id=EXPENSES,
        title="Expenses",
        covers="Schedule J: the household's regular monthly expenses.",
        default_instructions=(
            "Tell us what your household spends in a typical month on housing, "
            "utilities, food, transportation, insurance and other regular "
            "costs."
        ),
        switchable=True,
    ),
    Section(
        id=OTHER,
        title="Other",
        covers="The statement of financial affairs, leases and contracts on "
        "Schedule G, and anything not asked elsewhere.",
        default_instructions=(
            "Answer a few questions about recent payments, transfers, lawsuits "
            "and other events, and tell us about any leases or contracts you "
            "are part of."
        ),
        switchable=True,
    ),
)

SECTION_IDS: Final = tuple(section.id for section in CATALOGUE)
_BY_ID: Final = {section.id: section for section in CATALOGUE}


@dataclass(frozen=True)
class SectionSetting:
    """A firm's choice for one section. `instructions` None means the
    catalogue's default — not "no instructions"."""

    enabled: bool
    instructions: str | None = None


@dataclass(frozen=True)
class QuestionnaireConfig:
    """A firm's stored questionnaire config — the item that exists only
    once the firm has saved something. `updated_by` is the saving firm
    user's subject: a fact about the write, never an authorization input."""

    firm_id: str
    sections: Mapping[str, SectionSetting]
    updated_at: str
    updated_by: str


@dataclass(frozen=True)
class ResolvedSection:
    """A catalogue section with the firm's config applied — what both the
    staff screen and the portal are built from."""

    section: Section
    enabled: bool
    custom_instructions: str | None

    @property
    def instructions(self) -> str:
        return self.custom_instructions or self.section.default_instructions


# Every section is ON by default: a firm that has never opened the screen
# shows its clients the whole questionnaire.
DEFAULT_SETTING: Final = SectionSetting(enabled=True)


def resolve(config: QuestionnaireConfig | None) -> tuple[ResolvedSection, ...]:
    """Every catalogue section, in catalogue order, with `config` applied.

    A section the stored config does not name — one added to the catalogue
    after the firm last saved — takes its default, so a new section reaches
    every firm. A stored id the catalogue no longer has is ignored (ids are
    never removed, but a read must not fail on one). `personal_information`
    is on whatever the item says: the rule is enforced on read as well as on
    write, so a hand-edited row cannot hide it.
    """
    stored = config.sections if config is not None else {}
    resolved: list[ResolvedSection] = []
    for section in CATALOGUE:
        setting = stored.get(section.id) or DEFAULT_SETTING
        resolved.append(
            ResolvedSection(
                section=section,
                enabled=setting.enabled or not section.switchable,
                custom_instructions=setting.instructions,
            )
        )
    return tuple(resolved)


def client_sections(config: QuestionnaireConfig | None) -> tuple[ResolvedSection, ...]:
    """The sections the firm's CLIENTS see — the enabled ones, in order."""
    return tuple(section for section in resolve(config) if section.enabled)


# ── Parsing a save ──────────────────────────────────────────────────


def parse_questionnaire_update(
    payload: Mapping[str, object],
) -> dict[str, SectionSetting]:
    """Validate PUT `/v1/firm/questionnaire`: `{"sections": [...]}`, one
    entry per catalogue section, each `{id, enabled, instructions}`.

    Every section must be named exactly once — a whole-record save, see the
    module docstring. Errors are keyed by `sections.<id>.<field>` where the
    id is known, so a screen can put each message beside its own section.
    """
    raw = payload.get("sections")
    if not isinstance(raw, list):
        raise FieldValidationError({"sections": "Must be a list of sections."})
    errors: dict[str, str] = {}
    settings: dict[str, SectionSetting] = {}
    for index, entry in enumerate(raw):
        if not isinstance(entry, Mapping):
            errors[f"sections.{index}"] = "Must be an object."
            continue
        section_id = entry.get("id")
        if not isinstance(section_id, str) or section_id not in _BY_ID:
            errors[f"sections.{index}.id"] = "Must be one of " + ", ".join(SECTION_IDS)
            continue
        if section_id in settings:
            errors[f"sections.{section_id}"] = "Named more than once."
            continue
        section = _BY_ID[section_id]
        path = f"sections.{section_id}"
        enabled = boolean(entry.get("enabled"), f"{path}.enabled", errors)
        if enabled is None and f"{path}.enabled" not in errors:
            errors[f"{path}.enabled"] = "Must be true or false."
        if enabled is False and not section.switchable:
            errors[f"{path}.enabled"] = f"{section.title} is always on."
        instructions = narrative(
            entry.get("instructions"),
            f"{path}.instructions",
            errors,
            limit=MAX_INSTRUCTIONS,
        )
        # Identical to the default is not a customisation: storing it would
        # freeze today's default for this firm (module docstring).
        if instructions == section.default_instructions:
            instructions = None
        settings[section_id] = SectionSetting(
            enabled=bool(enabled), instructions=instructions
        )
    for section_id in SECTION_IDS:
        if section_id not in settings and f"sections.{section_id}" not in errors:
            errors[f"sections.{section_id}"] = "Missing from the save."
    if errors:
        raise FieldValidationError(errors)
    return settings


def build_config(
    settings: Mapping[str, SectionSetting], *, firm_id: str, updated_by: str
) -> QuestionnaireConfig:
    return QuestionnaireConfig(
        firm_id=firm_id,
        sections=dict(settings),
        updated_at=timestamp(),
        updated_by=updated_by,
    )


# ── The stored item ─────────────────────────────────────────────────


def questionnaire_item(config: QuestionnaireConfig) -> dict[str, object]:
    """The exact stored item, shared by both FirmStore implementations.

    PK  FIRM#<firm_id>
    SK  QUESTIONNAIRE

    No GSI keys — the by-subject index stays people-only (`firms.firm_item`
    explains why that sparseness matters). `instructions` is sparse: absent
    is the default.
    """
    sections: dict[str, object] = {}
    for section_id, setting in config.sections.items():
        block: dict[str, object] = {"enabled": setting.enabled}
        if setting.instructions is not None:
            block["instructions"] = setting.instructions
        sections[section_id] = block
    return {
        "PK": partition_key(config.firm_id),
        "SK": SORT_KEY,
        "firmId": config.firm_id,
        "sections": sections,
        "updatedAt": config.updated_at,
        "updatedBy": config.updated_by,
    }


def questionnaire_from_item(item: Mapping[str, object]) -> QuestionnaireConfig:
    """Inverse of `questionnaire_item`. Raises ValidationError on a row this
    package did not write, as `firms.firm_from_item` does."""
    try:
        raw_sections = item["sections"]
        if not isinstance(raw_sections, Mapping):
            raise ValueError("sections is not a map")
        sections: dict[str, SectionSetting] = {}
        for section_id, raw in raw_sections.items():
            if not isinstance(raw, Mapping):
                raise ValueError(f"section {section_id!r} is not a map")
            enabled = raw.get("enabled")
            if not isinstance(enabled, bool):
                raise ValueError(f"section {section_id!r} has no enabled flag")
            instructions = raw.get("instructions")
            sections[str(section_id)] = SectionSetting(
                enabled=enabled,
                instructions=str(instructions) if instructions is not None else None,
            )
        return QuestionnaireConfig(
            firm_id=str(item["firmId"]),
            sections=sections,
            updated_at=str(item["updatedAt"]),
            updated_by=str(item["updatedBy"]),
        )
    except (KeyError, ValueError) as error:
        raise ValidationError(
            f"stored questionnaire item is malformed: {error}"
        ) from error


# ── JSON ────────────────────────────────────────────────────────────


def questionnaire_json(config: QuestionnaireConfig | None) -> dict[str, object]:
    """The firm's view (`/v1/firm/questionnaire`): EVERY section, switched
    off or not, with what it covers, the firm's own instructions and the
    default beside them. `isDefault` says no config is stored at all;
    `updatedAt`/`updatedBy` are explicit nulls then, `firm_json`'s rule."""
    return {
        "isDefault": config is None,
        "updatedAt": config.updated_at if config is not None else None,
        "updatedBy": config.updated_by if config is not None else None,
        "sections": [
            {
                "id": resolved.section.id,
                "title": resolved.section.title,
                "covers": resolved.section.covers,
                "switchable": resolved.section.switchable,
                "enabled": resolved.enabled,
                "instructions": resolved.custom_instructions,
                "defaultInstructions": resolved.section.default_instructions,
            }
            for resolved in resolve(config)
        ],
    }


def portal_questionnaire_json(
    config: QuestionnaireConfig | None,
) -> dict[str, object]:
    """The client's view (`/v1/portal/questionnaire`): the enabled sections
    only, each as the client reads it — a title and the instructions in
    force. Nothing about what is switched off (not even that something is),
    nothing staff-facing (`covers`), and nothing about who saved it."""
    return {
        "sections": [
            {
                "id": resolved.section.id,
                "title": resolved.section.title,
                "instructions": resolved.instructions,
            }
            for resolved in client_sections(config)
        ]
    }
