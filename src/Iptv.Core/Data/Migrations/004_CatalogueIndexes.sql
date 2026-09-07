-- Migration 004 - indexes for the VOD and series browse views.
--
-- The catalogue queries group by key over the whole of a kind: 158,255 VOD rows on the
-- reference provider. Without an index matching that shape, opening the films view cost
-- 270ms and counting it 565ms, which is felt as a stall rather than a load.
--
-- Partial on the same predicate the queries use. Every consumer filters to active,
-- non-separator rows, so indexing the rest is wasted space in a table that is already the
-- largest in the database.

CREATE INDEX ix_streams_catalogue
  ON streams(kind, channel_key)
  WHERE is_active = 1 AND is_separator = 0;

-- Series browse groups by series_key and orders by year. The reference provider lists
-- 49,783 series, deduplicating to fewer keys.
CREATE INDEX ix_series_browse ON series(series_key, year);

-- Title search is a leading-wildcard LIKE, which no B-tree can serve. It is fast enough
-- in practice (39ms over 158,255 rows) because the scan is index-only where the predicate
-- allows. FTS5 already covers streams_fts for the real search feature in Phase 9; this
-- index is for browsing, not searching.
