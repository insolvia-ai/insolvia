-- The case stores (ADR 0030 build row 3, #622): the DynamoDB case table's
-- partitions, one table per entity.
--
-- TENANCY IS A COLUMN. Every row carries `firm_id`, and every child row's
-- (case_id, firm_id) is a foreign key to its case's (id, firm_id), so a
-- child can never name a case of another firm. `firm_id` references no firm
-- table yet: the firm stores move in their own migration (#621).
--
-- THE CASE ROOT AND ITS HISTORY ARE COLUMNS; every other child keeps the
-- domain's stored item shape (`*_item` / `*_from_item` in insolvia_core and
-- services/api) as `item jsonb`, minus the DynamoDB key attributes, beside
-- the key columns its queries need. The domain keeps validating those
-- documents in code: provenance and confirm-before-entry are domain
-- invariants (docs/reference/case-data-model.md), not SQL ones.
--
-- Timestamps the domain stamps are `timestamptz`; court dates are `date`.
-- An event's `sort_key` stays text: it is the instant-or-day key the
-- calendar range-queries, built by `events.sort_instant`.

CREATE TABLE cases (
    id                 text PRIMARY KEY,
    firm_id            text NOT NULL,
    created_by         text NOT NULL,
    chapter            smallint NOT NULL,
    district           text NOT NULL,
    status             text NOT NULL,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    court              text,
    division           text,
    form_revisions     jsonb,
    constants_set_id   text,
    exemption_set      text,
    filed_at           date,
    meeting_341_at     date,
    case_number        text,
    judge              text,
    trustee            text,
    office_file_number text,
    archived_at        timestamptz,
    archived_by        text,
    deleted_at         timestamptz,
    deleted_by         text,
    -- The notice matcher's key (`case_numbers.case_number_key`), derived on
    -- every write from court + case number; never on the wire.
    case_number_key    text,
    UNIQUE (id, firm_id)
);

-- The firm-wide listing, newest first.
CREATE INDEX cases_by_firm ON cases (firm_id, created_at DESC, id DESC);

CREATE TABLE case_assignments (
    case_id         text NOT NULL,
    subject         text NOT NULL,
    firm_id         text NOT NULL,
    case_created_at timestamptz NOT NULL,
    assigned_at     timestamptz NOT NULL,
    assigned_by     text NOT NULL,
    PRIMARY KEY (case_id, subject),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- The by-assignee listing.
CREATE INDEX case_assignments_by_subject
    ON case_assignments (subject, case_created_at DESC, case_id DESC);

-- Today's STATUS# rows, moved as they are (#506 builds the lifecycle
-- history on top). Keyed by instant, as the sort key was: two moves of one
-- case at the same microsecond collide and the second write is refused.
CREATE TABLE case_status_changes (
    case_id     text NOT NULL,
    changed_at  timestamptz NOT NULL,
    firm_id     text NOT NULL,
    changed_by  text NOT NULL,
    from_status text NOT NULL,
    to_status   text NOT NULL,
    filing_id   text,
    PRIMARY KEY (case_id, changed_at),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- One row per filing role per case. `client_id` feeds the by-client listing
-- (ADR 0022); the partial unique index is "one client, one role per case".
CREATE TABLE debtors (
    case_id         text NOT NULL,
    filing_role     text NOT NULL,
    firm_id         text NOT NULL,
    client_id       text,
    case_created_at timestamptz,
    item            jsonb NOT NULL,
    PRIMARY KEY (case_id, filing_role),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE INDEX debtors_by_client ON debtors (client_id) WHERE client_id IS NOT NULL;
CREATE UNIQUE INDEX debtors_one_role_per_client
    ON debtors (case_id, client_id) WHERE client_id IS NOT NULL;

-- Every case collection (creditors, assets, income, …): `kind` is the
-- collection's `EntityKind.sk_prefix`.
CREATE TABLE case_entities (
    case_id text NOT NULL,
    kind    text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, kind, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE candidates (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE tasks (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE document_requests (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE documents (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE jobs (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

CREATE TABLE packets (
    case_id text NOT NULL,
    id      text NOT NULL,
    firm_id text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (case_id, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- A case event (case_id set) or a firm event (case_id NULL, so the foreign
-- key does not apply). `scope` is `events.partition_key`, the id's scope.
CREATE TABLE events (
    scope    text NOT NULL,
    id       text NOT NULL,
    firm_id  text NOT NULL,
    case_id  text,
    sort_key text COLLATE "C" NOT NULL,
    item     jsonb NOT NULL,
    PRIMARY KEY (scope, id),
    FOREIGN KEY (case_id, firm_id) REFERENCES cases (id, firm_id)
);

-- The firm's calendar: one range query on start.
CREATE INDEX events_by_firm ON events (firm_id, sort_key);

-- One calendar-feed token per firm member.
CREATE TABLE calendar_tokens (
    firm_id text NOT NULL,
    subject text NOT NULL,
    item    jsonb NOT NULL,
    PRIMARY KEY (firm_id, subject)
);
