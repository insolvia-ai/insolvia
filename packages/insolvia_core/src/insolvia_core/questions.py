"""The client questionnaire's questions, and how an answer becomes a
candidate (ADR 0023 PR 4 / issue #363).

`questionnaire.py` owns the SECTIONS and a firm's config of them; this module
hangs QUESTIONS off those section ids. Each question has a stable id, the
section it belongs to, plain-language text for a debtor, the inputs it
takes, and the case field its answer maps to — a target (`debtors`, or one
of `case_collections.COLLECTIONS`) and where in that target's body each input
lands.

AN ANSWER IS NEVER CASE DATA. It is stored as a candidate
(`insolvia_core.candidates`) in the case's one review queue — origin channel
`client`, a `question` locator naming the section, the question and the
filing role it was answered for — and becomes case data only when a person
accepts it through the review routes, which mint `client_answered`
provenance under the same confirmation rule as `ai_extracted`
(`provenance.py`). Nothing in this module, or anything that imports it,
writes a case record.

THE QUESTION IDS ARE A CONTRACT, the way the section ids are: a stored
candidate names its question, and the review queue shows staff the
question's text from this catalogue. Add a question; never rename or reuse
an id. A question that is retired stays in the catalogue with
`retired=True` — the review queue still has to label the answers already
given, and a client can no longer give new ones.

THE FIRST CATALOGUE IS DELIBERATELY SMALL (ADR 0023's Risks call #363's
catalogue "the largest and least bounded surface"). Personal information is
covered as fully as a debtor can answer it — legal name, other names,
both addresses, phone, mobile, email. Each switchable section gets a few
high-value questions whose answer is one whole record. What is left out,
and why, is in docs/reference/case-data-model.md § "Answers from the client
portal"; the short version is: no tax id (it is a sealed item, and a
candidate payload is not a place for the digits), nothing that needs a
second record to mean anything (a claim needs its creditor), and nothing
that is an attorney's judgement rather than a debtor's fact (venue,
exemptions, intentions, priority).

THREE PLACEMENTS — how an answer's inputs become a target body:

- `merge`: the inputs are top-level fields of the target body, plus the
  question's `fixed` values (an asset's `category`). A record question —
  each answer is one new record — and the debtor's scalar fields.
- `nest`: the inputs are the members of one structured field of the debtor
  (`name` → given, middle, surname, suffix).
- `append`: the inputs are one element of a list field of the debtor
  (`other_names_used`); the answer mints the element's id, so provenance can
  address it (`debtors.parse_other_names` explains why ids are required).

Pure: no boto3, no framework.
"""

from __future__ import annotations

import uuid
from collections.abc import Mapping
from dataclasses import dataclass, field, replace
from typing import Final

from insolvia_core.candidates import (
    PENDING,
    Candidate,
    CandidateOrigin,
    ProposalDraft,
    create_candidate,
)
from insolvia_core.case_collections import COLLECTIONS
from insolvia_core.case_entities import entity_body, parse_entity
from insolvia_core.clients import CLIENT_ROLES
from insolvia_core.debtors import debtor_body, parse_debtor
from insolvia_core.errors import (
    ConflictError,
    FieldValidationError,
    NotFoundError,
    ValidationError,
)
from insolvia_core.fields import prune_body, timestamp
from insolvia_core.questionnaire import CATALOGUE as SECTION_CATALOGUE
from insolvia_core.questionnaire import (
    DEBTS,
    EXPENSES,
    INCOME,
    OTHER,
    PERSONAL_INFORMATION,
    PROPERTY,
    SECTION_IDS,
    QuestionnaireConfig,
    ResolvedSection,
    portal_questionnaire_json,
)

# The candidate channel and locator kind every answer carries. The channel
# is candidates.ORIGIN_CHANNELS' `client`; the locator kind is what tells a
# reader this locator names a question rather than a page of a document.
CHANNEL: Final = "client"
LOCATOR_KIND: Final = "question"

# The debtor target, beside the generic collections.
DEBTORS: Final = "debtors"

# Input types — what the portal renders and what the value of each input is.
# `address` is a structured value (`fields.Address` without `raw`, the
# fallback for blocks a machine could not parse); every other type is a
# scalar the target's own parser checks.
TEXT: Final = "text"
LONG_TEXT: Final = "long_text"
DATE: Final = "date"
MONEY: Final = "money"
WHOLE_NUMBER: Final = "whole_number"
ADDRESS: Final = "address"
INPUT_TYPES: Final = (TEXT, LONG_TEXT, DATE, MONEY, WHOLE_NUMBER, ADDRESS)

