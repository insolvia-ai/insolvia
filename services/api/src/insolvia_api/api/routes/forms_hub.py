"""The forms hub (issue 13.2 / #343): `GET /v1/cases/<id>/forms` lists every
form the case's chapter and pin require, with per-form status; `GET
/v1/cases/<id>/forms/<form>/preview` renders exactly one of them.

**Preview runs IN THIS REQUEST, not as a pipeline job.** ADR 0015 keeps
minutes-long work off the lambdalith and behind `api/routes/jobs.py`'s
accept/poll pair — packet assembly renders thirteen PDFs and is exactly that.
This route renders ONE, and `core/form_fill.py`'s own module docstring says
why that stays synchronous: "packet assembly (9.6) calls it once per form
from a worker; a single-form render stays fast enough for a synchronous
caller too." A single `fill_form` call is a few hundred milliseconds of pure
PDF manipulation, nowhere near the API Gateway's 30-second ceiling, so
wrapping it in a job would trade a sub-second response for a poll loop with
no reason for one.

**Same access and permission path as packets** (`api/routes/packets.py`):
`VIEW_ONLY` on `CASES`, because rendering a form is a projection of records
the caller can already read — it writes nothing to the case. The rendered
bytes ARE written, to the same bucket packets use, under their own prefix
(`core/forms_hub.form_preview_object_key`) — that write is this service's
own, the same shape `packet_assembly`'s worker write is, just from the API
role instead of the worker's (the bucket module already grants the API role
`s3:PutObject` across the whole bucket for exactly this reason: presigning an
upload needs it).

**200 either way, the creditor-matrix route's contract**: the outcome is
either a short-lived download URL (`problems` empty) or every reason the form
could not render (`problems` non-empty, no URL) — never both, and never a
partial form. A 4xx would turn the problem list into an error body, and the
list is precisely what the client renders next to each blocked row.
"""

from __future__ import annotations

import logging
from datetime import UTC, datetime

from flask import Blueprint, jsonify
from flask.typing import ResponseReturnValue
from insolvia_core.access_log import record_access
from insolvia_core.documents import expiry_timestamp
from insolvia_core.errors import NotFoundError
from insolvia_core.firms import CASES, VIEW_ONLY
from insolvia_core.ports import (
    AccessLog,
    CaseEntityStore,
    CaseStore,
    DebtorStore,
    DocumentBlobStore,
)

from insolvia_api.api.auth import current_accessor, require_auth, requires
from insolvia_api.api.dependencies import dependencies
from insolvia_api.core.forms_hub import (
    FormSummary,
    form_metric_json,
    form_preview_object_key,
    forms_hub,
    new_preview_id,
    render_form_preview,
)
from insolvia_api.core.packet_assembly import (
    PacketProblem,
    packet_form_series,
    problem_json,
    read_case_data,
)

logger = logging.getLogger(__name__)

blueprint = Blueprint("forms_hub", __name__)

# The document/packet download route's TTL: the app asks for a URL at the
# moment the preparer clicks "preview" and opens it immediately.
DOWNLOAD_URL_TTL_SECONDS = 5 * 60

# The PDF content type every rendered preview carries.
PREVIEW_CONTENT_TYPE = "application/pdf"


def _stores() -> tuple[
    CaseStore, DebtorStore, CaseEntityStore, DocumentBlobStore, AccessLog
]:
    deps = dependencies()
    if (
        deps.case_store is None
        or deps.debtor_store is None
        or deps.case_entity_store is None
        or deps.document_blobs is None
        or deps.access_log is None
    ):
        raise RuntimeError(
            "case store, debtor store, entity store, blob store and access log"
            " are not composed"
        )
    return (
        deps.case_store,
        deps.debtor_store,
        deps.case_entity_store,
        deps.document_blobs,
        deps.access_log,
    )


def _form_summary_json(summary: FormSummary) -> dict[str, object]:
    body: dict[str, object] = {
        "series": summary.series,
        "form": summary.form,
        "title": summary.title,
        "officialNumber": summary.official_number,
        "problems": [problem_json(p) for p in summary.problems],
    }
    if summary.metric is not None:
        body["metric"] = form_metric_json(summary.metric)
    return body


@blueprint.get("/v1/cases/<case_id>/forms")
@require_auth
@requires(CASES, VIEW_ONLY)
def list_case_forms_route(case_id: str) -> ResponseReturnValue:
    """Every form this case's chapter and pin require, each with its item
    count or dollar total (where the issue defines one) and its own slice of
    the completeness gate's problem list.

    Built entirely from `packet_assembly`'s own functions — `packet_form_series`
    for which forms, `completeness_problems` for what is wrong — grouped per
    form rather than flattened, never a second definition of either.
    """
    case_store, debtor_store, entity_store, _, access_log = _stores()
    accessor = current_accessor()

    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="case.read",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")

    data = read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    summaries = forms_hub(data, as_of=datetime.now(UTC).date())
    return jsonify({"forms": [_form_summary_json(s) for s in summaries]}), 200


@blueprint.get("/v1/cases/<case_id>/forms/<form>/preview")
@require_auth
@requires(CASES, VIEW_ONLY)
def form_preview_route(case_id: str, form: str) -> ResponseReturnValue:
    """Render exactly one form and mint a short-lived URL to the PDF — or
    return the reasons it cannot render yet. Never a partial form.

    `form` is the short form key (`b101`, `b106ab`, …, `release.form`), not
    the `form/<x>` series id — the URL segment stays free of the slash that
    id carries. A form this case does not file (an unknown key, or one
    `packet_form_series` currently skips — B106J-2 with no separate
    household, B122A-2 below the median) answers 404: from a preparer's
    seat, that form simply is not part of this case, the same reading
    `assemble` gives it when it silently drops the series from the set.
    """
    case_store, debtor_store, entity_store, blobs, access_log = _stores()
    accessor = current_accessor()

    case = case_store.get(case_id, accessor=accessor)
    access_log.record(
        record_access(
            case_id=case_id,
            principal=accessor.subject,
            action="form_preview.render",
            outcome="allowed" if case is not None else "denied",
        )
    )
    if case is None:
        raise NotFoundError("case not found")

    data = read_case_data(case, debtor_store=debtor_store, entity_store=entity_store)
    series_id = f"form/{form}"
    if series_id not in packet_form_series(data):
        raise NotFoundError("form not found")

    today = datetime.now(UTC).date()
    outcome = render_form_preview(data, series_id, as_of=today)

    if isinstance(outcome, tuple):
        problems: tuple[PacketProblem, ...] = outcome
        logger.info(
            # GLBA: the case, the form and whether it rendered — never a
            # problem message, which can name case facts.
            "form preview blocked",
            extra={"case_id": case.id, "series": series_id, "problems": len(problems)},
        )
        return jsonify({"problems": [problem_json(p) for p in problems]}), 200

    content = outcome
    storage_ref = form_preview_object_key(case.id, new_preview_id())
    blobs.put_bytes(storage_ref, content=content, content_type=PREVIEW_CONTENT_TYPE)
    logger.info(
        "form preview rendered",
        extra={"case_id": case.id, "series": series_id},
    )
    return (
        jsonify(
            {
                "url": blobs.download_url(
                    storage_ref, expires_in=DOWNLOAD_URL_TTL_SECONDS
                ),
                "method": "GET",
                "expiresAt": expiry_timestamp(DOWNLOAD_URL_TTL_SECONDS),
                "problems": [],
            }
        ),
        200,
    )
