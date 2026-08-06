from __future__ import annotations

import boto3
from botocore.exceptions import ClientError

from insolvia_api.core.errors import ConflictError


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
        """`email` MUST already be lower-cased. core/firms._parse_email does it.

        THIS POOL IS CASE-SENSITIVE, which is not what "email is the username"
        leads you to expect and is not something a fake can tell you. Measured
        against the real dev pool: creating `a@example.invalid` and then
        `A@EXAMPLE.INVALID` produces TWO accounts, no exception, both with
        their own subject.

        The pool has no `username_configuration` block at all, and Cognito's
        behaviour for an unset one is the legacy default — case sensitive. It
        cannot be changed after creation: the block is immutable, so fixing it
        replaces the pool and every account in it.

        So normalisation is the parser's job and there is exactly one place
        that does it. It is NOT repeated here, because two normalisers that
        must agree are two normalisers that will eventually disagree — and the
        failure would be silent, a second Insolvia account for one human, each
        in a different firm.
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