ADDRESS_MEMBERS: Final = ("line1", "line2", "city", "state", "postal_code", "county")

MERGE: Final = "merge"
NEST: Final = "nest"
APPEND: Final = "append"

# A bound on what one client can put in front of a paralegal at once. A
# repeating question is an open door; 200 pending answers is far beyond any
# honest questionnaire (the whole first catalogue is 18 questions) and far
# below a queue a script could make unreviewable.
MAX_PENDING_ANSWERS: Final = 200


@dataclass(frozen=True)
class AnswerInput:
    """One box on the portal screen. `key` is the key in the answer's value
    and — for `merge` and `nest` — the field of the target body it lands
    in."""

    key: str
    label: str
    type: str
    required: bool = False
    help: str | None = None


@dataclass(frozen=True)
class Question:
    id: str
    section: str
    text: str
    target: str
    inputs: tuple[AnswerInput, ...]
    # Many answers (each vehicle, each other name) or one live answer at a
    # time (a legal name — a second while the first is pending is an edit).
    repeats: bool = False
    # Answered for a filing role, and bound by the binding's `roles` (ADR
    # 0023 § Binding): the debtor's own fields, and records that belong to
    # one debtor (an employment). Household-level questions are not.
    per_debtor: bool = False
    placement: str = MERGE
    # For `nest` and `append`: the debtor field the inputs live under.
    debtor_field: str | None = None
    # Constant members of a record answer — the asset's category, the
    # employment's status. Server-set: never in the value a client sends or
    # gets back.
    fixed: Mapping[str, object] = field(default_factory=dict)
    help: str | None = None
    retired: bool = False

    @property
    def input_keys(self) -> tuple[str, ...]:
        return tuple(answer_input.key for answer_input in self.inputs)


def _address(key: str, label: str, *, help: str | None = None) -> AnswerInput:
    return AnswerInput(key=key, label=label, type=ADDRESS, help=help)


