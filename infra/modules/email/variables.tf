variable "aws_region" {
  description = <<-EOT
    AWS region hosting the SES identity. Determines the MAIL FROM feedback
    endpoint (feedback-smtp.<region>.amazonses.com) that receives bounces and
    complaints for mail SES sends.
  EOT
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Apex domain to verify as an SES identity (e.g. insolvia.ai)."
  type        = string
}

variable "route53_zone_id" {
  description = "Route53 hosted zone ID containing the SES and mail DNS records."
  type        = string
}

variable "apex_mx_records" {
  description = <<-EOT
    MX record set for the apex — i.e. who RECEIVES mail addressed to
    @insolvia.ai. Defaults to Google Workspace.

    There is exactly one apex MX record set, so this is a mutually exclusive
    choice and the reason the SES inbound forwarder was removed: SES receiving
    needs the apex pointed at `inbound-smtp.<region>.amazonaws.com`, Google
    Workspace needs it pointed at Google, and no arrangement of priorities makes
    both work. Mailboxes now live in Google Workspace, so Google wins.

    `1 smtp.google.com` is Google's current single-record form. The legacy
    five-record ASPMX.L.GOOGLE.COM set is equivalent and still supported — if
    the Admin console shows that set instead, either can go here.

    This is unrelated to the MAIL FROM subdomain's MX (mail.insolvia.ai, below),
    which is the bounce/complaint feedback endpoint for mail SES SENDS and stays
    pointed at SES.
  EOT
  type        = list(string)
  default     = ["1 smtp.google.com"]
}

variable "spf_includes" {
  description = <<-EOT
    Sending systems authorised to send mail as `domain_name`, rendered into the
    apex SPF record as `include:<value>` in list order and terminated `-all`.

    Every system that sends mail with a From: of @insolvia.ai must be listed, or
    receivers will treat its mail as forged. There are two today and they are
    unrelated:

      • amazonses.com   — SES, for transactional mail from no-reply@ sent by our
                          own services.
      • _spf.google.com — Google Workspace, for mail humans send from their
                          insolvia.ai mailboxes.

    This is the APEX SPF only. The MAIL FROM subdomain's SPF (mail.insolvia.ai,
    below) stays SES-only on purpose: it is the Return-Path domain for mail SES
    sends, and Google never sends with that Return-Path.
  EOT
  type        = list(string)
  default     = ["amazonses.com", "_spf.google.com"]
}

variable "google_dkim_value" {
  description = <<-EOT
    The full TXT value Google Workspace generates for `google._domainkey`,
    verbatim from Admin console → Apps → Google Workspace → Gmail →
    Authenticate email — i.e. `v=DKIM1;k=rsa;p=<base64>`.

    Empty means "not set up yet" and publishes no record; Gmail then relies on
    SPF alone for our human mail, which fails DMARC alignment for any forwarded
    message. Treat empty as a temporary state.

    Google's key-length dropdown offers 1024 and 2048. Prefer 2048 — RFC 8301
    deprecated 1024-bit signing keys and receivers increasingly discount them.
    The 1024-only DNS hosts that dropdown exists for are ones that cannot take a
    record over 255 bytes; Route53 can, and the chunking in main.tf handles it,
    so the reason to pick 1024 does not apply to us.

    Unrelated to the SES DKIM CNAMEs, which live at `<ses-token>._domainkey` —
    different names, no collision. Both senders sign independently.
  EOT
  type        = string
  default     = ""
}

variable "additional_apex_txt_records" {
  description = <<-EOT
    Extra TXT values to publish at the apex alongside the SES SPF record. Route53
    permits only one TXT record set per name, so anything else needing an apex TXT
    (domain-ownership verification tokens, for example) must be added here rather
    than as a separate resource, which would clobber the SPF record.
  EOT
  type        = list(string)
  default     = []
}
