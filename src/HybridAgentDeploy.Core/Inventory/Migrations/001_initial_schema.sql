-- Migration 001 — initial schema.
--
-- Source of truth: PRD section 6. Migrations are forward-only; once this file has shipped
-- it is never edited, only superseded by 002, 003, ...
--
-- Two deliberate deviations from the PRD 6 DDL, both recorded here so a reader auditing
-- the schema against the specification finds the explanation in the same place:
--
--   1. deployment_result carries staging_path and staging_cleaned. NFR7 requires staged
--      paths to be recorded before the directory is created, so an interrupted run leaves
--      no orphan the tool cannot subsequently identify and clean up. The PRD 6 DDL has
--      nowhere to put that.
--
--   2. dc_last_deployment selects on res.id rather than matching MAX(completed_utc).
--      The PRD 6.1 form returns duplicate rows for one DC whenever two results share a
--      completed_utc — the same run, a coarse clock, or a redeploy inside one second — and
--      a view billed as "last known state per DC" must return exactly one row per DC.

CREATE TABLE domain_controller (
    id                INTEGER PRIMARY KEY,
    fqdn              TEXT NOT NULL UNIQUE COLLATE NOCASE,
    netbios_name      TEXT,
    domain            TEXT,
    site_name         TEXT,
    os_version        TEXT,
    is_read_only      INTEGER NOT NULL DEFAULT 0,
    is_global_catalog INTEGER NOT NULL DEFAULT 0,
    source            TEXT NOT NULL,          -- 'ad_enumeration' | 'file_import' | 'manual'
    first_seen_utc    TEXT NOT NULL,
    last_seen_utc     TEXT NOT NULL,
    is_active         INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE tag (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
    description   TEXT,
    created_utc   TEXT NOT NULL
);

CREATE TABLE dc_tag (
    dc_id       INTEGER NOT NULL REFERENCES domain_controller(id) ON DELETE CASCADE,
    tag_id      INTEGER NOT NULL REFERENCES tag(id) ON DELETE CASCADE,
    applied_utc TEXT NOT NULL,
    PRIMARY KEY (dc_id, tag_id)
);

CREATE TABLE deployment_run (
    id                  INTEGER PRIMARY KEY,
    run_guid            TEXT NOT NULL UNIQUE,
    started_utc         TEXT NOT NULL,
    completed_utc       TEXT,
    operator_account    TEXT NOT NULL,
    msi_path            TEXT NOT NULL,
    msi_file_name       TEXT NOT NULL,
    msi_sha256          TEXT NOT NULL,
    msi_product_version TEXT NOT NULL,
    msi_product_name    TEXT,
    msi_product_code    TEXT,
    org_id              TEXT NOT NULL,
    cloud_mode          INTEGER NOT NULL DEFAULT 1,   -- SG=1
    max_parallel        INTEGER NOT NULL,
    target_count        INTEGER NOT NULL,
    success_count       INTEGER NOT NULL DEFAULT 0,
    failure_count       INTEGER NOT NULL DEFAULT 0,
    was_halted          INTEGER NOT NULL DEFAULT 0,
    halt_reason         TEXT,
    log_directory       TEXT NOT NULL
);

CREATE TABLE deployment_result (
    id               INTEGER PRIMARY KEY,
    run_id           INTEGER NOT NULL REFERENCES deployment_run(id) ON DELETE CASCADE,
    dc_id            INTEGER NOT NULL REFERENCES domain_controller(id),
    started_utc      TEXT,
    completed_utc    TEXT,
    outcome          TEXT NOT NULL,   -- PRD 10.2
    stage            TEXT,            -- 'preflight'|'stage'|'execute'|'retrieve'|'cleanup'
    exit_code        INTEGER,
    attempt_count    INTEGER NOT NULL DEFAULT 1,
    error_category   TEXT,
    error_detail     TEXT,
    msi_log_path     TEXT,
    staging_path     TEXT,                          -- NFR7; see header note 1
    staging_cleaned  INTEGER NOT NULL DEFAULT 0     -- NFR7, SEC8
);

CREATE INDEX ix_deployment_result_dc ON deployment_result(dc_id);
CREATE INDEX ix_deployment_result_run ON deployment_result(run_id);

-- Supports the orphan-recovery query: staging directories written to the database that
-- cleanup never confirmed removed.
CREATE INDEX ix_deployment_result_orphans
    ON deployment_result(staging_cleaned)
    WHERE staging_path IS NOT NULL;

-- PRD 6.1, with the one-row-per-DC correction described in header note 2.
CREATE VIEW dc_last_deployment AS
SELECT dc.id AS dc_id, dc.fqdn, r.msi_product_version, r.org_id,
       res.completed_utc, res.outcome, res.exit_code, res.msi_log_path
FROM domain_controller dc
JOIN deployment_result res ON res.dc_id = dc.id
JOIN deployment_run r ON r.id = res.run_id
WHERE res.id = (
    SELECT r2.id
    FROM deployment_result r2
    WHERE r2.dc_id = dc.id AND r2.completed_utc IS NOT NULL
    ORDER BY r2.completed_utc DESC, r2.id DESC
    LIMIT 1
);