# In section order, then the order a client answers them.
CATALOGUE: Final = (
    # ── Personal information: the debtor record, as far as a debtor can
    # state it. Every question is per debtor.
    Question(
        id="personal_information.legal_name",
        section=PERSONAL_INFORMATION,
        text="What is your full legal name?",
        help="As it appears on your driver's license or other identification.",
        target=DEBTORS,
        placement=NEST,
        debtor_field="name",
        per_debtor=True,
        inputs=(
            AnswerInput("given", "First name", TEXT, required=True),
            AnswerInput("middle", "Middle name", TEXT),
            AnswerInput("surname", "Last name", TEXT, required=True),
            AnswerInput("suffix", "Suffix", TEXT, help="Jr., Sr., III"),
        ),
    ),
    Question(
        id="personal_information.other_names",
        section=PERSONAL_INFORMATION,
        text="Have you used any other names in the last 8 years?",
        help=(
            "Include maiden names, earlier married names, and business names "
            "you used. Add one at a time."
        ),
        target=DEBTORS,
        placement=APPEND,
        debtor_field="other_names_used",
        per_debtor=True,
        repeats=True,
        inputs=(
            AnswerInput("given", "First name", TEXT),
            AnswerInput("middle", "Middle name", TEXT),
            AnswerInput("surname", "Last name", TEXT),
            AnswerInput("business_name", "Business name", TEXT),
        ),
    ),
    Question(
        id="personal_information.residence_address",
        section=PERSONAL_INFORMATION,
        text="Where do you live?",
        help="The address of the home you live in now, including the county.",
        target=DEBTORS,
        per_debtor=True,
        inputs=(_address("residence_address", "Home address"),),
    ),
    Question(
        id="personal_information.mailing_address",
        section=PERSONAL_INFORMATION,
        text="Do you get mail somewhere else?",
        help="Only if your mail goes to a different address than your home.",
        target=DEBTORS,
        per_debtor=True,
        inputs=(_address("mailing_address", "Mailing address"),),
    ),
    Question(
        id="personal_information.phone",
        section=PERSONAL_INFORMATION,
        text="What is the best phone number to reach you?",
        target=DEBTORS,
        per_debtor=True,
        inputs=(AnswerInput("phone", "Phone number", TEXT, required=True),),
    ),
    Question(
        id="personal_information.mobile",
        section=PERSONAL_INFORMATION,
        text="Do you have a cell phone number?",
        help="Only if it is different from the number above.",
        target=DEBTORS,
        per_debtor=True,
        inputs=(AnswerInput("mobile", "Cell phone number", TEXT, required=True),),
    ),
    Question(
        id="personal_information.email",
        section=PERSONAL_INFORMATION,
        text="What email address should we use for you?",
        target=DEBTORS,
        per_debtor=True,
        inputs=(AnswerInput("email", "Email address", TEXT, required=True),),
    ),
    # ── Property: one asset per answer.
    Question(
        id="property.real_estate",
        section=PROPERTY,
        text="Do you own a home, land or other real estate?",
        help="Add each property you own or have a share in.",
        target="assets",
        repeats=True,
        fixed={"category": "real_property"},
        inputs=(
            AnswerInput(
                "description",
                "Address or description",
                LONG_TEXT,
                required=True,
            ),
            AnswerInput("value_entire", "What it is worth today", MONEY),
        ),
    ),
    Question(
        id="property.vehicle",
        section=PROPERTY,
        text="Do you own a car, truck, motorcycle or other vehicle?",
        help="Add each vehicle, including ones you are still paying for.",
        target="assets",
        repeats=True,
        fixed={"category": "vehicle"},
        inputs=(
            AnswerInput("year", "Year", WHOLE_NUMBER),
            AnswerInput("make", "Make", TEXT, required=True),
            AnswerInput("model", "Model", TEXT),
            AnswerInput("mileage", "Approximate mileage", WHOLE_NUMBER),
            AnswerInput("value_entire", "What it is worth today", MONEY),
        ),
    ),
    Question(
        id="property.bank_account",
        section=PROPERTY,
        text="Do you have checking, savings or other bank accounts?",
        help="Add each account, with the bank's name and today's balance.",
        target="assets",
        repeats=True,
        fixed={"category": "deposits_of_money"},
        inputs=(
            AnswerInput(
                "description",
                "Bank and type of account",
                TEXT,
                required=True,
                help="For example: First Example Bank, checking",
            ),
            AnswerInput("value_entire", "Balance today", MONEY),
        ),
    ),
    Question(
        id="property.retirement_account",
        section=PROPERTY,
        text="Do you have a 401(k), IRA, pension or other retirement account?",
        target="assets",
        repeats=True,
        fixed={"category": "retirement_accounts"},
        inputs=(
            AnswerInput(
                "description",
                "Type of account and who holds it",
                TEXT,
                required=True,
            ),
            AnswerInput("value_entire", "Balance today", MONEY),
        ),
    ),
    # ── Debts: who is owed. A creditor record only — see the module
    # docstring for why the claim (amount, class) is not asked here.
    Question(
        id="debts.creditor",
        section=DEBTS,
        text="Who do you owe money to?",
        help=(
            "Add each person or company you owe, including debts you dispute. "
            "Use the name and address on their latest statement or letter."
        ),
        target="creditors",
        repeats=True,
        inputs=(
            AnswerInput("name", "Name of the person or company", TEXT, required=True),
            _address("address", "Their address"),
        ),
    ),
    # ── Income: the debtor's employment; one record per job.
    Question(
        id="income.employment",
        section=INCOME,
        text="Where do you work?",
        help="Add each job you have now.",
        target="employments",
        repeats=True,
        per_debtor=True,
        fixed={"status": "employed"},
        inputs=(
            AnswerInput("employer_name", "Employer", TEXT, required=True),
            AnswerInput("occupation", "Your job title", TEXT),
            _address("employer_address", "Employer's address"),
            AnswerInput("employed_since", "When you started", DATE),
        ),
    ),
    # ── Expenses: the household's largest regular costs, one each.
    Question(
        id="expenses.housing",
        section=EXPENSES,
        text="How much do you pay each month for rent or your mortgage?",
        target="expenses",
        fixed={"category": "rent_or_home_ownership"},
        inputs=(AnswerInput("amount", "Monthly amount", MONEY, required=True),),
    ),
    Question(
        id="expenses.utilities",
        section=EXPENSES,
        text="How much do you pay each month for electricity, heat and gas?",
        target="expenses",
        fixed={"category": "electricity_heat_gas"},
        inputs=(AnswerInput("amount", "Monthly amount", MONEY, required=True),),
    ),
    Question(
        id="expenses.food",
        section=EXPENSES,
        text="How much does your household spend each month on food and "
        "household supplies?",
        target="expenses",
        fixed={"category": "food_and_housekeeping"},
        inputs=(AnswerInput("amount", "Monthly amount", MONEY, required=True),),
    ),
    Question(
        id="expenses.transportation",
        section=EXPENSES,
        text="How much do you spend each month on gas, transit and car upkeep?",
        help="Not your car payment or insurance.",
        target="expenses",
        fixed={"category": "transportation"},
        inputs=(AnswerInput("amount", "Monthly amount", MONEY, required=True),),
    ),
    # ── Other: the leases and contracts on Schedule G.
    Question(
        id="other.lease",
        section=OTHER,
        text="Are you part of a lease or contract that is still in effect?",
        help="For example: an apartment lease, a car lease, or a phone contract.",
        target="contract_leases",
        repeats=True,
        inputs=(
            AnswerInput(
                "counterparty_name",
                "Who the lease or contract is with",
                TEXT,
                required=True,
            ),
            AnswerInput("description", "What it is for", LONG_TEXT),
        ),
    ),
)

