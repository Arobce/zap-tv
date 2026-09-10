-- Migration 008 - series categories.
--
-- The series listing has always carried a category_id and this schema has always dropped
-- it. The consequence was a Series tab with 29,100 entries, newest first, and no way to
-- narrow them: the categories table already accepts kind='series', the provider already
-- publishes the names, and the only missing piece was somewhere to join them to.
--
-- Nullable, because it is. A provider can list a series with no category, and a sync must
-- be able to store that rather than inventing one.

ALTER TABLE series ADD COLUMN category_id TEXT;

-- Partial, matching ix_streams_provider_category from migration 006 and for the same
-- reason: the rows with no category are not the ones being looked up, and indexing them
-- costs space on every insert to answer a query nobody runs.
CREATE INDEX ix_series_provider_category
    ON series(provider_id, category_id)
 WHERE category_id IS NOT NULL;
