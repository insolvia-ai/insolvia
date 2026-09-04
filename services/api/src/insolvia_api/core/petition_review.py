"""AI petition review — a second set of eyes on every packet (issue #97).

The malpractice-reframed feature (business plan §4): AFTER deterministic
assembly, Claude reads the packet's actual content and flags problems the
deterministic gate cannot see — schedules that disagree with each other,
figures that do not add up, creditors the records name but the schedules
miss, SOFA answers that look like undisclosed transfers. Review ONLY: a
finding is advisory, cites the form and line it points at, and never
generates or changes a filed value. The attorney disposes of the list.

This is a PIPELINE WORKER (ADR 0015/0018), the same shape as packet assembly:
the API accepts a `petition_review` job and the worker Lambda runs this. It
is also the repo's first Claude surface, so the decisions it embodies — the
model call runs inside this worker, the Anthropic API is called directly
under its no-training standing, tax identifiers never reach the prompt — are
recorded in ADR 0019 and inherited by extraction (8.7).

What the model is shown, exactly:

- **The packet's own content** — every form's projected field values, keyed
  by the projection's line ids (`4_combined_monthly_income`, …), which is
  the same content the PDFs printed (packet assembly re-runs and must hash
  to the LATEST stored packet, so the review provably describes an assembled
  packet, not a moving target).
- **The confirmed case records** the projections read — creditors, claims,
  assets, income, SOFA entries — which is what lets it notice a creditor
  with no schedule row.
- **Nothing else.** No source documents (that cross-check arrives with
  extraction, 8.7-8.9), no tax identifiers (the stores never hold a full
  SSN/ITIN — `insolvia_core.debtors.parse_debtor` refuses them, claims carry
  last-four only — and `scrub` below re-enforces that as defence in depth).

The findings land on the JOB RESULT, read back through the ordinary job
status endpoint. They are deliberately NOT case data: no store row, no
provenance entry, no candidate — the confirm-before-entry invariant
(docs/reference/case-data-model.md) is about values entering the case, and a
review that only ever produces prose about existing values stays outside it.

The model call sits behind the `ReviewModel` port (core/ports.py): tests and
a laptop without a key use the memory fake; the worker entrypoints compose
the Anthropic adapter when ANTHROPIC_API_KEY is configured, and this worker
fails the job honestly — deterministically, no retry — when it is not.

Everything except the port call and the store reads is pure and runs under
pytest with the memory adapters — the local story ADR 0018 requires.
"""

from __future__ import annotations

import hashlib
import json
import logging
import re
from collections.abc import Callable, Mapping
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any, Final

from insolvia_core.access_log import record_access

from insolvia_api.core.creditor_matrix import MATRIX_FILE_NAME
from insolvia_api.core.form_fill import Check, Option, Text, WidgetStates
from insolvia_api.core.jobs import Job, JobError
from insolvia_api.core.packet_assembly import (
    AssembledPacket,
    CaseData,
    PacketProblem,
    assemble,
    packet_zip,
    problem_json,
    read_case_data,
)

if TYPE_CHECKING:
    from datetime import date

    from insolvia_core.ports import (
        AccessLog,
        CaseEntityStore,
        CaseStore,
        DebtorStore,
    )

    from insolvia_api.core.form_projections import FieldValues
    from insolvia_api.core.ports import PacketStore, ReviewModel

logger = logging.getLogger(__name__)

# The job kind the accept endpoint validates and the worker registries key on.
PETITION_REVIEW_KIND: Final = "petition_review"

# What a finding may be graded. `high` reads "an attorney must look before
# filing"; `low` reads "worth a glance". The model is constrained to these by
# the output schema, and parse_findings re-checks (defence against a schema
# drifting apart from this tuple, not against the API).
SEVERITIES: Final = ("high", "medium", "low")

