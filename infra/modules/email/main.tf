# SES domain identity + the DNS records that make insolvia.ai a usable mail
# domain:
#
#   • SES domain identity for the apex, verified via its _amazonses TXT record
#   • DKIM signing (3 CNAMEs) so outbound mail is signed
#   • a custom MAIL FROM subdomain (mail.insolvia.ai) so the Return-Path
#     aligns with our domain instead of amazonses.com
#   • apex MX pointing inbound mail at Google Workspace, plus SPF / DMARC so
#     receivers know which senders are legitimate
#
# Sending and receiving are deliberately split across two providers: SES sends
# transactional mail as no-reply@, Google Workspace holds the human mailboxes.
# Nothing here receives mail into AWS any more — the SES inbound forwarder that
# used to own hello@ / support@ / security@ was removed when the Workspace
# mailboxes replaced it.
#
# Ported from andreas-services/humbugg/infra/modules/email.

locals {
  mail_from_domain = "mail.${var.domain_name}"
  from_address     = "no-reply@${var.domain_name}"

  # SPF authorising our senders to send as insolvia.ai — see `var.spf_includes`
  # for who and why. Note this is the APEX SPF and is separate from the MAIL FROM
  # subdomain's SPF below — receivers check SPF against the Return-Path (MAIL
  # FROM) domain, while DMARC alignment checks it against the From: header
  # domain, so both records need to exist.
  apex_spf_record = "v=spf1 ${join(" ", [for host in var.spf_includes : "include:${host}"])} -all"

  # Route53 allows exactly ONE TXT record set per name, so every apex TXT value
  # (SPF today; a domain-ownership token or similar tomorrow) must live in this
  # single list. Adding a second `aws_route53_record` of type TXT at the apex
  # would silently clobber this one. New values go through
  # `var.additional_apex_txt_records` instead.
  apex_txt_records = concat([local.apex_spf_record], var.additional_apex_txt_records)
}

# ── Domain identity + verification ──────────────────────────────
resource "aws_ses_domain_identity" "domain" {
  domain = var.domain_name
}

resource "aws_route53_record" "identity_verification" {
  zone_id = var.route53_zone_id
  name    = "_amazonses.${var.domain_name}"
  type    = "TXT"
  ttl     = 300
  records = [aws_ses_domain_identity.domain.verification_token]
}

# Blocks until SES observes the TXT record above. Requires the hosted zone to be
# delegated from the registrar first, otherwise this times out.
resource "aws_ses_domain_identity_verification" "domain" {
  domain = aws_ses_domain_identity.domain.id

  depends_on = [aws_route53_record.identity_verification]
}

# ── DKIM ────────────────────────────────────────────────────────
resource "aws_ses_domain_dkim" "domain" {
  domain = aws_ses_domain_identity.domain.domain
}

resource "aws_route53_record" "dkim" {
  count = 3

  zone_id = var.route53_zone_id
  name    = "${aws_ses_domain_dkim.domain.dkim_tokens[count.index]}._domainkey.${var.domain_name}"
  type    = "CNAME"
  ttl     = 300
  records = ["${aws_ses_domain_dkim.domain.dkim_tokens[count.index]}.dkim.amazonses.com"]
}

# ── Custom MAIL FROM subdomain ──────────────────────────────────
# RejectMessage means SES refuses to send at all if the MAIL FROM MX below is
# missing, rather than silently falling back to amazonses.com and breaking SPF
# alignment.
resource "aws_ses_domain_mail_from" "domain" {
  domain                 = aws_ses_domain_identity.domain.domain
  mail_from_domain       = local.mail_from_domain
  behavior_on_mx_failure = "RejectMessage"

  depends_on = [aws_ses_domain_identity_verification.domain]
}

# MX for the MAIL FROM subdomain ONLY. This is the *feedback* endpoint that
# receives bounces and complaints for mail we send. It is NOT the apex MX and
# must never be confused with it — see `apex_mx` below.
resource "aws_route53_record" "mail_from_mx" {
  zone_id = var.route53_zone_id
  name    = local.mail_from_domain
  type    = "MX"
  ttl     = 300
  records = ["10 feedback-smtp.${var.aws_region}.amazonses.com"]
}

resource "aws_route53_record" "mail_from_spf" {
  zone_id = var.route53_zone_id
  name    = local.mail_from_domain
  type    = "TXT"
  ttl     = 300
  records = ["v=spf1 include:amazonses.com -all"]
}

# ── Apex mail DNS ───────────────────────────────────────────────
# MX for the APEX. This is the INBOUND endpoint: it decides who receives mail
# addressed to @insolvia.ai — Google Workspace, as of the move to real mailboxes.
# See `var.apex_mx_records` for why this is an exclusive choice.
#
# Distinct from `mail_from_mx` above, which only handles bounce/complaint
# feedback for the MAIL FROM subdomain. Getting these two backwards silently
# breaks inbound mail, so they are deliberately kept apart.
resource "aws_route53_record" "apex_mx" {
  zone_id = var.route53_zone_id
  name    = var.domain_name
  type    = "MX"
  ttl     = 300
  records = var.apex_mx_records
}

# Single apex TXT record set — see `local.apex_txt_records`.
resource "aws_route53_record" "apex_txt" {
  zone_id = var.route53_zone_id
  name    = var.domain_name
  type    = "TXT"
  ttl     = 300
  records = local.apex_txt_records
}

# Starts at p=none deliberately: a stricter policy applied before we can see
# aggregate reports risks receivers silently dropping our own legitimate mail.
# Tighten to p=quarantine and then p=reject once the rua reports show all
# legitimate senders passing DKIM/SPF alignment.
resource "aws_route53_record" "dmarc" {
  zone_id = var.route53_zone_id
  name    = "_dmarc.${var.domain_name}"
  type    = "TXT"
  ttl     = 300
  records = ["v=DMARC1; p=none; adkim=r; aspf=r; pct=100"]
}
