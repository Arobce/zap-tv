-- Migration 007 - full-text index for series titles.
--
-- Search hits three FTS indexes and one LIKE, and the LIKE was the slowest part of it:
-- 23ms scanning 49,783 series against 2ms for the equivalent FTS lookup. A leading-wildcard
-- LIKE cannot use a B-tree, so the only way to make it fast is to stop using one.
--
-- External-content, like the other two: it holds no data of its own and is rebuilt after a
-- sync rather than maintained by triggers, which would put an index write on the bulk
-- ingest path the PRD budgets by the hundred thousand rows.

CREATE VIRTUAL TABLE series_fts USING fts5(
  title,
  content='series',
  content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);
