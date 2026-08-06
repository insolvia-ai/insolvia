# Rotating an IAM user's MFA device

Replacing the virtual MFA device on a human IAM user — new phone, new
authenticator app, or a device that was lost.

> **This is not a Terraform change.** No MFA resource exists in
> [`infra/envs/account-access`](../../infra/envs/account-access/main.tf), on
> purpose: `aws_iam_virtual_mfa_device` writes `base_32_string_seed` — the TOTP
> shared secret — into the state bucket, where anyone with read access could
> then generate valid second factors for an admin. Enrolment stays a console
> action performed by the person holding the device. What *is* in Terraform is
> the user and the group that grant the permission to do it.

## Before you start: it is almost certainly not permissions

Every human user in this account is in the `Admin` group, which carries
`AdministratorAccess` — so every MFA action is already allowed. Confirm rather
than assume:

```bash
aws iam simulate-principal-policy --policy-source-arn "arn:aws:iam::521762924626:user/$USER_NAME" --action-names iam:CreateVirtualMFADevice iam:EnableMFADevice iam:DeactivateMFADevice iam:DeleteVirtualMFADevice iam:ResyncMFADevice iam:ListMFADevices --resource-arns "arn:aws:iam::521762924626:user/$USER_NAME" "arn:aws:iam::521762924626:mfa/$USER_NAME" --query 'EvaluationResults[].{Action:EvalActionName,Decision:EvalDecision}' --output table
```

All six must say `allowed`. If they do, **do not attach the
`AllowManageOwnMFA` policy** that search results recommend — it grants a subset
of what the user already has and changes nothing.

**Pass both resource ARNs.** `EnableMFADevice`, `DeactivateMFADevice`,
`ResyncMFADevice` and `ListMFADevices` authorize against the *user*, not the
MFA device; simulating with only the `mfa/` ARN returns a spurious
`implicitDeny` for those four and sends you chasing a permissions bug that does
not exist.

### The two things that actually fail, and how to tell them apart

**1. The console shows `AccessDenied` noise that has nothing to do with MFA.**
Opening *Security credentials* as an IAM user fires calls no IAM user can make
in any account — `iam:GetAccountName`, `iam:GetAccountEmailAddress`,
`billingconsole:GetAccountInformation`, `sso:DescribeRegisteredRegions`. They
fail, the page renders a permissions-flavoured banner, and the real error is
somewhere else on the page. Ignore them.

**2. `InvalidAuthenticationCodeException` — "Authentication code for device is
not valid".** This is the real one, and it is *authorization succeeding*: the
call reached IAM and was rejected on the code. Causes, in order of likelihood:

- The two codes were not **consecutive and distinct**. AWS wants code *n* and
  code *n+1* from two different 30-second windows. Typing the same code twice,
  or two codes from the same window, fails.
- Codes came from the **old** entry in the authenticator app. Scanning a new QR
  adds a second entry with the same account label; the old one keeps producing
  valid-looking codes for a device AWS is not enrolling.
- Clock drift on the phone. Google Authenticator: ⋮ → Settings → Time
  correction for codes → Sync now. iOS: Settings → General → Date & Time →
  toggle *Set Automatically* off and on.

Confirm which one you hit — CloudTrail records the outcome and costs nothing:

```bash
aws cloudtrail lookup-events --lookup-attributes AttributeKey=EventName,AttributeValue=EnableMFADevice --max-results 5 --query 'Events[].CloudTrailEvent' --output text
```

An `errorCode` of `InvalidAuthenticationCodeException` means the code; an
`AccessDenied` or `errorCode` naming an IAM action means permissions, and then
the simulate above should have caught it.

## The procedure

1. **Sign in to the console as the IAM user** (not root), and open *Security
   credentials*.
2. **Deactivate the old device** if it still exists. AWS allows up to 8 devices
   per user, so this is not strictly required — but leaving a device you no
   longer hold is a live second factor for an admin account.
3. **Assign a new MFA device.** Give it a name **no existing device already
   uses**, including unassigned ones (see cleanup below) — a collision fails
   with `EntityAlreadyExists`, which reads nothing like a naming problem.
4. **Scan the QR into the authenticator, then delete the old entry from the
   app** before typing anything. This removes the single most common cause of
   the invalid-code failure.
5. **Enter two consecutive codes**, waiting for the second to roll over.
6. **Verify**, and do not skip this — a half-finished enrolment leaves an
   unassigned device behind and no working factor:

   ```bash
   aws iam list-mfa-devices --user-name "$USER_NAME"
   ```

   Done when exactly one device is listed with the new serial and a current
   `EnableDate`.
7. **Sign out and sign back in** with the new factor, in a private window, before
   closing the session you still have. This is the step that catches an
   enrolment that reported success but does not work.

## Cleanup: unassigned virtual MFA devices

A failed or abandoned enrolment leaves a virtual MFA device created but never
attached. It grants nothing, but it holds the name — which is what makes the
next attempt fail on `EntityAlreadyExists`.

```bash
aws iam list-virtual-mfa-devices --assignment-status Unassigned --query 'VirtualMFADevices[].SerialNumber' --output table
```

Delete the ones you recognise as dead:

```bash
aws iam delete-virtual-mfa-device --serial-number "arn:aws:iam::521762924626:mfa/<name>"
```

**Check `list-virtual-mfa-devices` against `get-account-summary` before
deleting anything.** The root user's device also reports with no assignee in
the first command, because the `User` field is only populated for IAM users —
so a root device looks exactly like an orphan. `MFADevicesInUse` in
`aws iam get-account-summary` counts root's; if that number exceeds the devices
you can account for across `list-mfa-devices` for each IAM user, the difference
is root's and must not be touched.

## Related

- [`infra/envs/account-access`](../../infra/envs/account-access/main.tf) — the
  users and groups this permission comes from, and why MFA is not modelled there.
- [`terraform.md`](../reference/terraform.md#human-account-access) — where this
  root sits in the state model.
- `insolvia-aws-auth` skill — if the `aws` commands above cannot authenticate.
