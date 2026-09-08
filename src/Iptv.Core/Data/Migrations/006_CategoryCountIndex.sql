-- Migration 006 - index for the category picker.
--
-- Listing categories counts the streams in each one, which is a join on
-- (provider_id, kind, category_id) across 118,763 rows. The existing
-- ix_streams_kind_category omits provider_id, so the planner could not use it for the
-- join and the picker took 806ms to open - a stall on every tab switch, for a control
-- the user has not even touched yet.
--
-- Partial on the same predicate the count uses, like ix_streams_catalogue: every consumer
-- filters to active, non-separator rows, so indexing the rest is dead weight in the
-- largest table in the database.

CREATE INDEX ix_streams_provider_category
  ON streams(provider_id, kind, category_id)
  WHERE is_active = 1 AND is_separator = 0;
