"""The mail templates' parity fixture (plan 18.18): every template in
services/api's core/mail.py rendered by the REAL Python, so the .NET port
(Insolvia.Api.Mail.MailTemplates) is held to it byte for byte.

    python3 python_mail.py   # -> python-mail.json, beside this file

core/mail.py is pure standard library, so any python3 runs it; the module is
loaded by path, with no package install. Every input is synthetic: example.test
addresses, a made-up firm, names chosen to exercise HTML escaping.
"""

from __future__ import annotations

import importlib.util
import json
import sys
from dataclasses import asdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
MAIL = HERE.parents[3] / "services" / "api" / "src" / "insolvia_api" / "core" / "mail.py"

spec = importlib.util.spec_from_file_location("insolvia_mail", MAIL)
assert spec and spec.loader
mail = importlib.util.module_from_spec(spec)
sys.modules["insolvia_mail"] = mail
spec.loader.exec_module(mail)

UNSUBSCRIBE = 'https://staging.example.test/unsubscribe?token=v1.abc&x="1"'
ORIGINS = {
    "with-unsubscribe": "https://staging.example.test/",
    "privacy-only": "http://localhost:5173",
}
LINKS = {
    "with-unsubscribe": mail.links_for(ORIGINS["with-unsubscribe"], UNSUBSCRIBE),
    "privacy-only": mail.links_for(ORIGINS["privacy-only"]),
}
NAMES = {
    "named": "  Pat O'Example <b>&</b>  ",
    "unnamed": None,
    "blank": "   ",
}
FIRM = "Example & Partners <LLP> \"O'Brien\""
URL = "https://app.example.test/portal?a=1&b=<2>"


def render() -> list[dict[str, object]]:
    cases: list[dict[str, object]] = []
    for links_name, links in LINKS.items():
        for name_key, name in NAMES.items():
            built = {
                "welcome": mail.welcome_email(
                    "pat@example.test", links=links, app_url=URL, recipient_name=name
                ),
                "email_verification": mail.email_verification_email(
                    "pat@example.test",
                    links=links,
                    verification_url=URL,
                    recipient_name=name,
                ),
                "password_reset": mail.password_reset_email(
                    "pat@example.test", links=links, reset_url=URL, recipient_name=name
                ),
                "client_invitation": mail.client_invitation_email(
                    "pat@example.test",
                    links=links,
                    firm_name=FIRM,
                    portal_url=URL,
                    recipient_name=name,
                ),
            }
            for template, email in built.items():
                cases.append(
                    {
                        "template": template,
                        "links": links_name,
                        "name": name_key,
                        "email": asdict(email),
                    }
                )
    return cases


def main() -> None:
    inputs = {
        "unsubscribeUrl": UNSUBSCRIBE,
        "marketingOrigins": ORIGINS,
        "names": NAMES,
        "firmName": FIRM,
        "url": URL,
    }
    out = HERE / "python-mail.json"
    out.write_text(
        json.dumps({"inputs": inputs, "cases": render()}, indent=2, ensure_ascii=False)
        + "\n",
        encoding="utf-8",
    )
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
