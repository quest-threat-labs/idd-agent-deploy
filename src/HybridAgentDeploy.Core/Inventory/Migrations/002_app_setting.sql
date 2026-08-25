-- Small key/value store for things the tool should remember between sessions.
--
-- Introduced for the Org ID: an operator upgrading a forest opens this tool repeatedly over
-- days, and retyping a tenant GUID each time is both tedious and a way to get it wrong.
--
-- Deliberately not a settings file. It belongs beside the inventory it relates to, so a
-- portable installation carries its remembered values with it (NFR4) and an operator working
-- two forests from two folders does not get one forest's Org ID prefilled into the other's.
--
-- Nothing secret goes in here. The Org ID is a tenant identifier that is already recorded in
-- deployment_run and written into every run.log; SEC1 credential material is never persisted
-- anywhere, and this table does not change that.
CREATE TABLE app_setting (
    key         TEXT PRIMARY KEY NOT NULL,
    value       TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) WITHOUT ROWID;