# The issue's check families, plus `other` so a real defect outside them is
# not forced into a wrong bucket.
CATEGORIES: Final = (
    "consistency",
    "arithmetic",
    "missing_creditor",
    "exemption",
    "transfer",
    "incomplete",
    "other",
)

# Caps on what a model answer may put on the job row: the result is stored in
# the case partition's job item, and an unbounded list or message would let a
# bad generation bloat a stored record. Fifty findings is far beyond a useful
# review; a message is one sentence.
MAX_FINDINGS: Final = 50
MAX_MESSAGE_CHARS: Final = 500

# What the model is, to the preparer: instructions first, the petition after.
# Everything here is why "a clean case produces near-zero noise" (the issue's
# done-when) is a prompt property, not a hope: the empty list is named as the
# expected common outcome, and the deterministic gate's territory is fenced
# off so the model does not re-report what code already enforces.
REVIEW_SYSTEM_PROMPT: Final = """\
You are reviewing a completed U.S. Chapter 7 individual bankruptcy petition
packet before filing: form B101, the B106 schedules and summary, the B107
statement of financial affairs, and the creditor mailing matrix. You are the
second set of eyes AFTER deterministic assembly — the packet is structurally
complete and every form filled from confirmed case data. Your job is to find
substantive problems that rules-based checks cannot see.

Look for, in rough order of importance:
- Internal contradictions: schedules disagreeing with each other or with the
  statement of financial affairs (income on 106I vs employment answers vs
  SOFA income; property on 106A/B vs secured claims on 106D; expenses on
  106J inconsistent with dependents or household answers).
- Arithmetic that does not hold: totals, subtotals and derived lines that do
  not follow from their inputs.
- Missing creditors: a creditor in the case records with no claim on any
  schedule, a claim whose creditor is absent from the mailing matrix, or
  debts the other answers imply (a mortgage on scheduled real property, a
  lender behind a vehicle) that no schedule lists.
- Exemption red flags: exemptions claimed against property that does not
  appear, amounts that look inconsistent with the property's stated value.
- Undisclosed-transfer signals: SOFA answers (payments, transfers, closed
  accounts, property held for another) that look incomplete or inconsistent
  with the schedules.
- Answers that look missing rather than genuinely empty: a filled section
  whose content implies a companion answer that is blank.

Rules for every finding:
- Advisory only. Never propose a value to enter; describe the problem and
  where it shows.
- Cite the specific form and the line or field key it points at, exactly as
  they appear in the packet content you are given.
- One finding per distinct problem; do not restate one defect on every form
  it touches — cite the primary location and mention the others in the
  message.
- Report only defects you are confident an attorney preparing this filing
  would want to see. Do not report style, formatting, absent optional facts,
  or anything the deterministic gate already guarantees (completeness of the
  form set, dangling record references, matrix generation).
- A consistent, complete petition yields an EMPTY findings list. That is the
  expected outcome for most packets, not a failure to do your job.
"""