QUESTION_IDS: Final = tuple(question.id for question in CATALOGUE)
_BY_ID: Final = {question.id: question for question in CATALOGUE}


def question(question_id: str) -> Question | None:
    """The catalogue entry, retired ones included — the review queue labels
    every answer ever given."""
    return _BY_ID.get(question_id)


def questions_for(section_id: str) -> tuple[Question, ...]:
    """The live questions of one section, in order."""
    return tuple(
        entry
        for entry in CATALOGUE
        if entry.section == section_id and not entry.retired
    )


# ── Turning a value into a payload ──────────────────────────────────


def _raw_input(answer_input: AnswerInput, raw: object) -> object:
    """An input's raw value, narrowed to what the input may carry. An
    address keeps its known members only — never `raw`, the fallback a
    machine uses for a block it could not parse."""
    if answer_input.type == ADDRESS and isinstance(raw, Mapping):
        return {member: raw[member] for member in ADDRESS_MEMBERS if member in raw}
    return raw


def _validate_target(
    entry: Question, payload: Mapping[str, object]
) -> dict[str, object]:
    """The payload through the TARGET'S OWN PARSER — `parse_debtor` or the
    collection's `parse_entity`, the functions every staff write and every
    other candidate stream runs — and back as the canonical, pruned body.
    Provenance is not enforced: a candidate is not case data."""
    if entry.target == DEBTORS:
        return prune_body(debtor_body(parse_debtor(payload, enforce_provenance=False)))
    kind = COLLECTIONS[entry.target]
    return prune_body(
        entity_body(parse_entity(kind, payload, enforce_provenance=False))
    )


def _error_key(entry: Question, path: str) -> str:
    """A target-body error path as the portal knows it: `value.<input>…`."""
    if entry.placement == NEST and entry.debtor_field is not None:
        path = path.removeprefix(f"{entry.debtor_field}.")
    elif entry.placement == APPEND and entry.debtor_field is not None:
        prefix = f"{entry.debtor_field}["
        if path.startswith(prefix) and "]." in path:
            path = path.split("].", 1)[1]
    return f"value.{path}"


