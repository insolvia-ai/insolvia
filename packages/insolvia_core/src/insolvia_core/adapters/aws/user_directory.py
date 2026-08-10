from __future__ import annotations

import boto3
from botocore.exceptions import ClientError

from insolvia_core.errors import ConflictError, NotFoundError


class CognitoUserDirectory:
    """UserDirectory backed by the environment's Cognito user pool.

    Credentials come from the runtime's default provider chain — the Lambda
    execution role in AWS, or in local dev the short-lived credentials the
    developer's AWS profile supplies. There is no emulator: `infra/envs/dev`
    provisions this machine's real pool.
    """

    def __init__(self, user_pool_id: str) -> None:
        self.user_pool_id = user_pool_id
        self.client = boto3.client("cognito-idp")

    def create_user(self, email: str) -> str:
        """`email` arrives already lower-cased. core/firms._parse_email does it.

        THE POOL NO LONGER DEPENDS ON THAT, and the history is worth keeping
        because it is what the lower-casing was originally load-bearing for.

        Cognito's legacy default is case-SENSITIVE usernames, which for a pool
        whose username is an email address meant `a@example.invalid` and
        `A@EXAMPLE.INVALID` were two accounts — measured against the real
        staging pool, not inferred. `infra/modules/auth` now sets
        `username_configuration { case_sensitive = false }`, so Cognito matches
        case-insensitively and this method's duplicate detection is correct for
        any casing.

        The parser still normalises, for a smaller and still-good reason: it is
        what makes the address we STORE deterministic, so the firm's staff list
        and any future lookup of ours agree with each other. It is deliberately
        not repeated here — two normalisers that must agree are two normalisers
        that will eventually disagree.
        """
        try:
            response = self.client.admin_create_user(
                UserPoolId=self.user_pool_id,
                Username=email,
                UserAttributes=[
                    {"Name": "email", "Value": email},
                    # The pool has `auto_verified_attributes = ["email"]`, but
                    # that governs SELF-service verification. An
                    # admin-created user starts UNVERIFIED unless this says
                    # otherwise, and an unverified address cannot drive
                    # account recovery — so a colleague who lost their
                    # invitation email would have no way back in without us.
                    {"Name": "email_verified", "Value": "true"},
                ],
                # Cognito emails the temporary password to the address above.
                # NOTHING IN THIS SERVICE EVER SEES IT: no password is
                # generated here, none is returned, and the role holds no
                # AdminSetUserPassword — which is what keeps creating an
                # account from being a way to become one.
                DesiredDeliveryMediums=["EMAIL"],
            )
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code == "UsernameExistsException":
                # A 409, and the message names the cause. It is a small
                # account-enumeration surface — an authenticated firm admin
                # learns that some address has an Insolvia account — and it is
                # the right trade: the alternative is a firm admin unable to
                # tell "already here" from "something broke", on the one
                # workflow where the fix depends entirely on which it is.
                raise ConflictError(
                    "that email address already has an Insolvia account"
                ) from error
            raise

        # The subject Cognito assigned, read back rather than minted. A
        # made-up value would produce a firm-user row nobody can sign in as,
        # and nothing would notice until the person tried.
        for attribute in response["User"]["Attributes"]:
            if attribute["Name"] == "sub":
                return str(attribute["Value"])
        # Cognito always returns `sub` on a created user; if it ever does not,
        # the row must not be written with a guess.
        raise RuntimeError("Cognito returned a created user with no subject")

    def resend_invite(self, email: str) -> None:
        """AdminCreateUser with MessageAction=RESEND — the same IAM action as
        create_user, which is why the one-action grant covers both (the port's
        docstring owns that argument). Cognito re-sends the invitation with a
        FRESH temporary password; nothing here sees either one."""
        try:
            self.client.admin_create_user(
                UserPoolId=self.user_pool_id,
                Username=email,
                MessageAction="RESEND",
                DesiredDeliveryMediums=["EMAIL"],
            )
        except ClientError as error:
            code = error.response.get("Error", {}).get("Code")
            if code == "UserNotFoundException":
                raise NotFoundError("no account exists for that address") from error
            if code == "UnsupportedUserStateException":
                # Already CONFIRMED: they signed in at least once, so the
                # invite did its job. A password problem now is the
                # forgot-password flow's, and re-inviting would not help —
                # RESEND is refused by Cognito for exactly this state.
                raise ConflictError(
                    "that user has already completed first sign-in; "
                    "the forgot-password flow is the way back in"
                ) from error
            raise
