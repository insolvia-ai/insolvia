import pytest

from insolvia_api.core.mail import (
    email_verification_email,
    password_reset_email,
    welcome_email,
)

# --- welcome -----------------------------------------------------------


def test_welcome_email_shape():
    email = welcome_email(
        "ada@example.com", recipient_name="Ada", app_url="https://app.insolvia.ai"
    )

    assert email.category == "welcome"
    assert email.message_class == "transactional"
    assert email.to_address == "ada@example.com"
    assert email.subject == "Welcome to Insolvia"
    assert email.html_body
    assert email.text_body
    assert "https://app.insolvia.ai" in email.html_body
    assert "https://app.insolvia.ai" in email.text_body


def test_welcome_email_without_recipient_name():
    email = welcome_email("ada@example.com", app_url="https://app.insolvia.ai")

    assert "Welcome" in email.html_body or "Hello" in email.html_body
    assert email.html_body
    assert email.text_body


# --- email verification -------------------------------------------------


def test_email_verification_email_shape():
    email = email_verification_email(
        "ada@example.com",
        verification_url="https://app.insolvia.ai/verify?token=abc",
        recipient_name="Ada",
    )

    assert email.category == "email_verification"
    assert email.message_class == "transactional"
    assert email.subject == "Verify your email address"
    assert email.html_body
    assert email.text_body
    assert "https://app.insolvia.ai/verify?token=abc" in email.html_body
    assert "https://app.insolvia.ai/verify?token=abc" in email.text_body


# --- password reset -------------------------------------------------------


def test_password_reset_email_shape():
    email = password_reset_email(
        "ada@example.com",
        reset_url="https://app.insolvia.ai/reset?token=xyz",
        recipient_name="Ada",
    )

    assert email.category == "password_reset"
    assert email.message_class == "transactional"
    assert email.subject == "Reset your Insolvia password"
    assert email.html_body
    assert email.text_body
    assert "https://app.insolvia.ai/reset?token=xyz" in email.html_body
    assert "https://app.insolvia.ai/reset?token=xyz" in email.text_body


# --- shared behaviour -----------------------------------------------------


@pytest.mark.parametrize(
    "builder",
    [
        lambda name: welcome_email(
            "a@b.com", recipient_name=name, app_url="https://app.insolvia.ai"
        ),
        lambda name: email_verification_email(
            "a@b.com", verification_url="https://x.example", recipient_name=name
        ),
        lambda name: password_reset_email(
            "a@b.com", reset_url="https://x.example", recipient_name=name
        ),
    ],
)
def test_recipient_name_is_html_escaped(builder):
    email = builder("<script>alert('hi')</script> & Co")

    assert "<script>" not in email.html_body
    assert "&lt;script&gt;" in email.html_body
    assert "&amp; Co" in email.html_body


@pytest.mark.parametrize(
    "builder",
    [
        lambda: welcome_email("a@b.com", app_url="https://app.insolvia.ai"),
        lambda: email_verification_email(
            "a@b.com", verification_url="https://x.example"
        ),
        lambda: password_reset_email("a@b.com", reset_url="https://x.example"),
    ],
)
def test_html_is_email_client_safe(builder):
    email = builder()

    assert "<style" not in email.html_body
    assert "<link" not in email.html_body
    assert "<script" not in email.html_body
    assert "<button" not in email.html_body
