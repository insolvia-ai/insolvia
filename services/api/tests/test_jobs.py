"""The job model and the dispatch loop (ADR 0018).

Two things matter most here. The MESSAGE CONTRACT test pins the exact wire
shape both sides of the queue meet at — the seam that cannot run under pytest
end to end — so the enqueue adapter and the worker entrypoint cannot drift
apart silently. And the run_job tests exercise the at-least-once semantics
(redelivery, races, the two failure shapes) with plain callables and the
in-memory store: the local story the ADR commits to.
"""

from __future__ import annotations

import json
from dataclasses import replace

import pytest
from insolvia_api.adapters.memory.job_store import MemoryJobStore
from insolvia_api.core.jobs import (
    KINDS,
    MESSAGE_VERSION,
    WORKERS,
    Job,
    JobError,
    JobFailure,
    JobMessage,
    complete,
    fail,
    find_active,
    handle_sqs_event,
    job_from_item,
    job_item,
    job_json,
    job_message,
    new_job,
    parse_job_acceptance,
    parse_job_message,
    run_echo,
    run_job,
    start_attempt,
)
from insolvia_core.errors import FieldValidationError, ValidationError

CASE = "00000000-0000-4000-8000-00000000ca5e"
ALICE = "00000000-0000-4000-8000-00000000a11c"


def make_job(**overrides) -> Job:
    job = new_job("echo", case_id=CASE, created_by=ALICE)
    return replace(job, **overrides) if overrides else job


# ── Acceptance validation ───────────────────────────────────────


def test_a_registered_kind_is_accepted() -> None:
    assert parse_job_acceptance({"kind": "echo"}) == "echo"


@pytest.mark.parametrize(
    "payload",
    [
        {},
        {"kind": "assemble_packet_from_the_future"},
        {"kind": 7},
        {"kind": None},
    ],
)
def test_anything_but_a_registered_kind_is_rejected(payload) -> None:
    with pytest.raises(FieldValidationError):
        parse_job_acceptance(payload)


def test_kinds_are_derived_from_the_worker_registry() -> None:
    # The accept endpoint's validation and the worker's dispatch must not be
    # able to disagree about what exists.
    assert tuple(WORKERS) == KINDS


# ── Identity and transitions ────────────────────────────────────


def test_a_new_job_is_queued_with_no_attempts() -> None:
    job = new_job("echo", case_id=CASE, created_by=ALICE)
    assert job.status == "queued"
    assert job.attempts == 0
    assert job.failure is None
    assert job.result is None
    assert job.created_at == job.updated_at


def test_start_attempt_counts_and_runs() -> None:
    job = make_job()
    started = start_attempt(job)
    assert started.status == "running"
    assert started.attempts == 1


@pytest.mark.parametrize("status", ["queued", "running", "failed"])
def test_every_non_terminal_status_can_start_an_attempt(status) -> None:
    # `running` because a crashed attempt leaves the record saying running
    # while SQS redelivers; `failed` because an infrastructure failure's
    # redelivery may still get it right.
    assert start_attempt(make_job(status=status)).status == "running"


def test_a_succeeded_job_cannot_be_rerun() -> None:
    with pytest.raises(ValidationError):
        start_attempt(make_job(status="succeeded"))


def test_complete_clears_any_earlier_failure() -> None:
    job = make_job(status="running", failure=JobFailure("internal", "boom"))
    done = complete(job, {"echo": job.id})
    assert done.status == "succeeded"
    assert done.failure is None
    assert done.result == {"echo": job.id}


def test_find_active_matches_kind_and_liveness() -> None:
    queued = make_job()
    assert find_active((queued,), "echo") is queued
    assert find_active((make_job(status="failed"),), "echo") is None
    assert find_active((make_job(status="succeeded"),), "echo") is None
    assert find_active((queued,), "another-kind") is None


# ── Stored item shape ───────────────────────────────────────────


def test_job_item_round_trips() -> None:
    job = complete(start_attempt(make_job()), {"echo": "ok"})
    assert job_from_item(job_item(job)) == job


def test_job_item_round_trips_a_failure() -> None:
    job = fail(start_attempt(make_job()), JobFailure("internal", "retrying"))
    assert job_from_item(job_item(job)) == job


def test_job_item_keys_into_the_case_partition() -> None:
    job = make_job()
    item = job_item(job)
    assert item["PK"] == f"CASE#{CASE}"
    assert item["SK"] == f"JOB#{job.id}"
    # Absent, not null, so the common row stays small.
    assert "failure" not in item
    assert "result" not in item


def test_a_malformed_stored_item_fails_loudly() -> None:
    with pytest.raises(ValidationError):
        job_from_item({"id": "only-an-id"})


def test_job_json_carries_failure_only_when_failed() -> None:
    job = make_job()
    assert "failure" not in job_json(job)
    assert "result" not in job_json(job)
    failed = fail(job, JobFailure("job_error", "The petition has no debtor."))
    assert job_json(failed)["failure"] == {
        "category": "job_error",
        "message": "The petition has no debtor.",
    }


def test_job_json_omits_the_case_id() -> None:
    # The client named the case in the URL; echoing the id back adds nothing.
    assert "caseId" not in job_json(make_job())


# ── The queue contract (the pinned seam) ────────────────────────


def test_the_wire_message_is_exactly_ids_and_a_version() -> None:
    # THE contract pin. Both sides of the queue build from these functions,
    # so this literal is what actually crosses SQS — change it only with a
    # consumer that understands both versions, and never add case data.
    job = make_job()
    assert job_message(job) == {
        "version": 1,
        "jobId": job.id,
        "caseId": CASE,
        "kind": "echo",
    }
    assert MESSAGE_VERSION == 1


