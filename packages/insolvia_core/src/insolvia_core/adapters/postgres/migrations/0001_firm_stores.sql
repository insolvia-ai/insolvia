-- The firm stores (ADR 0030 build row 2, #621).
--
-- Each row is keyed by its firm, and `item` holds the domain's stored shape
-- (`firms.firm_item`, `firm_clients.firm_client_item`, …) minus the DynamoDB
-- key attributes: the domain keeps owning that shape and validating it on
-- read. Columns exist for what a query keys, filters or locks on.
--
-- Never edit this file once merged; correct it with the next number.

CREATE TABLE firms (
    id   text  PRIMARY KEY,
    item jsonb NOT NULL
);

-- One person, one firm is still an application invariant (FirmStore.find_user
-- raises on two rows), so `subject` is indexed, not unique.
CREATE TABLE firm_users (
    firm_id text  NOT NULL REFERENCES firms (id),
    subject text  NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (firm_id, subject)
);
CREATE INDEX firm_users_by_subject ON firm_users (subject);

CREATE TABLE firm_clients (
    firm_id text  NOT NULL REFERENCES firms (id),
    id      text  NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (firm_id, id)
);

CREATE TABLE library_creditors (
    firm_id text  NOT NULL REFERENCES firms (id),
    id      text  NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (firm_id, id)
);

CREATE TABLE firm_questionnaires (
    firm_id text  PRIMARY KEY REFERENCES firms (id),
    item    jsonb NOT NULL
);

CREATE TABLE firm_document_checklists (
    firm_id text  PRIMARY KEY REFERENCES firms (id),
    item    jsonb NOT NULL
);

-- The portal binding (insolvia_core.clients). One row per subject per firm;
-- the case it opens is a column, so a case's bindings are one indexed query
-- and the DynamoDB case-partition mirror is gone.
CREATE TABLE client_bindings (
    firm_id text  NOT NULL REFERENCES firms (id),
    subject text  NOT NULL,
    case_id text  NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (firm_id, subject)
);
CREATE INDEX client_bindings_by_subject ON client_bindings (subject);
CREATE INDEX client_bindings_by_case ON client_bindings (case_id);

-- Which subject answers for a filing role on a case: one holder per role.
-- No foreign key to the case yet: the case table arrives with the case
-- stores (#622).
CREATE TABLE client_role_claims (
    case_id text NOT NULL,
    role    text NOT NULL,
    subject text NOT NULL,
    PRIMARY KEY (case_id, role)
);

-- Append-only marketing submissions (insolvia_api.core.waitlist.record_item).
CREATE TABLE waitlist (
    id           text        PRIMARY KEY,
    submitted_at timestamptz NOT NULL,
    item         jsonb       NOT NULL
);