def build_payload(
    entry: Question,
    value: object,
    *,
    element_id: str | None = None,
) -> dict[str, object]:
    """Validate an answer's `value` and return the candidate payload.

    `value` is an object keyed by the question's input keys; any other key
    is ignored, so a client cannot reach a field the question does not ask
    about. `element_id` is an `append` answer's list-element id — minted on
    the first write and kept on an edit, so the element the reviewer sees
    is the one the client changed.
    """
    if not isinstance(value, Mapping):
        raise FieldValidationError({"value": "Must be an object."})
    inputs = {
        answer_input.key: _raw_input(answer_input, value.get(answer_input.key))
        for answer_input in entry.inputs
        if value.get(answer_input.key) is not None
    }
    if entry.placement == NEST and entry.debtor_field is not None:
        raw_payload: dict[str, object] = {entry.debtor_field: inputs}
    elif entry.placement == APPEND and entry.debtor_field is not None:
        raw_payload = {
            entry.debtor_field: [{"id": element_id or str(uuid.uuid4()), **inputs}]
        }
    else:
        raw_payload = {**inputs, **entry.fixed}

    try:
        body = _validate_target(entry, raw_payload)
    except FieldValidationError as error:
        raise FieldValidationError(
            {_error_key(entry, path): message for path, message in error.fields.items()}
        ) from error

    # Only the question's own fields survive — the parser re-emits the whole
    # body shape, and an empty-but-present member is not part of the answer.
    if entry.placement in (NEST, APPEND) and entry.debtor_field is not None:
        payload = (
            {entry.debtor_field: body[entry.debtor_field]}
            if entry.debtor_field in body
            else {}
        )
    else:
        payload = {
            key: body[key] for key in (*entry.input_keys, *entry.fixed) if key in body
        }

    answered = answer_value(entry, payload)
    errors: dict[str, str] = {}
    for answer_input in entry.inputs:
        if answer_input.required and answered.get(answer_input.key) is None:
            errors[f"value.{answer_input.key}"] = "This is required."
    if not answered and not errors:
        errors["value"] = "Answer at least one part of the question."
    if errors:
        raise FieldValidationError(errors)
    return payload


def answer_value(entry: Question, payload: Mapping[str, object]) -> dict[str, object]:
    """The inverse of `build_payload`: the client's own value, from a
    candidate payload. `fixed` members and an appended element's id are
    the server's, so they are not part of it."""
    if entry.placement == NEST and entry.debtor_field is not None:
        nested = payload.get(entry.debtor_field)
        source: Mapping[str, object] = nested if isinstance(nested, Mapping) else {}
    elif entry.placement == APPEND and entry.debtor_field is not None:
        elements = payload.get(entry.debtor_field)
        first = elements[0] if isinstance(elements, list) and elements else None
        source = first if isinstance(first, Mapping) else {}
    else:
        source = payload
    return {key: source[key] for key in entry.input_keys if key in source}


def element_id_of(entry: Question, payload: Mapping[str, object]) -> str | None:
    """An `append` answer's list-element id, or None."""
    if entry.placement != APPEND or entry.debtor_field is None:
        return None
    elements = payload.get(entry.debtor_field)
    if isinstance(elements, list) and elements and isinstance(elements[0], Mapping):
        element_id = elements[0].get("id")
        return element_id if isinstance(element_id, str) else None
    return None


# ── The answer as a candidate ───────────────────────────────────────


@dataclass(frozen=True)
class AnswerRequest:
    """A validated `POST /v1/portal/answers`."""

    question: Question
    filing_role: str
    payload: Mapping[str, object]


def _enabled_question(
    question_id: object, sections: tuple[ResolvedSection, ...]
) -> Question:
    """The question, if it is live AND its section is one this client's
    firm shows. A question in a switched-off section is refused exactly as
    an unknown id is: to the client, that section does not exist."""
    enabled = {resolved.section.id for resolved in sections if resolved.enabled}
    entry = question(question_id) if isinstance(question_id, str) else None
    if entry is None or entry.retired or entry.section not in enabled:
        raise FieldValidationError(
            {"questionId": "Not a question in your questionnaire."}
        )
    return entry


def _filing_role(value: object, roles: tuple[str, ...]) -> str:
    """Which debtor an answer is for. A binding's `roles` bound it (ADR 0023
    § Binding): a client holding one role answers for that role, named or
    not; a client holding both must say which."""
    if value is None:
        if len(roles) == 1:
            return roles[0]
        raise FieldValidationError(
            {"filingRole": "Say which of you this answer is for."}
        )
    if not isinstance(value, str) or value not in CLIENT_ROLES:
        raise FieldValidationError(
            {"filingRole": "Must be one of " + ", ".join(CLIENT_ROLES) + "."}
        )
    if value not in roles:
        raise FieldValidationError(
            {"filingRole": "You can only answer for yourself on this case."}
        )
    return value


def parse_answer(
    body: Mapping[str, object],
    *,
    sections: tuple[ResolvedSection, ...],
    roles: tuple[str, ...],
) -> AnswerRequest:
    """Validate `POST /v1/portal/answers`: `{questionId, filingRole?, value}`.

    `sections` is the firm's resolved config (`questionnaire.resolve`), so a
    switched-off section's questions are not answerable; `roles` is the
    binding's. `filingRole` is recorded on every answer — it is who said it —
    and for a `per_debtor` question it is also the debtor the answer is
    about."""
    entry = _enabled_question(body.get("questionId"), sections)
    role = _filing_role(body.get("filingRole"), roles)
    return AnswerRequest(
        question=entry,
        filing_role=role,
        payload=build_payload(entry, body.get("value")),
    )