# The structured-output contract: the model must answer this shape and
# nothing else. Kept beside the prompt (its other half) and imported by the
# Anthropic adapter — one owner, so the adapter and parse_findings below
# cannot drift apart.
REVIEW_OUTPUT_SCHEMA: Final[dict[str, Any]] = {
    "type": "object",
    "properties": {
        "findings": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "severity": {"type": "string", "enum": list(SEVERITIES)},
                    "category": {"type": "string", "enum": list(CATEGORIES)},
                    "form": {
                        "type": "string",
                        "description": "The form the finding points at, as its"
                        " series id in the packet content (e.g. form/b106i),"
                        " or 'packet' for a cross-form finding.",
                    },
                    "line": {
                        "type": "string",
                        "description": "The line or field key on that form"
                        " (e.g. 4_combined_monthly_income), or '' when the"
                        " finding has no single line.",
                    },
                    "message": {
                        "type": "string",
                        "description": "One or two sentences a petition"
                        " preparer can act on.",
                    },
                },
                "required": ["severity", "category", "form", "line", "message"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["findings"],
    "additionalProperties": False,
}


@dataclass(frozen=True)
class ReviewFinding:
    """One advisory finding, already validated against the vocabulary above.
    `form` is a series id ("form/b106i") or "packet" for cross-form findings;
    `line` is that form's projection line key, empty when none applies."""

    severity: str
    category: str
    form: str
    line: str
    message: str


def finding_json(finding: ReviewFinding) -> dict[str, object]:
    """The wire shape inside the job result — every key always present, so
    the client renders without per-key existence checks."""
    return {
        "severity": finding.severity,
        "category": finding.category,
        "form": finding.form,
        "line": finding.line,
        "message": finding.message,
    }


@dataclass(frozen=True)
class ReviewModelResult:
    """What the ReviewModel port answers: which model actually ran (recorded
    on the report, so "what reviewed this packet" stays answerable) and the
    raw structured output, still to be validated by parse_findings."""

    model: str
    raw: Mapping[str, Any]


def parse_findings(raw: Mapping[str, Any]) -> tuple[ReviewFinding, ...]:
    """Validate the model's structured output into findings.

    Structured outputs mean a well-behaved API answer already matches
    REVIEW_OUTPUT_SCHEMA; this is the boundary check that keeps a drifted or
    misbehaving adapter from writing junk onto a job row. Anything malformed
    raises ValueError — run_job treats that as infrastructure, which is
    right: a retry gets a fresh generation.
    """
    findings_raw = raw.get("findings")
    if not isinstance(findings_raw, list):
        raise ValueError("review model output has no findings list")
    findings: list[ReviewFinding] = []
    for entry in findings_raw[:MAX_FINDINGS]:
        if not isinstance(entry, Mapping):
            raise ValueError("review model finding is not an object")
        severity = entry.get("severity")
        category = entry.get("category")
        form = entry.get("form")
        line = entry.get("line")
        message = entry.get("message")
        if (
            severity not in SEVERITIES
            or category not in CATEGORIES
            or not isinstance(form, str)
            or not isinstance(line, str)
            or not isinstance(message, str)
            or not message.strip()
        ):
            raise ValueError("review model finding is malformed")
        findings.append(
            ReviewFinding(
                severity=severity,
                category=category,
                form=form[:100],
                line=line[:100],
                message=message[:MAX_MESSAGE_CHARS],
            )
        )
    return tuple(findings)


# ── What the model is shown ─────────────────────────────────────

# SSN/ITIN shape (000-00-0000). The stores cannot hold one — parse_debtor
# refuses tax_id outright and claims carry account LAST FOUR only — so this
# never fires on data our own validation admitted. It exists as defence in
# depth for the GLBA posture ADR 0019 records: if a full tax id ever reaches
# a stored free-text field, it still does not leave for the model API.
_TAX_ID_PATTERN: Final = re.compile(r"\b\d{3}-\d{2}-\d{4}\b")

# Key names that would carry a tax identifier if the data model ever grows
# one; dropped wholesale rather than pattern-matched.
_TAX_ID_KEYS: Final = frozenset({"tax_id", "ssn", "itin", "social_security_number"})


def scrub(value: Any) -> Any:
    """Recursively remove tax identifiers from a JSON-shaped structure:
    keys named for one are dropped, values shaped like one are replaced."""
    if isinstance(value, Mapping):
        return {
            key: scrub(item) for key, item in value.items() if key not in _TAX_ID_KEYS
        }
    if isinstance(value, (list, tuple)):
        return [scrub(item) for item in value]
    if isinstance(value, str):
        return _TAX_ID_PATTERN.sub("[tax id removed]", value)
    return value


def _rendered_fill(fill: object) -> object:
    """A projection fill as the review document spells it — the printed
    value, not the fill dataclass."""
    if isinstance(fill, Text):
        return fill.value
    if isinstance(fill, Check):
        return fill.on
    if isinstance(fill, Option):
        return fill.export
    if isinstance(fill, WidgetStates):  # the broken-button-group escape hatch
        return list(fill.states) or list(fill.indexes)
    return str(fill)


def _rendered_form(values: FieldValues) -> dict[str, object]:
    rendered: dict[str, object] = {}
    for field_id, fill in values.items():
        if isinstance(fill, Mapping):
            rendered[field_id] = {
                pdf_name: _rendered_fill(inner) for pdf_name, inner in fill.items()
            }
        else:
            rendered[field_id] = _rendered_fill(fill)
    return rendered


def _prune(value: Any) -> Any:
    """Drop None values and empty containers so the document carries facts,
    not a lattice of blanks — absent stays absent, exactly as the forms
    leave blank boxes."""
    if isinstance(value, Mapping):
        pruned = {
            key: cleaned
            for key, cleaned in ((k, _prune(v)) for k, v in value.items())
            if cleaned is not None and cleaned != {} and cleaned != []
        }
        return pruned
    if isinstance(value, (list, tuple)):
        return [
            cleaned
            for cleaned in (_prune(item) for item in value)
            if cleaned is not None and cleaned != {} and cleaned != []
        ]
    return value


def _record(body: object) -> dict[str, object]:
    cleaned = _prune(asdict(body))  # type: ignore[call-overload]
    assert isinstance(cleaned, dict)
    return cleaned


def review_document(data: CaseData, packet: AssembledPacket) -> str:
    """The one user message the model reads: the packet's rendered content
    and the confirmed records behind it, as deterministic JSON (sorted keys,
    stable record order — the stores' listing order), scrubbed of tax
    identifiers last so nothing added later can dodge it.

    Deliberately JSON rather than prose: the line keys ARE the citation
    vocabulary the findings must answer with, and a serialization the tests
    can pin keeps "what leaves for the model API" reviewable.
    """
    matrix_text = next(
        content for name, content in packet.parts if name == MATRIX_FILE_NAME
    ).decode("ascii")

    debtors = []
    for debtor in data.debtors:
        record = _record(debtor)
        # Identity and audit plumbing, not petition content.
        for key in ("id", "case_id", "created_at", "updated_at", "provenance"):
            record.pop(key, None)
        debtors.append({"filing_role": debtor.filing_role, **record})

    def bodies(entities: tuple[Any, ...]) -> list[dict[str, object]]:
        return [_record(entity.body) for entity in entities]

    document = {
        "case": {"chapter": data.case.chapter, "district": data.case.district},
        "forms": [
            {
                "form": series_id,
                "revision": packet.form_revisions[series_id],
                "fields": _rendered_form(values),
            }
            for series_id, values in packet.projections.items()
        ],
        "creditor_matrix": matrix_text,
        "confirmed_records": {
            "debtors": debtors,
            "petition": bodies(data.petitions),
            "employments": bodies(data.employments),
            "income_summaries": bodies(data.income_summaries),
            "assets": bodies(data.assets),
            "exemptions": bodies(data.exemptions),
            "creditors": bodies(data.creditors),
            "claims": bodies(data.claims),
            "contract_leases": bodies(data.contract_leases),
            "codebtors": bodies(data.codebtors),
            "households": bodies(data.households),
            "expenses": bodies(data.expenses),
            "dependents": bodies(data.dependents),
            "sofa_entries": bodies(data.sofa_entries),
        },
    }
    return json.dumps(scrub(document), sort_keys=True, default=str)


# ── The worker ──────────────────────────────────────────────────


@dataclass(frozen=True)
class PetitionReviewDeps:
    """What the worker composes — the entrypoints build this from the AWS
    adapters and the Anthropic adapter, tests from the memory ones. `model`
    is None in an environment whose key is not configured, and the worker
    turns that into an honest deterministic failure rather than a retry
    loop."""

    case_store: CaseStore
    debtor_store: DebtorStore
    entity_store: CaseEntityStore
    packet_store: PacketStore
    access_log: AccessLog
    model: ReviewModel | None


def _blocked(message: str, *, source: str = "packets") -> dict[str, Any]:
    """A successful job whose answer is 'not reviewable yet, and here is
    why' — packet assembly's `blocked` contract, same wire shape, so the
    client renders both with one code path."""
    return {
        "outcome": "blocked",
        "problems": [
            problem_json(
                PacketProblem(source=source, item_id=None, field="", message=message)
            )
        ],
    }


def run_petition_review(
    job: Job, deps: PetitionReviewDeps, *, today: date | None = None
) -> dict[str, Any]:
    """The worker: Job in, JSON-shaped result out (core/jobs.py's contract).

    Result shapes, all SUCCEEDED jobs:

        {"outcome": "blocked", "problems": [...]}   nothing reviewable: no
                                                    packet yet, the case has
                                                    changed since the last
                                                    one, or the gate refuses
        {"outcome": "reviewed", "report": {...}}    the advisory findings

    The review describes an ASSEMBLED PACKET, never a moving target: the
    deterministic assembly re-runs here and its bytes must hash to the
    newest stored packet's sha256. A mismatch means the case was edited
    after assembly — reviewing the edited data would produce findings about
    a packet nobody holds, so the honest answer is "re-assemble first".

    JobError (-> job `failed`) is reserved for a missing configuration or a
    vanished case; an Anthropic API failure raises through and takes the
    run_job retry path, because a retry genuinely may succeed.
    """
    if deps.model is None:
        raise JobError(
            "AI review is not configured in this environment yet.",
            category="not_configured",
        )
    case = deps.case_store.read_for_worker(job.case_id)
    if case is None:
        raise JobError(
            "The case this job was accepted against no longer exists.",
            category="case_not_found",
        )
    # The same pair rule packet assembly states: a whole-case read is a
    # case-data read, recorded against the preparer whose accept caused it.
    deps.access_log.record(
        record_access(
            case_id=case.id, principal=job.created_by, action="petition.review"
        )
    )

    data = read_case_data(
        case, debtor_store=deps.debtor_store, entity_store=deps.entity_store
    )
    as_of = today if today is not None else datetime.now(UTC).date()
    outcome = assemble(data, as_of=as_of)
    if not isinstance(outcome, AssembledPacket):
        logger.info(
            # GLBA: the count alone — a problem message names case facts.
            "petition review blocked by the gate",
            extra={"case_id": case.id, "job_id": job.id, "problems": len(outcome)},
        )
        return {
            "outcome": "blocked",
            "problems": [problem_json(problem) for problem in outcome],
        }

    packets = deps.packet_store.list_for_case(case.id)  # newest first
    if not packets:
        return _blocked(
            "No packet has been assembled yet — assemble the filing packet,"
            " then run the review."
        )
    latest = packets[0]
    sha256 = hashlib.sha256(packet_zip(outcome.parts)).hexdigest()
    if sha256 != latest.sha256:
        return _blocked(
            "The case has changed since its packet was assembled — assemble"
            " the packet again, then run the review."
        )

    result = deps.model.review(review_document(data, outcome))
    findings = parse_findings(result.raw)
    logger.info(
        # Counts and identifiers only; a finding's message names case facts.
        "petition reviewed",
        extra={
            "case_id": case.id,
            "job_id": job.id,
            "packet_id": latest.id,
            "model": result.model,
            "findings": len(findings),
        },
    )
    return {
        "outcome": "reviewed",
        "report": {
            "packetId": latest.id,
            "packetSha256": latest.sha256,
            "model": result.model,
            "findings": [finding_json(finding) for finding in findings],
        },
    }


def petition_review_worker(deps: PetitionReviewDeps) -> Callable[[Job], dict[str, Any]]:
    """The registry entry: closes the dependencies over the plain
    Job-in/result-out callable the worker registries expect."""

    def worker(job: Job) -> dict[str, Any]:
        return run_petition_review(job, deps)

    return worker
