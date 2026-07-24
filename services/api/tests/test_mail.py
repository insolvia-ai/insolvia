import pytest

from insolvia_api.core.mail import (
    email_verification_email,
    links_for,
    password_reset_email,
    welcome_email,
)

# Every template takes its footer URLs rather than reading a constant, because
# they are per-environment and the unsubscribe one is per-recipient. These two
# stand in for "production, with a link" and "no signing secret configured".
LINKS = links_for(
    "https://www.insolvia.ai",
    unsubscribe_url="https://www.insolvia.ai/unsubscribe?token=v1.abc.def",
)
LINKS_WITHOUT_UNSUBSCRIBE = links_for("https://www.insolvia.ai")

# --- welcome -----------------------------------------------------------


def test_welcome_email_shape():
    email = welcome_email(
        "ada@example.com",
        links=LINKS,
        recipient_name="Ada",
        app_url="https://app.insolvia.ai",
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
    email = welcome_email(
        "ada@example.com", links=LINKS, app_url="https://app.insolvia.ai"
    )

    assert "Welcome" in email.html_body or "Hello" in email.html_body
    assert email.html_body
    assert email.text_body


# --- email verification -------------------------------------------------


def test_email_verification_email_shape():
    email = email_verification_email(
        "ada@example.com",
        links=LINKS,
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
        links=LINKS,
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
            "a@b.com",
            links=LINKS,
            recipient_name=name,
            app_url="https://app.insolvia.ai",
        ),
        lambda name: email_verification_email(
            "a@b.com",
            links=LINKS,
            verification_url="https://x.example",
            recipient_name=name,
        ),
        lambda name: password_reset_email(
            "a@b.com", links=LINKS, reset_url="https://x.example", recipient_name=name
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
        lambda: welcome_email(
            "a@b.com", links=LINKS, app_url="https://app.insolvia.ai"
        ),
        lambda: email_verification_email(
            "a@b.com", links=LINKS, verification_url="https://x.example"
        ),
        lambda: password_reset_email(
            "a@b.com", links=LINKS, reset_url="https://x.example"
        ),
    ],
)
def test_html_is_email_client_safe(builder):
    email = builder()

    assert "<style" not in email.html_body
    assert "<link" not in email.html_body
    assert "<script" not in email.html_body
    assert "<button" not in email.html_body


# --- footer links (#80) ---------------------------------------------------

BUILDERS_WITH_LINKS = [
    lambda links: welcome_email(
        "a@b.com", links=links, app_url="https://app.insolvia.ai"
    ),
    lambda links: email_verification_email(
        "a@b.com", links=links, verification_url="https://x.example"
    ),
    lambda links: password_reset_email(
        "a@b.com", links=links, reset_url="https://x.example"
    ),
]


@pytest.mark.parametrize("builder", BUILDERS_WITH_LINKS)
def test_footer_carries_both_links(builder):
    email = builder(LINKS)

    for body in (email.html_body, email.text_body):
        assert LINKS.privacy_url in body
        assert LINKS.unsubscribe_url in body


@pytest.mark.parametrize("builder", BUILDERS_WITH_LINKS)
def test_unsubscribe_url_is_also_offered_to_the_mailer(builder):
    # The mailer turns this into List-Unsubscribe + List-Unsubscribe-Post, so
    # a mail client can render its own control. Same URL as the footer link:
    # one opt-out path, two ways to reach it.
    assert builder(LINKS).list_unsubscribe_url == LINKS.unsubscribe_url


@pytest.mark.parametrize("builder", BUILDERS_WITH_LINKS)
def test_no_unsubscribe_link_degrades_visibly(builder):
    email = builder(LINKS_WITHOUT_UNSUBSCRIBE)

    # No signing secret means no token, which means no link — and nothing
    # that pretends to be one. A dead "Unsubscribe" in the footer, or a
    # List-Unsubscribe header pointing nowhere, would be worse than neither.
    assert email.list_unsubscribe_url is None
    assert "Unsubscribe" not in email.html_body
    assert "Unsubscribe:" not in email.text_body
    assert LINKS.privacy_url in email.html_body


@pytest.mark.parametrize("builder", BUILDERS_WITH_LINKS)
def test_links_follow_the_environment(builder):
    staging = links_for(
        "https://staging-www.insolvia.ai",
        unsubscribe_url="https://staging-www.insolvia.ai/unsubscribe?token=t",
    )
    email = builder(staging)

    # A staging email linking www.insolvia.ai/unsubscribe would send a
    # tester's click to the production API and suppress the address there.
    assert "https://staging-www.insolvia.ai/privacy" in email.html_body
    assert "https://www.insolvia.ai" not in email.html_body
