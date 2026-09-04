-- Migration 002 - classification derived at ingest.
--
-- Both columns are computed by StreamMapper once per stream during sync, rather than
-- re-derived on read. Normalization runs across 28,285 entries per sync on the reference
-- provider, and the EPG grid queries these columns on every scroll frame.

-- Providers pad their listings with section headings ("##### GOLDEN EVENTS #####"):
-- 1,137 of 28,285 live entries on the reference provider. These are kept and shown,
-- because they are the provider's own grouping and users navigate by them, but they are
-- not channels, so search, dedup, failover candidacy and the EPG coverage denominator
-- each exclude them.
ALTER TABLE streams ADD COLUMN is_separator INTEGER NOT NULL DEFAULT 0;

-- Country prefix stripped by normalization ("US: ESPN" -> "espn"). Kept because that
-- normalization deliberately collapses "US: ESPN" and "UK| ESPN" onto one channel_key,
-- and failover needs the country to avoid silently substituting a different channel.
-- Aggregated up to channels.country during RefreshChannels.
ALTER TABLE streams ADD COLUMN country TEXT;

-- Partial index: the overwhelming majority of rows are real channels, and every consumer
-- of is_separator filters for 0.
CREATE INDEX ix_streams_real_channels
  ON streams(provider_id, kind)
  WHERE is_separator = 0;