def test_the_message_round_trips() -> None:
    job = make_job()
    message = parse_job_message(job_message(job))
    assert message == JobMessage(job_id=job.id, case_id=CASE, kind="echo")


@pytest.mark.parametrize(
    "payload",
    [
        None,
        "a string",
        {},
        {"version": 2, "jobId": "j", "caseId": "c", "kind": "echo"},
        {"version": 1, "jobId": 7, "caseId": "c", "kind": "echo"},
        {"version": 1, "jobId": "j", "kind": "echo"},
    ],
)
def test_anything_this_service_did_not_mint_is_rejected(payload) -> None:
    with pytest.raises(ValidationError):
        parse_job_message(payload)


# ── The dispatch loop ───────────────────────────────────────────


@pytest.fixture
def store() -> MemoryJobStore:
    return MemoryJobStore()


def accepted(store: MemoryJobStore) -> Job:
    job = new_job("echo", case_id=CASE, created_by=ALICE)
    store.create(job)
    return job


def message_for(job: Job) -> JobMessage:
    return parse_job_message(job_message(job))


def test_a_delivered_job_runs_to_succeeded(store) -> None:
    job = accepted(store)
    run_job(message_for(job), store=store, workers=WORKERS)
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "succeeded"
    assert stored.attempts == 1
    assert stored.result == {"echo": job.id}


def test_a_redelivery_of_a_finished_job_is_a_no_op(store) -> None:
    job = accepted(store)
    run_job(message_for(job), store=store, workers=WORKERS)
    first = store.get(CASE, job.id)
    run_job(message_for(job), store=store, workers=WORKERS)
    assert store.get(CASE, job.id) == first


def test_a_message_with_no_record_is_dropped_not_raised(store) -> None:
    # Retrying a message that can never resolve only poisons the queue.
    stray = new_job("echo", case_id=CASE, created_by=ALICE)
    run_job(message_for(stray), store=store, workers=WORKERS)
    assert store.get(CASE, stray.id) is None


def test_a_deterministic_failure_is_terminal_and_preparer_readable(store) -> None:
    job = accepted(store)

    def rejecting(running: Job) -> dict[str, object]:
        raise JobError("The case has no debtor yet.", category="case_incomplete")

    # Swallowed, not raised: running again would produce the same answer.
    run_job(message_for(job), store=store, workers={"echo": rejecting})
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "failed"
    assert stored.failure == JobFailure(
        "case_incomplete", "The case has no debtor yet."
    )


def test_an_infrastructure_failure_marks_failed_and_reraises(store) -> None:
    job = accepted(store)

    def crashing(running: Job) -> dict[str, object]:
        raise ConnectionError("socket closed")

    with pytest.raises(ConnectionError):
        run_job(message_for(job), store=store, workers={"echo": crashing})
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "failed"
    assert stored.failure is not None
    assert stored.failure.category == "internal"
    # GLBA: the exception's text stays in the log, never on the record.
    assert "socket" not in stored.failure.message


def test_a_retry_after_an_infrastructure_failure_can_still_succeed(store) -> None:
    job = accepted(store)

    def crashing(running: Job) -> dict[str, object]:
        raise ConnectionError("socket closed")

    with pytest.raises(ConnectionError):
        run_job(message_for(job), store=store, workers={"echo": crashing})
    run_job(message_for(job), store=store, workers=WORKERS)
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "succeeded"
    assert stored.attempts == 2


def test_an_unknown_kind_fails_the_job_and_raises_for_redelivery(store) -> None:
    # The deploy-window case: the API accepted a kind the running worker
    # image does not carry yet.
    job = accepted(store)
    with pytest.raises(ValidationError):
        run_job(message_for(job), store=store, workers={})
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "failed"
    assert stored.failure is not None
    assert stored.failure.category == "unknown_kind"


def test_losing_the_start_race_runs_nothing(store) -> None:
    job = accepted(store)
    calls: list[str] = []

    class RacingStore(MemoryJobStore):
        def update(self, updated: Job, *, expected_status: str) -> Job | None:
            if updated.status == "running" and expected_status == "queued":
                return None  # the other delivery got there first
            return super().update(updated, expected_status=expected_status)

    racing = RacingStore()
    racing.create(job)
    run_job(
        message_for(job),
        store=racing,
        workers={"echo": lambda running: calls.append(running.id) or {}},
    )
    assert calls == []
    stored = racing.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "queued"


def test_the_sqs_event_shape_reaches_run_job(store) -> None:
    # The exact record shape the Lambda event source mapping delivers, and
    # the one the local poller reconstructs — one consume path.
    job = accepted(store)
    event = {"Records": [{"body": json.dumps(job_message(job))}]}
    handle_sqs_event(event, store=store, workers=WORKERS)
    stored = store.get(CASE, job.id)
    assert stored is not None
    assert stored.status == "succeeded"


def test_an_unparseable_body_raises_toward_the_dlq(store) -> None:
    # It will never parse, so retries walking it to the DLQ (visible, under
    # an alarm) beats a silent drop.
    with pytest.raises(ValidationError):
        handle_sqs_event(
            {"Records": [{"body": '{"version": 99}'}]}, store=store, workers=WORKERS
        )


def test_the_echo_worker_reads_no_case_data(store) -> None:
    job = make_job()
    assert run_echo(job) == {"echo": job.id}
