-- Filing and sealed data (ADR 0030 build row 4, #623): the filing records,
-- the per-filing approvals, the sealed tax ids, the credential vault and the
-- signed authorizations, the case access log and the admin audit log — and
-- the per-service roles and grants every table so far is reached through.
--
-- SEALED VALUES ARE `bytea`, BYTE FOR BYTE. The domain's `Envelope` carries
-- base64 text; the stores decode it on write and encode it on read, so the
-- database holds the exact ciphertext, nonce and KMS-wrapped data key the
-- cipher produced, and never a plaintext. Encryption contexts and the
-- AES-256-GCM envelope (adapters/envelope.py) are unchanged.
--
-- The sealed rows' `created_at`/`updated_at` stay text: the domain stamps
-- them in its own form, and the store hands back exactly what it was given.

-- ── Filing records and approvals (case children) ────────────────

CREATE TABLE filings (
    case_id   text NOT NULL,
    filing_id text NOT NULL,
    firm_id   text NOT NULL,
    item      jsonb NOT NULL,
    PRIMARY KEY (case_id, filing_id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE filing_approvals (
    case_id     text NOT NULL,
    approval_id text NOT NULL,
    firm_id     text NOT NULL,
    item        jsonb NOT NULL,
    PRIMARY KEY (case_id, approval_id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- The case's CURRENT approval: the row every new approval's transaction
-- conditions on (the DynamoDB `SK = FILING_APPROVAL` pointer item).
CREATE TABLE filing_approval_pointers (
    case_id     text PRIMARY KEY,
    approval_id text NOT NULL,
    FOREIGN KEY (case_id, approval_id) REFERENCES filing_approvals (case_id, approval_id)
);

-- ── Sealed tax ids ──────────────────────────────────────────────
-- Keyed by the case they are stored under; the ref is the identity a debtor
-- points at (insolvia_core.tax_ids).

CREATE TABLE tax_ids (
    case_id     text NOT NULL,
    ref         text NOT NULL,
    firm_id     text NOT NULL,
    kind        text NOT NULL,
    last_four   text NOT NULL,
    scheme      text NOT NULL,
    ciphertext  bytea NOT NULL,
    nonce       bytea NOT NULL,
    wrapped_key bytea NOT NULL,
    created_at  text NOT NULL,
    updated_at  text NOT NULL,
    PRIMARY KEY (case_id, ref),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- ── The credential vault and the signed authorizations ──────────
-- ADR 0024's guardrails 2 and 3. Isolation is two layers (ADR 0030): KMS
-- (only the filing role decrypts) and the grants at the end of this file.

CREATE TABLE filing_credentials (
    firm_id           text NOT NULL,
    attorney_id       text NOT NULL,
    credential_id     text NOT NULL,
    login             text NOT NULL,
    courts            jsonb NOT NULL,
    status            text NOT NULL,
    authorization_ref text,
    created_at        text NOT NULL,
    updated_at        text NOT NULL,
    -- The secret. Never readable by the api role (column grants below).
    scheme            text NOT NULL,
    ciphertext        bytea NOT NULL,
    nonce             bytea NOT NULL,
    wrapped_key       bytea NOT NULL,
    PRIMARY KEY (firm_id, attorney_id, credential_id)
);

-- One immutable record per signature (the history)…
CREATE TABLE filing_authorizations (
    firm_id          text NOT NULL,
    attorney_id      text NOT NULL,
    authorization_id text NOT NULL,
    item             jsonb NOT NULL,
    PRIMARY KEY (firm_id, attorney_id, authorization_id)
);

-- …and the attorney's current one, at a fixed key the worker reads.
CREATE TABLE filing_authorization_current (
    firm_id          text NOT NULL,
    attorney_id      text NOT NULL,
    authorization_id text NOT NULL,
    item             jsonb NOT NULL,
    PRIMARY KEY (firm_id, attorney_id)
);

-- ── Append-only logs ────────────────────────────────────────────
-- No foreign keys: a row outlives what it is about (a revoked credential, a
-- purged case). Keyed by what was accessed, as the DynamoDB partition was.

CREATE TABLE case_access_log (
    event_id    text PRIMARY KEY,
    subject_key text NOT NULL,
    recorded_at timestamptz NOT NULL,
    item        jsonb NOT NULL
);

CREATE INDEX case_access_log_by_subject ON case_access_log (subject_key, recorded_at);

CREATE TABLE admin_audit_log (
    event_id    text PRIMARY KEY,
    firm_id     text NOT NULL,
    recorded_at timestamptz NOT NULL,
    item        jsonb NOT NULL
);

CREATE INDEX admin_audit_log_by_firm ON admin_audit_log (firm_id, recorded_at);

-- ── Roles and grants ────────────────────────────────────────────
-- One role per service (ADR 0030). Each logs in with IAM database
-- authentication in a deployed environment (infra/modules/database grants
-- `rds-db:connect` for exactly these names), so each is LOGIN and, where
-- Aurora's `rds_iam` role exists, a member of it. They have no password, so
-- locally nothing can log in as one: the services connect as the compose
-- superuser, and the grants test (SET ROLE) proves what each role may do.
-- `migrator` is the deployed master user and owns what it migrates, so it is
-- granted nothing here (locally it is created, NOLOGIN, for completeness).
-- Roles are cluster-wide, so an existing one is kept.

DO $$
DECLARE
    role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY['api', 'worker', 'admin', 'mcp', 'filing', 'migrator'] LOOP
        BEGIN
            EXECUTE format(
                'CREATE ROLE %I %s',
                role_name,
                CASE WHEN role_name = 'migrator' THEN 'NOLOGIN' ELSE 'LOGIN' END
            );
        EXCEPTION WHEN duplicate_object OR unique_violation THEN
            NULL;
        END;
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rds_iam') THEN
        GRANT rds_iam TO api, worker, admin, mcp, filing;
    END IF;
END $$;

-- Every domain table: read and write for every service. A table created by a
-- later migration gets no grant unless that migration gives one, and the
-- grants test fails until it does.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public
    TO api, worker, admin, mcp, filing;
REVOKE ALL ON schema_migrations FROM api, worker, admin, mcp, filing;

-- The exceptions, each narrower than the line above.
REVOKE ALL ON tax_ids, filing_credentials, filing_authorizations,
    filing_authorization_current, case_access_log, admin_audit_log
    FROM api, worker, admin, mcp, filing;

-- Tax ids: the writers (api; admin's seeder) and the readers that open them
-- for a form (the pipeline worker, the filing worker). MCP holds no grant,
-- as it holds no key.
GRANT SELECT, INSERT, UPDATE, DELETE ON tax_ids TO api, admin;
GRANT SELECT ON tax_ids TO worker, filing;

-- The vault: the api enrols (INSERT) and revokes (DELETE), and reads the
-- status view's columns — never the envelope. The filing worker alone reads
-- the whole row. DELETE … WHERE needs SELECT on the key columns, which the
-- column grant covers.
GRANT INSERT, DELETE ON filing_credentials TO api;
GRANT SELECT (firm_id, attorney_id, credential_id, login, courts, status,
    authorization_ref, created_at, updated_at) ON filing_credentials TO api;
GRANT SELECT ON filing_credentials TO filing;

-- Authorizations: signed and withdrawn through the api, read by the worker.
GRANT SELECT, INSERT, UPDATE, DELETE
    ON filing_authorizations, filing_authorization_current TO api;
GRANT SELECT ON filing_authorizations, filing_authorization_current TO filing;

-- Append-only: INSERT, nothing else, for anyone.
GRANT INSERT ON case_access_log TO api, worker, admin, mcp, filing;
GRANT INSERT ON admin_audit_log TO admin;
