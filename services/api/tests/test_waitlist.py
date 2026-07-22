import uuid

import pytest

from insolvia_api.core.errors import FieldValidationError
from insolvia_api.core.waitlist import (
    WaitlistRecord,
    WaitlistSubmission,
    create_waitlist_record,
    parse_waitlist_submission,
    record_item,
)

VALID_BODY = {
    "name": "Ada Lovelace",
    "firm": "Lovelace & Byron LLP",
    "email": "ada@lovelace-law.example",
    "currentSoftware": "Best Case",
    "message": "Two attorneys, chapter 7 heavy.",
    "host": "www.insolvia.ai",
}


# --- core validation -------------------------------------------------------


def test_valid_submission_is_trimmed_and_parsed():
    padded = {key: f"  {value}  " for key, value in VALID_BODY.items()}

    submission = parse_waitlist_submission(padded)

    assert submission == WaitlistSubmission(
        name="Ada Lovelace",
        firm="Lovelace & Byron LLP",
        email="ada@lovelace-law.example",
        current_software="Best Case",
        message="Two attorneys, chapter 7 heavy.",
        host="www.insolvia.ai",
    )


def test_optional_fields_default_to_empty():
    submission = parse_waitlist_submission(
        {"name": "Ada", "firm": "Lovelace LLP", "email": "ada@example.com"}
    )

    assert submission.current_software == ""
    assert submission.message == ""
    assert submission.host == ""


def test_missing_required_fields_are_reported_per_field():
    with pytest.raises(FieldValidationError) as excinfo:
        parse_waitlist_submission({})

    assert excinfo.value.fields == {
        "name": "Please tell us your name.",
        "firm": "Please tell us your firm's name.",
        "email": "A work email is required.",
    }


def test_non_string_values_count_as_missing():
    with pytest.raises(FieldValidationError) as excinfo:
        parse_waitlist_submission({"name": 42, "firm": None, "email": ["a@b.c"]})

    assert set(excinfo.value.fields) == {"name", "firm", "email"}


@pytest.mark.parametrize("email", ["not-an-email", "a@b", "a b@c.d", "@example.com"])
def test_malformed_email_is_rejected(email):
    with pytest.raises(FieldValidationError) as excinfo:
        parse_waitlist_submission({"name": "Ada", "firm": "LLP", "email": email})

    assert excinfo.value.fields == {
        "email": "That doesn't look like a valid email address."
    }


@pytest.mark.parametrize(
    ("field", "cap"),
    [
        ("name", 200),
        ("firm", 200),
        ("email", 320),
        ("currentSoftware", 100),
        ("message", 2000),
    ],
)
def test_overlong_fields_are_rejected_with_the_marketing_caps(field, cap):
    body = dict(VALID_BODY)
    body[field] = "x" * (cap + 1)
    if field == "email":
        body[field] = "a@" + "x" * cap + ".com"

    with pytest.raises(FieldValidationError) as excinfo:
        parse_waitlist_submission(body)

    assert excinfo.value.fields == {field: f"Please keep this under {cap} characters."}


def test_unknown_keys_are_ignored():
    # The marketing form's honeypot is checked in its SSR action and never
    # forwarded — an unexpected key must not fail the submission here.
    body = dict(VALID_BODY, website="http://spam.example", extra=1)

    parse_waitlist_submission(body)


# --- record + item shape ---------------------------------------------------


def test_record_item_preserves_the_marketing_dynamodb_schema():
    submission = parse_waitlist_submission(VALID_BODY)
    record = create_waitlist_record(submission)

    item = record_item(record)

    assert item == {
        "PK": "WAITLIST",
        "SK": f"{record.submitted_at}#{record.id}",
        "id": record.id,
        "submittedAt": record.submitted_at,
        "status": "new",
        "name": "Ada Lovelace",
        "firm": "Lovelace & Byron LLP",
        "email": "ada@lovelace-law.example",
        "host": "www.insolvia.ai",
        "currentSoftware": "Best Case",
        "message": "Two attorneys, chapter 7 heavy.",
    }
    uuid.UUID(record.id)  # a real uuid4
    assert record.submitted_at.endswith("Z")


def test_record_item_omits_empty_optionals_rather_than_storing_empty_strings():
    record = WaitlistRecord(
        id="abc",
        submitted_at="2026-07-22T00:00:00.000Z",
        submission=WaitlistSubmission(
            name="Ada",
            firm="LLP",
            email="ada@example.com",
            current_software="",
            message="",
            host="",
        ),
    )

    item = record_item(record)

    assert "currentSoftware" not in item
    assert "message" not in item
    assert "host" not in item


# --- the route -------------------------------------------------------------


def test_post_waitlist_returns_201_and_stores_the_record(client, store):
    response = client.post("/v1/waitlist", json=VALID_BODY)

    assert response.status_code == 201
    body = response.get_json()
    assert set(body) == {"id", "submittedAt"}
    uuid.UUID(body["id"])

    assert len(store.records) == 1
    record = store.records[0]
    assert record.id == body["id"]
    assert record.submitted_at == body["submittedAt"]
    assert record_item(record)["email"] == "ada@lovelace-law.example"


def test_post_waitlist_reports_field_errors_as_400(client, store):
    response = client.post(
        "/v1/waitlist", json={"name": "", "firm": "LLP", "email": "nope"}
    )

    assert response.status_code == 400
    assert response.get_json() == {
        "error": "ValidationError",
        "fields": {
            "name": "Please tell us your name.",
            "email": "That doesn't look like a valid email address.",
        },
    }
    assert store.records == []


def test_post_waitlist_rejects_non_json_bodies(client, store):
    response = client.post("/v1/waitlist", data="name=Ada", content_type="text/plain")

    assert response.status_code == 400
    body = response.get_json()
    assert body["error"] == "ValidationError"
    assert "JSON object" in body["message"]
    assert store.records == []


def test_post_waitlist_rejects_json_arrays(client, store):
    response = client.post("/v1/waitlist", json=["not", "an", "object"])

    assert response.status_code == 400
    assert store.records == []
