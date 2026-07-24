# Registering an application

Applications own templates and copy. Mailer owns sender identity, admission,
storage, delivery, and status normalization.

To register a service:

1. Add its fixed sender, allowed categories/classes, application role ARN,
   internal queue, status queue, prefixes, configuration set, and kill switch to
   the Terraform-managed registry.
2. Grant its role `execute-api:Invoke` only for its service routes.
3. Give the application the Mailer base URL and fixed service route. Do not give
   it S3 or internal send-queue permissions.
4. Implement the versioned message request and optional attachment-upload calls.
5. Consume normalized events from the assigned status queue idempotently.
6. Add the service to `MAILER_DEVELOPMENT_SERVICES_JSON` so the same routes work
   with the shared development server and Mailpit.

Every application message ID must be stable across retries. Reusing an ID with
different content is a conflict.
