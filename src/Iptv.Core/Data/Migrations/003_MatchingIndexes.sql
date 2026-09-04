-- Migration 003 - indexes for EPG matching.
--
-- Matching joins streams.tvg_id to epg_channels.epg_channel_id case-insensitively.
-- Wrapping both sides in lower() makes the columns' own indexes unusable, so SQLite
-- scanned 28,285 streams against 4,968 guide entries: 7.5 seconds on the reference data,
-- which would stall the UI after every sync and every guide refresh.
--
-- Expression indexes match the join's shape exactly. lower() is deterministic, which is
-- what SQLite requires of an indexed expression.
--
-- Case is not normalized in the columns themselves because tvg_id and epg_channel_id are
-- provider-supplied identifiers; the app should not silently rewrite them, and the
-- diagnostics view shows them as the provider sent them.

CREATE INDEX ix_streams_tvg_id_lower
  ON streams(lower(tvg_id))
  WHERE tvg_id IS NOT NULL AND tvg_id <> '' AND is_separator = 0;

-- The matching side of this join lives on epg_channels, which the ingest swap recreates.
-- EpgIngest.SwapAsync rebuilds this index after the rename, exactly as it does for
-- ix_programmes_lookup; dropping a table takes its indexes with it.
CREATE INDEX ix_epg_channels_id_lower
  ON epg_channels(lower(epg_channel_id));

-- Programme existence is checked once per candidate during matching.
CREATE INDEX ix_programmes_channel ON programmes(epg_channel_id);
