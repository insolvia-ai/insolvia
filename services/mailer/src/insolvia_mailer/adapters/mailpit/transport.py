from __future__ import annotations

import os
import smtplib
from email.message import EmailMessage


class MailpitTransport:
    """Development SMTP transport targeting the private Mailpit container."""

    def __init__(self) -> None:
        self.host = os.environ.get("MAILER_SMTP_HOST", "mailpit")
        self.port = int(os.environ.get("MAILER_SMTP_PORT", "1025"))

    def send(self, message: EmailMessage) -> None:
        with smtplib.SMTP(self.host, self.port, timeout=10) as smtp:
            smtp.send_message(message)
