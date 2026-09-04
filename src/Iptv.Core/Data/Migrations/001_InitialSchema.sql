-- Migration 001 - initial schema.
--
-- Mirrors the Phase 1 schema in the PRD. Notes worth keeping next to the DDL:
--
--   * Times are unix seconds (INTEGER), never text. The EPG grid range-scans on them
--     millions of rows at a time and text comparison would dominate the query.
--   * channels carries no epg_channel_id. EPG association lives in epg_map so that
--     automatic matching and manual overrides have one home and one source of truth.
--   * programmes_fts indexes title only. Indexing description across millions of
--     programmes multiplies index size and rebuild time for a search nobody asked for.
--   * FTS sync triggers are deliberately absent. Bulk ingest drops and rebuilds the
--     index instead; trigger-per-row on a 50k insert is a large fraction of ingest time.

CREATE TABLE meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
) STRICT;

CREATE TABLE providers (
  id                  INTEGER PRIMARY KEY,
  name                TEXT    NOT NULL,
  kind                TEXT    NOT NULL CHECK (kind IN ('xtream', 'm3u')),
  base_url            TEXT    NOT NULL,
  -- DPAPI-encrypted blobs, never plaintext. See Security in the PRD.
  username            BLOB,
  password            BLOB,
  epg_url             TEXT,
  -- Lower wins during failover.
  priority            INTEGER NOT NULL DEFAULT 0,
  max_connections     INTEGER,
  -- XMLTV offsets are frequently wrong or absent; this is the per-provider correction.
  epg_offset_minutes  INTEGER NOT NULL DEFAULT 0,
  enabled             INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
  last_sync_utc       INTEGER
) STRICT;

CREATE TABLE series (
  id                 INTEGER PRIMARY KEY,
  provider_id        INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  provider_series_id TEXT    NOT NULL,
  title              TEXT    NOT NULL,
  normalized_title   TEXT    NOT NULL,
  -- Cross-provider identity, same derivation as channel_key.
  series_key         TEXT    NOT NULL,
  plot               TEXT,
  cover_url          TEXT,
  year               INTEGER,
  tmdb_id            INTEGER,
  UNIQUE (provider_id, provider_series_id)
) STRICT;

CREATE INDEX ix_series_key ON series(series_key);

CREATE TABLE streams (
  id                 INTEGER PRIMARY KEY,
  provider_id        INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  -- Xtream stream_id, or a synthetic stable id for M3U. See Phase 2 "Stable identity":
  -- tvg-id is routinely empty and routinely duplicated within one playlist.
  provider_stream_id TEXT    NOT NULL,
  kind               TEXT    NOT NULL CHECK (kind IN ('live', 'vod', 'series_episode')),
  title              TEXT    NOT NULL,
  normalized_title   TEXT    NOT NULL,
  tvg_id             TEXT,
  logo_url           TEXT,
  category_id        TEXT,
  url                TEXT    NOT NULL,
  container          TEXT,
  -- Parsed out of the title and kept, never discarded: users want to prefer the UHD
  -- copy while still failing over to the HD one.
  quality            TEXT,
  channel_key        TEXT    NOT NULL,
  catchup_kind       TEXT,
  catchup_source     TEXT,
  catchup_days       INTEGER,
  series_id          INTEGER REFERENCES series(id) ON DELETE CASCADE,
  season_num         INTEGER,
  episode_num        INTEGER,
  -- Provider sync is a merge, never a replace. Absent streams are deactivated and only
  -- hard-deleted once last_seen_utc is old, because providers drop channels for hours.
  is_active          INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
  last_seen_utc      INTEGER,
  UNIQUE (provider_id, provider_stream_id, kind)
) STRICT;

CREATE INDEX ix_streams_channel_key    ON streams(channel_key);
CREATE INDEX ix_streams_kind_category  ON streams(kind, category_id);
CREATE INDEX ix_streams_series         ON streams(series_id, season_num, episode_num);

CREATE TABLE channels (
  channel_key     TEXT PRIMARY KEY,
  display_name    TEXT NOT NULL,
  logo_url        TEXT,
  -- Normalization deliberately collapses "US: ESPN" and "UK| ESPN" onto one key, so the
  -- country is kept separately and used to stop failover substituting a different channel.
  country         TEXT,
  user_sort_order INTEGER,
  is_favorite     INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
  is_hidden       INTEGER NOT NULL DEFAULT 0 CHECK (is_hidden IN (0, 1))
) STRICT;

CREATE TABLE epg_channels (
  epg_channel_id     TEXT PRIMARY KEY,
  -- All <display-name> values, newline-separated. Phase 4 matching needs every one of
  -- them, and they are free to collect during the XMLTV parse.
  display_names      TEXT NOT NULL,
  normalized_names   TEXT NOT NULL,
  icon_url           TEXT,
  source_provider_id INTEGER REFERENCES providers(id) ON DELETE CASCADE
) STRICT;

CREATE TABLE epg_map (
  channel_key    TEXT PRIMARY KEY REFERENCES channels(channel_key) ON DELETE CASCADE,
  epg_channel_id TEXT NOT NULL,
  confidence     REAL NOT NULL,
  method         TEXT NOT NULL
                 CHECK (method IN ('tvg_id', 'display_name', 'normalized', 'fuzzy', 'manual')),
  -- User override. Automatic matching must never overwrite a locked row.
  locked         INTEGER NOT NULL DEFAULT 0 CHECK (locked IN (0, 1)),
  updated_utc    INTEGER NOT NULL
) STRICT;

CREATE TABLE programmes (
  id             INTEGER PRIMARY KEY,
  epg_channel_id TEXT    NOT NULL,
  start_utc      INTEGER NOT NULL,
  stop_utc       INTEGER NOT NULL,
  title          TEXT    NOT NULL,
  subtitle       TEXT,
  description    TEXT,
  category       TEXT,
  episode_num    TEXT
) STRICT;

-- The query the EPG grid actually issues: a set of channels against a time window.
CREATE INDEX ix_programmes_lookup ON programmes(epg_channel_id, start_utc, stop_utc);

CREATE TABLE playback_state (
  -- Keyed on content rather than stream id so resume survives a provider re-sync.
  content_key   TEXT    PRIMARY KEY,
  position_secs INTEGER NOT NULL,
  duration_secs INTEGER,
  completed     INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
  updated_utc   INTEGER NOT NULL
) STRICT;

CREATE TABLE stream_health (
  stream_id     INTEGER NOT NULL REFERENCES streams(id) ON DELETE CASCADE,
  attempted_utc INTEGER NOT NULL,
  outcome       TEXT    NOT NULL
                CHECK (outcome IN ('ok', 'timeout', 'http_error', 'stall', 'decode_error')),
  ttfb_ms       INTEGER,
  detail        TEXT
) STRICT;

CREATE INDEX ix_health_stream_time ON stream_health(stream_id, attempted_utc DESC);

CREATE VIRTUAL TABLE streams_fts USING fts5(
  title,
  content='streams',
  content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);

CREATE VIRTUAL TABLE programmes_fts USING fts5(
  title,
  content='programmes',
  content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);

INSERT INTO meta (key, value) VALUES ('normalization_version', '1');
