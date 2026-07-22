import json
import logging

from insolvia_api.core.logging import JsonFormatter, configure_logging


def format_record(**extra):
    record = logging.LogRecord(
        name="insolvia_api.test",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="hello %s",
        args=("world",),
        exc_info=None,
    )
    for key, value in extra.items():
        setattr(record, key, value)
    return JsonFormatter().format(record)


def test_formatter_emits_one_json_object():
    payload = json.loads(format_record())

    assert payload["level"] == "INFO"
    assert payload["logger"] == "insolvia_api.test"
    assert payload["message"] == "hello world"
    assert payload["timestamp"].endswith("Z")


def test_formatter_includes_extra_fields():
    payload = json.loads(
        format_record(method="POST", path="/v1/waitlist", status=201, duration_ms=1.2)
    )

    assert payload["method"] == "POST"
    assert payload["path"] == "/v1/waitlist"
    assert payload["status"] == 201
    assert payload["duration_ms"] == 1.2


def test_formatter_serializes_non_json_values_as_strings():
    payload = json.loads(format_record(oddity={1, 2}))

    assert isinstance(payload["oddity"], str)


def test_configure_logging_reformats_existing_handlers():
    # The Lambda runtime pre-installs a root handler; configure_logging must
    # reformat it rather than stacking a second one (one line per record).
    root = logging.getLogger()
    original_handlers = root.handlers[:]
    original_level = root.level
    handler = logging.StreamHandler()
    try:
        root.handlers = [handler]
        configure_logging()
        assert root.handlers == [handler]
        assert isinstance(handler.formatter, JsonFormatter)
    finally:
        root.handlers = original_handlers
        root.setLevel(original_level)


def test_each_request_logs_exactly_one_json_line_with_no_pii(client, caplog):
    body = {
        "name": "Ada Lovelace",
        "firm": "Lovelace & Byron LLP",
        "email": "ada@lovelace-law.example",
        "message": "please keep this out of the logs",
    }

    with caplog.at_level(logging.INFO):
        response = client.post("/v1/waitlist", json=body)
    assert response.status_code == 201

    request_lines = [
        record for record in caplog.records if record.name == "insolvia_api.request"
    ]
    assert len(request_lines) == 1
    line = json.loads(JsonFormatter().format(request_lines[0]))
    assert line["method"] == "POST"
    assert line["path"] == "/v1/waitlist"
    assert line["status"] == 201
    assert isinstance(line["duration_ms"], float)

    # GLBA: no submitted field value may appear in ANY log line; the
    # generated id is the only waitlist datum allowed.
    submission_id = response.get_json()["id"]
    all_output = " ".join(JsonFormatter().format(record) for record in caplog.records)
    for value in body.values():
        assert value not in all_output
    assert submission_id in all_output


def test_health_requests_log_one_line_too(client, caplog):
    with caplog.at_level(logging.INFO):
        client.get("/health")

    request_lines = [
        record for record in caplog.records if record.name == "insolvia_api.request"
    ]
    assert len(request_lines) == 1
    assert request_lines[0].path == "/health"
    assert request_lines[0].status == 200