def answer_locator(entry: Question, filing_role: str) -> dict[str, object]:
    """The candidate's `locator` for an answer — ADR 0023 § Writes: "the
    section and question answered (the candidate's locator, of a question
    kind)". Snake-case, like a document locator's members."""
    return {
        "kind": LOCATOR_KIND,
        "section_id": entry.section,
        "question_id": entry.id,
        "filing_role": filing_role,
    }


@dataclass(frozen=True)
class AnswerRef:
    """What a candidate's question locator says, read back."""

    question: Question
    filing_role: str


def answer_ref(candidate: Candidate) -> AnswerRef | None:
    """The question a client candidate answers, or None for any other
    candidate (or a locator naming a question the catalogue has never had,
    which nothing writes)."""
    if candidate.origin.channel != CHANNEL:
        return None
    locator = candidate.locator
    if not isinstance(locator, Mapping) or locator.get("kind") != LOCATOR_KIND:
        return None
    question_id = locator.get("question_id")
    filing_role = locator.get("filing_role")
    entry = question(question_id) if isinstance(question_id, str) else None
    if entry is None or not isinstance(filing_role, str):
        return None
    return AnswerRef(question=entry, filing_role=filing_role)


def is_own_answer(candidate: Candidate, *, subject: str) -> bool:
    """A candidate this client wrote — `origin.subject == self`, ADR 0023
    § Read policy. Everything else in the queue is invisible to them."""
    return candidate.origin.channel == CHANNEL and candidate.origin.subject == subject


def create_answer(
    request: AnswerRequest,
    *,
    case_id: str,
    origin: CandidateOrigin,
    existing: tuple[Candidate, ...],
) -> Candidate:
    """The pending candidate for a new answer, or a refusal.

    `existing` is the case's candidates. Two refusals, both 409s because
    the request is well-formed and the state refuses it:

    - a non-repeating question with a PENDING answer from this client for
      this role already has its live answer — change it (PUT) rather than
      stacking a second one in front of the reviewer. Once staff have acted
      on it, a new answer is a new candidate (ADR 0023 § Writes);
    - this client already has MAX_PENDING_ANSWERS waiting.
    """
    if origin.channel != CHANNEL:
        raise ValueError("an answer's origin is the client channel")
    pending = [
        candidate
        for candidate in existing
        if candidate.status == PENDING
        and is_own_answer(candidate, subject=origin.subject)
    ]
    if len(pending) >= MAX_PENDING_ANSWERS:
        raise ConflictError(
            "you have too many answers waiting for review — your firm will "
            "review them before you can add more"
        )
    if not request.question.repeats:
        for candidate in pending:
            ref = answer_ref(candidate)
            if (
                ref is not None
                and ref.question.id == request.question.id
                and ref.filing_role == request.filing_role
            ):
                raise ConflictError(
                    "this question already has an answer waiting for review "
                    "— change that answer instead"
                )
    return create_candidate(
        ProposalDraft(
            entity_type=request.question.target,
            payload=request.payload,
            external_ref=None,
            note=None,
        ),
        case_id=case_id,
        origin=origin,
        locator=answer_locator(request.question, request.filing_role),
    )


def own_answer_or_404(
    candidate: Candidate | None,
    *,
    subject: str,
    sections: tuple[ResolvedSection, ...],
) -> tuple[Candidate, AnswerRef]:
    """The client's own answer in an enabled section, or NotFoundError.

    404 for somebody else's candidate, not 403: unlike the MCP surface's
    colleagues (`candidates.withdraw`), a client cannot see any candidate
    but their own, so "that exists but isn't yours" would be an oracle over
    the case's queue. An answer in a section the firm has since switched
    off is gone from the client's view, and so from here."""
    if candidate is None or not is_own_answer(candidate, subject=subject):
        raise NotFoundError("answer not found")
    ref = answer_ref(candidate)
    enabled = {resolved.section.id for resolved in sections if resolved.enabled}
    if ref is None or ref.question.section not in enabled:
        raise NotFoundError("answer not found")
    return candidate, ref


def revise_answer(candidate: Candidate, ref: AnswerRef, value: object) -> Candidate:
    """The edited copy of a PENDING answer — edit-while-pending (ADR 0023
    § Writes). Once staff have acted on it the row is immutable: a 409, and
    a change is a new answer. The question and the role do not change; an
    appended element keeps its id."""
    if candidate.status != PENDING:
        raise ConflictError(
            f"this answer has already been reviewed (status: {candidate.status})"
        )
    payload = build_payload(
        ref.question,
        value,
        element_id=element_id_of(ref.question, candidate.payload),
    )
    return replace(candidate, payload=payload, updated_at=timestamp())


# ── JSON ────────────────────────────────────────────────────────────


def question_json(entry: Question) -> dict[str, object]:
    """One question as the portal renders it."""
    result: dict[str, object] = {
        "id": entry.id,
        "text": entry.text,
        "repeats": entry.repeats,
        "perDebtor": entry.per_debtor,
        "inputs": [
            {
                "key": answer_input.key,
                "label": answer_input.label,
                "type": answer_input.type,
                "required": answer_input.required,
                **({"help": answer_input.help} if answer_input.help else {}),
            }
            for answer_input in entry.inputs
        ],
    }
    if entry.help is not None:
        result["help"] = entry.help
    return result


def portal_sections_json(config: QuestionnaireConfig | None) -> dict[str, object]:
    """`GET /v1/portal/questionnaire` with this PR's questions: PR 3's
    client view (`questionnaire.portal_questionnaire_json` — the enabled
    sections only, each with the instructions in force) and, on each
    section, its live questions in order. A switched-off section's
    questions are absent with it."""
    body = portal_questionnaire_json(config)
    sections = body["sections"]
    if not isinstance(sections, list):  # pragma: no cover - built just above
        raise TypeError("portal sections must be a list")
    return {
        "sections": [
            {
                **section,
                "questions": [
                    question_json(entry) for entry in questions_for(str(section["id"]))
                ],
            }
            for section in sections
        ]
    }


def answer_json(candidate: Candidate, ref: AnswerRef) -> dict[str, object]:
    """A client's own answer, as the portal reads it back: the value THEY
    gave and where it stands. Not the staff side of it — no corrected
    payload, no reviewer, no resulting record id: those are the firm's."""
    return {
        "id": candidate.id,
        "questionId": ref.question.id,
        "sectionId": ref.question.section,
        "filingRole": ref.filing_role,
        "value": answer_value(ref.question, candidate.payload),
        "status": candidate.status,
        "createdAt": candidate.created_at,
        "updatedAt": candidate.updated_at,
    }


def review_question_json(ref: AnswerRef) -> dict[str, object]:
    """The `question` block the STAFF review queue shows beside a client's
    answer: which section, which question, in the words the client read,
    and which debtor it was answered for."""
    section_titles = {section.id: section.title for section in SECTION_CATALOGUE}
    return {
        "id": ref.question.id,
        "sectionId": ref.question.section,
        "sectionTitle": section_titles.get(ref.question.section, ref.question.section),
        "text": ref.question.text,
        "filingRole": ref.filing_role,
    }


def _check_catalogue() -> None:
    """Import-time guard: the catalogue's own consistency. A typo'd section,
    target or input type is a bug that would otherwise surface as a 500 the
    first time a client answered that question."""
    seen: set[str] = set()
    for entry in CATALOGUE:
        if entry.id in seen:
            raise ValidationError(f"duplicate question id {entry.id!r}")
        seen.add(entry.id)
        if entry.section not in SECTION_IDS:
            raise ValidationError(f"{entry.id}: unknown section {entry.section!r}")
        if not entry.id.startswith(f"{entry.section}."):
            raise ValidationError(f"{entry.id}: id must start with its section")
        if entry.target != DEBTORS and entry.target not in COLLECTIONS:
            raise ValidationError(f"{entry.id}: unknown target {entry.target!r}")
        if entry.placement not in (MERGE, NEST, APPEND):
            raise ValidationError(f"{entry.id}: unknown placement")
        if entry.placement != MERGE and (
            entry.target != DEBTORS or not entry.debtor_field
        ):
            raise ValidationError(f"{entry.id}: nest/append is for debtor fields")
        for answer_input in entry.inputs:
            if answer_input.type not in INPUT_TYPES:
                raise ValidationError(f"{entry.id}: unknown input type")


_check_catalogue()
