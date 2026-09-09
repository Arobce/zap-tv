# PRD — Windows IPTV Player

**Target agent:** Claude Code
**Platform:** Windows 10 1809+ / Windows 11, x64 and ARM64
**Stack:** .NET 10 (LTS), C#, WinUI 3 (Windows App SDK), libmpv render API, SQLite + FTS5

---

## 0. How to use this document

Build in the phase order given. Each phase ends with an **Exit criteria** block — do not start the next phase until every item passes. Phases 1–5 have no UI; they are validated by a console harness and tests. This is deliberate. The riskiest parts of this project are EPG ingest performance and mpv interop, and both are far cheaper to get wrong in a console app than inside a XAML view.

The exception is Phase 6, which stops and builds a deliberately minimal usable app before the expensive UI work begins. Harnesses prove throughput; they do not tell you whether the thing is any good against real providers.

**Phase 0 comes before all of it and is not optional.** Three of the constraints in this document — the video presentation path, the ARM64 target, and the EPG ingest budget — are assumptions, not established facts. Phase 0 turns each one into a decision record. If a spike invalidates an assumption, amend this document before continuing rather than building around it.

When a decision in this document conflicts with a plausible-looking tutorial or Stack Overflow answer, follow this document. Several of the constraints below exist specifically because the obvious approach fails.

---

## 1. Product summary

A desktop IPTV player that connects to user-supplied providers (Xtream Codes API or M3U/XMLTV URLs) and plays live TV, VOD, and series.

The app ships no content, no bundled playlists, no default provider, and no channel discovery. It is bring-your-own-source only. Do not add a "free channels" list, sample playlist, or provider directory at any point.

### What makes it better than existing players

These five items are the product. Everything else is table stakes.

1. **Sub-second channel change.** Existing players take 3–6 seconds. Target p50 under 800ms.
2. **Multi-provider merge with automatic failover.** Users commonly hold 2–3 subscriptions. Deduplicate channels across them, and when a stream stalls, transparently switch to another provider's copy of the same channel.
3. **A genuinely fast EPG grid.** 2D-virtualized, scrolls at refresh rate with 20k channels loaded.
4. **Instant search.** FTS5 across channels, VOD, series, and programme titles. Results as you type, no debounce longer than 80ms.
5. **A guide that is actually populated.** Automatic EPG-to-channel matching with a visible coverage metric and a manual remap UI. Competing players routinely leave a third of the guide empty and give the user no way to fix it. See Phase 4.

### Explicit non-goals for v1

Recording/DVR, transcoding, casting, mobile or TV builds, account sync, plugin system.

Resume position for VOD and series ("continue watching") is **in scope** — it is not DVR, it is table stakes for anything that plays a two-hour file.

---

## 2. Solution structure

```
Iptv.slnx
  CLAUDE.md               <- repo root, not docs/. Claude Code reads it from here.
  src/
    Iptv.Core/            net10.0 — no UI package references, ever
      Xtream/             player_api.php client + DTOs
      Playlists/          M3U/M3U8 streaming parser
      Epg/                XMLTV pull-parser, ingest pipeline, channel matching
      Data/               SQLite context, migrations, repositories
      Sources/            provider merge, channel identity, failover policy
      Metadata/           TMDB enrichment (optional feature)
      Models/             domain records
    Iptv.Mpv/             net10.0-windows — P/Invoke, render context, GPU interop
    Iptv.App/             WinUI 3, MVVM, packaged
    Iptv.Harness/         console app for timing ingest and playback smoke tests
  tests/
    Iptv.Core.Tests/
    Iptv.Mpv.Tests/
  docs/
    decisions/            one file per Phase 0 spike outcome
```

`Iptv.Core` must not reference `Microsoft.WindowsAppSDK`, `Microsoft.UI.Xaml`, or `CommunityToolkit.Mvvm`. Enforce this with a build check. If core logic needs to notify the UI, it exposes `IAsyncEnumerable<T>`, `Channel<T>`, or plain events — never `DispatcherQueue`.

### Key NuGet packages

- `Microsoft.WindowsAppSDK` (WinUI 3)
- `CommunityToolkit.Mvvm` (App layer only)
- `CommunityToolkit.WinUI.Collections` — `IncrementalLoadingCollection`
- `Microsoft.Data.Sqlite` — must be the SQLitePCLRaw bundle with FTS5 enabled (`SQLitePCLRaw.bundle_e_sqlite3`)
- `System.Threading.Channels`
- `Serilog` + `Serilog.Sinks.File`
- `Velopack` (packaging/update, Phase 10)

Use raw ADO.NET via `Microsoft.Data.Sqlite` for the bulk ingest path. EF Core is acceptable for CRUD on settings and providers but must not be used for programme inserts.

Package versions are centrally managed in `Directory.Packages.props`; projects reference by name only.

### Why .NET 10 and not .NET 9

This document originally specified .NET 9. .NET 9 is an STS release whose support ended in May 2026, so it is not a defensible target for a greenfield app being started now. .NET 10 is the current LTS with support into November 2028, and the language and runtime differences are immaterial to anything in this document.

---

## 3. Phase 0 — De-risking spikes

Four timeboxed spikes. Each produces a short file in `docs/decisions/`. Throwaway code; do not carry it forward.

### 0.1 — libmpv render API inventory (1 day) — BLOCKING

The video presentation design in Phase 5 depends on which render backends your libmpv build actually exposes. Establish this before writing any interop.

**Resolved, and it invalidated the original design.** `render.h` in the shipping build (client API 2.5) defines exactly two backends:

```c
#define MPV_RENDER_API_TYPE_OPENGL "opengl"
#define MPV_RENDER_API_TYPE_SW     "sw"
```

**There is no D3D11 render API.** No `render_d3d11.h`, and no header mentions D3D11 or DXGI at all. The version of this document that specified rendering "into a D3D11 texture" as the primary path described something libmpv does not provide. mpv uses D3D11 internally for `--gpu-api=d3d11`, but that is mpv choosing a backend for its own window; it never hands the embedder a texture.

The presentation path is therefore **OpenGL over ANGLE**, as detailed in Phase 5. See [0001](docs/decisions/0001-libmpv-render-api.md).

Re-run this inventory when upgrading libmpv. It is a one-line grep for `MPV_RENDER_API_TYPE`, and it is the cheapest check in this document relative to what it prevents.

### 0.2 — Compositing spike (3 days) — BLOCKING, highest risk in the project

Smallest possible WinUI 3 app. No MVVM, no DI, no navigation. It must:

- Create an mpv render context using the backend chosen in 0.1 (expected: OpenGL via ANGLE, whose backing device is D3D11).
- Render into a texture shared with the XAML compositor and present it through a `SwapChainPanel` via `ISwapChainPanelNative.SetSwapChain`.
- Play a live MPEG-TS stream.
- Draw a semi-transparent XAML `Border` with text over the video and confirm it composites correctly.
- Survive resize and DPI change without corruption.

If this cannot be made to work in three days, stop and escalate. Every UI decision downstream assumes it. Do not resolve a failure here by reaching for `--wid` — see the prohibition in Phase 5.

### 0.3 — ARM64 feasibility (half day)

**Resolved.** A maintained ARM64 build ships alongside x64 in the same shinchiro release (`mpv-dev-aarch64-*`), so `win-arm64` stays. See [0003](docs/decisions/0003-arm64-feasibility.md). Not yet validated on ARM64 hardware — buildable and shippable, not tested.

### 0.4 — SQLite ingest throughput floor (half day)

Before committing to the Phase 3 budget, measure the machine rather than the theory. Generate ~2M synthetic programme rows and measure sustained insert rate through one prepared command, 5,000-row transactions, WAL + `synchronous=NORMAL`, no indexes and no FTS.

The Phase 3 target only holds if you clear roughly 400k rows/s. If the real number is materially lower, revise the Phase 3 exit criteria now rather than discovering it as a failed gate later.

**Exit criteria**
- Four decision records written to `docs/decisions/`.
- 0.2 demonstrated on at least one machine and recorded as a short screen capture.
- Phases 5 and 10 amended in this document if 0.1 or 0.3 contradict them.

---

## 4. Phase 1 — Data layer and schema

### Schema

```sql
CREATE TABLE meta (                  -- schema/algorithm versioning, see Normalization versioning
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE providers (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,              -- 'xtream' | 'm3u'
  base_url TEXT NOT NULL,
  username TEXT, password TEXT,    -- DPAPI-encrypted blob, see Security
  epg_url TEXT,
  priority INTEGER NOT NULL DEFAULT 0,   -- lower wins during failover
  max_connections INTEGER,
  epg_offset_minutes INTEGER NOT NULL DEFAULT 0,
  enabled INTEGER NOT NULL DEFAULT 1,
  last_sync_utc TEXT
);

CREATE TABLE series (
  id INTEGER PRIMARY KEY,
  provider_id INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  provider_series_id TEXT NOT NULL,
  title TEXT NOT NULL,
  normalized_title TEXT NOT NULL,
  series_key TEXT NOT NULL,              -- cross-provider identity, same derivation as channel_key
  plot TEXT,
  cover_url TEXT,
  year INTEGER,
  tmdb_id INTEGER,
  UNIQUE(provider_id, provider_series_id)
);
CREATE INDEX ix_series_key ON series(series_key);

CREATE TABLE streams (
  id INTEGER PRIMARY KEY,
  provider_id INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  provider_stream_id TEXT NOT NULL,      -- Xtream stream_id; for M3U see "Stable identity" in Phase 2
  kind TEXT NOT NULL,                    -- 'live' | 'vod' | 'series_episode'
  title TEXT NOT NULL,
  normalized_title TEXT NOT NULL,        -- see Channel identity
  tvg_id TEXT,
  logo_url TEXT,
  category_id TEXT,
  url TEXT NOT NULL,
  container TEXT,                        -- 'ts' | 'm3u8' | 'mp4' | 'mkv'
  quality TEXT,                          -- 'UHD' | 'FHD' | 'HD' | 'SD' | NULL — parsed from title, never discarded
  channel_key TEXT NOT NULL,             -- cross-provider identity
  catchup_kind TEXT,                     -- M3U 'catchup' attribute, NULL if unsupported
  catchup_source TEXT,
  catchup_days INTEGER,
  series_id INTEGER REFERENCES series(id) ON DELETE CASCADE,   -- kind='series_episode' only
  season_num INTEGER,
  episode_num INTEGER,
  is_active INTEGER NOT NULL DEFAULT 1,  -- soft delete on re-sync, see Provider re-sync semantics
  last_seen_utc INTEGER,
  UNIQUE(provider_id, provider_stream_id, kind)
);
CREATE INDEX ix_streams_channel_key ON streams(channel_key);
CREATE INDEX ix_streams_kind_category ON streams(kind, category_id);
CREATE INDEX ix_streams_series ON streams(series_id, season_num, episode_num);

CREATE TABLE channels (              -- merged logical channel across providers
  channel_key TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  logo_url TEXT,
  country TEXT,                      -- parsed prefix/group; used as a failover guard
  user_sort_order INTEGER,
  is_favorite INTEGER NOT NULL DEFAULT 0,
  is_hidden INTEGER NOT NULL DEFAULT 0
);
```

Note that `channels` carries no `epg_channel_id`. EPG association lives in `epg_map` below, so automatic matching and manual overrides have one home and one source of truth.

```sql
CREATE TABLE epg_channels (          -- every <channel> element seen in an XMLTV source
  epg_channel_id TEXT PRIMARY KEY,
  display_names TEXT NOT NULL,       -- all <display-name> values, newline-separated
  normalized_names TEXT NOT NULL,    -- each name normalized, newline-separated
  icon_url TEXT,
  source_provider_id INTEGER REFERENCES providers(id) ON DELETE CASCADE
);

CREATE TABLE epg_map (
  channel_key TEXT PRIMARY KEY REFERENCES channels(channel_key) ON DELETE CASCADE,
  epg_channel_id TEXT NOT NULL,
  confidence REAL NOT NULL,          -- 0..1
  method TEXT NOT NULL,              -- 'tvg_id' | 'display_name' | 'normalized' | 'fuzzy' | 'manual'
  locked INTEGER NOT NULL DEFAULT 0, -- user override; automatic matching must never overwrite a locked row
  updated_utc INTEGER NOT NULL
);

CREATE TABLE programmes (
  id INTEGER PRIMARY KEY,
  epg_channel_id TEXT NOT NULL,
  start_utc INTEGER NOT NULL,          -- unix seconds, NOT text
  stop_utc INTEGER NOT NULL,
  title TEXT NOT NULL,
  subtitle TEXT,
  description TEXT,
  category TEXT,
  episode_num TEXT
);
CREATE INDEX ix_programmes_lookup ON programmes(epg_channel_id, start_utc, stop_utc);

CREATE TABLE playback_state (        -- continue-watching; keyed on content, not on stream id
  content_key TEXT PRIMARY KEY,      -- channel_key of the VOD item or episode
  position_secs INTEGER NOT NULL,
  duration_secs INTEGER,
  completed INTEGER NOT NULL DEFAULT 0,
  updated_utc INTEGER NOT NULL
);

CREATE TABLE stream_health (
  stream_id INTEGER NOT NULL REFERENCES streams(id) ON DELETE CASCADE,
  attempted_utc INTEGER NOT NULL,
  outcome TEXT NOT NULL,             -- 'ok' | 'timeout' | 'http_error' | 'stall' | 'decode_error'
  ttfb_ms INTEGER,
  detail TEXT
);
CREATE INDEX ix_health_stream_time ON stream_health(stream_id, attempted_utc DESC);

CREATE VIRTUAL TABLE streams_fts USING fts5(
  title, content='streams', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
);
CREATE VIRTUAL TABLE programmes_fts USING fts5(
  title, content='programmes', content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);
```

`programmes_fts` indexes **title only**. Indexing `description` across millions of programmes multiplies index size and rebuild time for a search nobody asked for. If description search is wanted later, make it an opt-in setting that triggers a one-time reindex.

Keep FTS tables in sync with triggers on insert/update/delete for ordinary CRUD, but **drop the triggers before bulk ingest and rebuild the index afterward** with `INSERT INTO streams_fts(streams_fts) VALUES('rebuild')`. Trigger-per-row on a 50k insert is a large fraction of ingest time.

### External-content FTS and the EPG table swap

These two designs collide, and the collision must be handled explicitly rather than discovered. `programmes_fts` is bound to the *table name* `programmes` and to its rowids. The Phase 3 staging-and-swap therefore leaves the FTS index pointing at content that no longer exists.

The rule: after the swap transaction commits, always run `INSERT INTO programmes_fts(programmes_fts) VALUES('rebuild')`. Never try to preserve the old index across a swap. Budget that rebuild as ingest time — the Phase 3 exit criteria state parse/insert and total-ingest numbers separately for exactly this reason.

### Normalization versioning

`channel_key` is derived from the normalization function, and `channels.is_favorite`, `is_hidden`, and `user_sort_order` all hang off `channel_key`. Any change to the normalization rules therefore silently relocates every user's favourites.

Store the algorithm version in `meta` under `normalization_version`. Any change to normalization increments it, and the migration shipping that change must rewrite `channel_key` across `streams`, `channels`, `epg_map`, and `playback_state` in one transaction, remapping user state onto the new keys. Treat this as a schema migration, because it is one.

### Retention

`stream_health` is append-only, and every playback attempt and failover probe writes to it. Prune on app start: delete rows older than 90 days, and keep at most the 500 most recent rows per `stream_id`. Without this the diagnostics queries degrade over months of use.

### Connection configuration

Open with these pragmas, in this order, on every connection:

```
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA mmap_size=268435456;
PRAGMA cache_size=-64000;
PRAGMA foreign_keys=ON;
```

WAL matters because the EPG refresh writes while the UI reads. Without it, the grid stutters during background sync.

`foreign_keys` is stated explicitly because the schema is full of `ON DELETE CASCADE`, and cascades are inert without it. Raw SQLite defaults it **off** per connection; `Microsoft.Data.Sqlite` happens to turn it on. Do not rely on that — set it, so the guarantee survives a provider change or a connection-string edit.

`cache_size=-64000` is 64MB **per connection**, and `Microsoft.Data.Sqlite` pools connections per connection string. Keep the pool small and deliberate — one long-lived reader for the UI, one writer — rather than opening ad-hoc connections from repositories and multiplying the cache budget by the pool size.

Store the database at `%LOCALAPPDATA%\IptvPlayer\library.db`. Logs beside it in `logs\`.

**Exit criteria**
- Migrations run from empty to current on a fresh machine.
- A test loads 200k programme rows across 2,000 EPG channels and serves the **grid window query** — 200 `epg_channel_id` values against a 3-hour window — in under 15ms. The single-channel point lookup is not a meaningful benchmark; the grid window is what the UI actually issues.
- A normalization migration test: bump `normalization_version`, run the migration, assert favourites and sort order survive.
- FTS5 confirmed available at runtime; app fails loudly at startup if not.

---

## 5. Phase 2 — Provider ingest

### Xtream Codes client

Base: `{base_url}/player_api.php?username={u}&password={p}`

Actions to implement: no action (account info), `get_live_categories`, `get_live_streams`, `get_vod_categories`, `get_vod_streams`, `get_vod_info`, `get_series_categories`, `get_series`, `get_series_info`, `get_short_epg`.

Stream URL construction:
- Live: `{base_url}/live/{u}/{p}/{stream_id}.ts`
- VOD: `{base_url}/movie/{u}/{p}/{stream_id}.{container_extension}`
- Series: `{base_url}/series/{u}/{p}/{episode_id}.{container_extension}`

Real-world behaviour to handle:
- Panels return `200 OK` with an HTML error page or `{"user_info":{"auth":0}}` instead of a proper status code. Validate the payload shape, not the status code.
- `get_vod_streams` on a large panel can return 40MB of JSON. Use `JsonSerializer.DeserializeAsyncEnumerable` over the response stream; do not buffer the whole body.
- Fields arrive as strings that should be numbers, and vice versa, inconsistently between panels. Write tolerant converters that accept both.
- Respect `max_connections` from account info. Exceeding it gets the account temporarily blocked, which users will blame on your app.

Use one `SocketsHttpHandler` for the app lifetime with `PooledConnectionLifetime = TimeSpan.FromMinutes(5)`. Set a realistic `User-Agent`; some panels reject the .NET default.

### M3U parser

Forward-only line reader over the response stream. Never `ReadToEnd`. Parse `#EXTINF` attributes (`tvg-id`, `tvg-name`, `tvg-logo`, `group-title`, `catchup`, `catchup-source`, `catchup-days`) with a span-based scanner rather than regex — a 50k-entry playlist is where naive regex parsing costs seconds.

### Stable identity for M3U entries

`provider_stream_id` is straightforward for Xtream and a trap for M3U. `tvg-id` is routinely empty and routinely duplicated within a single playlist, so using it directly both violates `UNIQUE(provider_id, provider_stream_id, kind)` and makes identity churn on every refresh — which silently destroys favourites and sort order.

Derivation, in priority order:
1. `tvg-id` when present, non-empty, **and** unique within the playlist.
2. Otherwise a synthetic id: a stable hash of the stream URL with the credential segment removed, since providers rotate credentials in URLs but keep the path.
3. If the URL is also unstable, hash `normalized_title` + `group-title`.

Log which strategy each provider fell back to. A provider on strategy 3 will produce lower-quality dedup, and the diagnostics view should say so.

### Provider re-sync semantics

Sync is a **merge, never a replace**. A refresh must not delete and recreate rows, because everything the user owns is attached to those keys.

- Upsert on `UNIQUE(provider_id, provider_stream_id, kind)`.
- Streams absent from the new payload are marked `is_active = 0` rather than deleted, and hard-deleted only once `last_seen_utc` is more than 30 days old. Providers routinely drop channels for a few hours and bring them back.
- `channels`, `epg_map`, `playback_state` are never touched by a provider sync except to add rows for newly-seen `channel_key` values.
- Report a sync summary: added, updated, deactivated, reactivated. Surface it in the Providers view.

### Channel identity (cross-provider dedup)

`channel_key` derivation, in priority order:
1. If `tvg_id` present and non-empty → `tvg:{lowercased tvg_id}`
2. Otherwise → `name:{normalized_title}`

Normalization for `normalized_title`: lowercase, strip diacritics, remove quality markers (`HD`, `FHD`, `UHD`, `4K`, `SD`, `H265`, `HEVC`, `RAW`, `[...]`, `(...)`), strip country prefixes when they duplicate a group (`US:`, `UK|`, `CA -`), collapse whitespace, remove non-alphanumerics.

Keep quality as a separate parsed field in `streams.quality` — do not discard it. Users want to prefer the UHD copy while still failing over to the HD one.

Capture the stripped country prefix into `channels.country` rather than throwing it away. Normalization deliberately collapses `US: ESPN` and `UK| ESPN` onto the same key, and Phase 8 needs the country to avoid failing over between two genuinely different channels.

**Exit criteria**
- `Iptv.Harness` syncs a real Xtream account end to end and prints counts and elapsed time per stage.
- A 50k-line M3U parses in under 1.5s and allocates under 100MB peak.
- Two providers with overlapping channels produce a merged channel list with correct dedup, verified by a unit test with fixture data.
- Re-sync test: sync a fixture, favourite a channel, sync a mutated fixture with entries removed and reordered, assert the favourite survives and removals are deactivated rather than deleted.

---

## 6. Phase 3 — EPG ingest

This is the phase most likely to be built badly. The requirement is a 120MB XMLTV file parsed and indexed fast enough that a background refresh is invisible to the user.

### Rules

- `XmlReader` with `Async = true`, `IgnoreWhitespace = true`. Never `XDocument`, `XmlDocument`, or `XmlSerializer` over the whole document.
- `DtdProcessing = DtdProcessing.Ignore` with `XmlResolver = null`. **Not `Prohibit`** — this document originally said `Prohibit`, and that was wrong: real XMLTV opens with `<!DOCTYPE tv SYSTEM "xmltv.dtd">`, and `Prohibit` throws on the declaration itself, rejecting essentially every real guide. `Ignore` keeps both properties that mattered — the DTD is skipped rather than processed, so internal entities are never expanded (billion-laughs), and the null resolver means no external subset is ever fetched (XXE).
- Detect and stream-decompress `.gz` transparently (`GZipStream`), since most EPG URLs serve gzip.
- Parse XMLTV timestamps (`20260904183000 +0000`) with a hand-written parser. `DateTime.ParseExact` with multiple candidate formats in a hot loop is measurably slow at this volume. Store as unix seconds.
- Capture every `<channel>` element into `epg_channels`, including **all** `<display-name>` values, not just the first. Phase 4 matching depends on having them, and they are free to collect here.
- Producer/consumer over `Channel.CreateBounded<Programme>(10_000)` with `SingleReader = true, SingleWriter = true, FullMode = Wait`. The bound provides backpressure so the parser can't run far ahead of the writer and balloon memory.
- Consumer batches 5,000 rows per transaction using **one** prepared `SqliteCommand`, resetting parameter values per row rather than recreating the command.
- Ingest into a staging table, then swap. A failed or partial EPG download must never leave the user with a half-empty guide.
- Create indexes on the staging table **after** the bulk load completes, not before. Maintaining `ix_programmes_lookup` per row across millions of inserts is pure waste when you can build it once over sorted data.
- After the swap, rebuild `programmes_fts` — see "External-content FTS and the EPG table swap" in Phase 1.
- Delete programmes older than 24h and further out than the configured horizon (default 14 days) after each ingest.

### Timezone

XMLTV offsets are frequently wrong or absent. Store UTC, display local, and apply `providers.epg_offset_minutes` as a per-provider correction exposed in settings. Users will need it.

**Exit criteria**
- Harness ingests a 100MB+ XMLTV: **parse + insert + swap under 8s**, and **total wall-clock including index build and FTS rebuild under 15s**, with peak working set under 400MB. Report both numbers separately every run; a regression in one is diagnostically different from a regression in the other.
- **Time the download separately from the parse.** Measured against the reference provider, fetching 67.5MB takes 45–51s and varies between runs, while parsing it takes 1.3s. A combined figure measures the provider's upload capacity, not this application.
- Ingest runs concurrently with reads without blocking a simulated UI query loop (WAL verification).
- Cancellation mid-ingest leaves the previous EPG intact.
- An empty but well-formed guide is rejected rather than committed. A provider serving nothing is a provider-side failure, not an instruction to wipe the guide.

**Status: met.** Against the reference provider's real 67.5MB guide — parse + insert + swap **1.3s**, total including index and FTS rebuild **1.6s**, peak working set **202MB**, 168,578 programmes. Roughly 6x inside the parse budget.

---

## 7. Phase 4 — EPG channel mapping

The single most common complaint about every IPTV player on the market is an empty guide. Phase 3 gives you programmes; this phase is what makes them visible.

Treat coverage as a headline product metric, not an implementation detail.

### What measurement showed, and how this phase changed because of it

This phase was originally written on the assumption that empty guides are a *matching* problem: that provider `tvg-id` values fail to line up with XMLTV `<channel id>` values, and cleverer matching recovers the difference. Measured against a real provider and its real guide, that is mostly false.

| Measure | Channels | Share |
| --- | --- | --- |
| User's channels | 20,478 | 100% |
| Matched by exact `tvg_id` | 4,871 | 23.8% |
| Matched **and** the guide has programmes | 3,566 | 17.4% |
| **Ceiling for any matcher at all** | **3,599** | **17.6%** |

The guide simply does not carry programmes for most channels. Exact id matching already recovers **99.1% of everything achievable**; tiers 2–4 combined are worth at most 33 more channels.

Two consequences. **The bottleneck is guide completeness, not match quality** — no matcher can invent programmes that do not exist, so effort spent on fuzzy matching is effort largely wasted. And **coverage must be reported against what the guide can supply**, not against the whole library, or a correctly working app looks broken.

Build tier 1, the coverage metric, and the manual remap UI first. Tiers 2–4 stay specified because a different provider or a better guide changes the arithmetic entirely, but they are not this phase's centre of gravity and should not be built before the rest of the app works.

### Matching pipeline

Run after every EPG ingest and every provider sync. Rows in `epg_map` with `locked = 1` are never modified.

1. **Exact tvg_id.** `streams.tvg_id` equals `epg_channels.epg_channel_id`, case-insensitive. `method='tvg_id'`, confidence 1.0.
2. **Exact display name.** `normalized_title` equals any of `epg_channels.normalized_names`. `method='display_name'`, confidence 0.9.
3. **Normalized match with country agreement.** As above but after full normalization, and only when `channels.country` is absent on one side or agrees on both. `method='normalized'`, confidence 0.75.
4. **Fuzzy.** Token-set similarity over normalized names, accepted only above a threshold (start at 0.85) and only with country agreement. `method='fuzzy'`, confidence = the score.

Ambiguity rule: when a channel matches multiple EPG channels at the same tier, record no mapping. A wrong guide is worse than a missing one — users trust what the grid says and will miss the thing they wanted to watch.

### Coverage and manual remap

- Compute coverage after each run: matched channels / total visible channels, and the same broken down per provider. Store the run in `meta` and show it in Diagnostics.
- The Channels view shows an unmistakable "no guide data" state per channel, with a one-click "Map EPG…" action.
- The remap UI: a searchable list of unmapped channels on the left, candidate `epg_channels` on the right ranked by the same similarity score, with the top candidate preselected. Confirming writes `epg_map` with `method='manual'`, `locked=1`.
- Offer a bulk action for the common case where an entire provider is offset by a naming convention (a shared prefix or suffix).
- Manual mappings are user data. Include them in settings export and never discard them on provider sync, EPG refresh, or normalization migration.

### Multiple EPG sources

Raising the ceiling needs better data, which needs a source that is not the provider's own thin guide. A library therefore holds **EPG sources independently of providers**: a user can add a third-party XMLTV URL alongside, or instead of, whatever their provider serves.

- An EPG source is a URL plus a refresh interval, owned by the library rather than by a provider. `epg_channels.source_provider_id` already allows for the association; it becomes nullable in practice for user-added sources.
- Ingest merges sources rather than replacing across them. Two sources may both cover a channel; the mapping keeps the one with more programmes in the visible window, and ties go to the higher-priority source.
- Per-source coverage is reported, so a user can see that adding a source moved the number and decide whether to keep it.

This is a scope addition made after measurement, not part of the original design. It is here because without it the guide is capped at 17.6% on the reference provider and no amount of matching work changes that.

**Exit criteria**
- **Recovery against the ceiling**: of the channels the guide can actually serve, at least 95% are matched. Currently 99.1% with tier 1 alone. This replaces the original 85%-of-library target, which is unreachable against a real guide and measured guide completeness rather than the matcher.
- Coverage is reported both ways — as a fraction of the library and as a fraction of what the guide can supply — because only the second is a statement about this code.
- **Zero false positives**: no channel is ever shown a guide belonging to a different channel. A wrong guide is worse than a missing one; users trust the grid and will miss the thing they wanted to watch.
- Zero ambiguous auto-mappings written: a fixture with two channels matching one EPG entry produces no mapping, not an arbitrary one.
- The grid renders a guide that ends 2–3 days out without looking broken. The reference provider's guide spans 2.6 days, not the 14 the default horizon implies.
- A locked manual mapping survives an EPG refresh, a provider re-sync, and a normalization version bump.

---

## 8. Phase 5 — mpv interop

Build this as a standalone library validated by a console harness that opens a window and plays a stream, before any XAML exists. The presentation path here is whatever Phase 0.1 and 0.2 established — if those spikes contradict this section, the spikes win and this section gets amended.

### Binaries

Ship `libmpv-2.dll` from the official mpv Windows builds (shinchiro's mpv-winbuild releases). Do not compile mpv. Place it in the app's output directory and load it explicitly with `NativeLibrary.SetDllImportResolver` so the load path is deterministic.

### P/Invoke

Use `[LibraryImport]` source generators, not `[DllImport]`:

```csharp
[LibraryImport("libmpv-2.dll", StringMarshalling = StringMarshalling.Utf8)]
internal static partial int mpv_set_option_string(IntPtr ctx, string name, string data);
```

Required surface:
- Client: `mpv_create`, `mpv_initialize`, `mpv_terminate_destroy`, `mpv_command`, `mpv_command_async`, `mpv_set_option_string`, `mpv_set_property_string`, `mpv_get_property`, `mpv_observe_property`, `mpv_wait_event`, `mpv_wakeup`, `mpv_request_log_messages`
- Render: `mpv_render_context_create`, `mpv_render_context_render`, `mpv_render_context_set_update_callback`, `mpv_render_context_update`, `mpv_render_context_free`

### Video presentation — the critical constraint

**Do not use `--wid` HWND embedding.** It creates an airspace violation: the video HWND renders above all XAML, so channel overlays, the EPG panel, and context menus cannot draw on top of the video. This is not fixable with z-order, and it is not an acceptable escape hatch when the render path is being difficult.

The presentation target is a **`SwapChainPanel`** via `ISwapChainPanelNative.SetSwapChain`, so that XAML composites correctly above and below the video. What feeds that swap chain depends on Phase 0.1:

- **Expected primary path — OpenGL render API over ANGLE.** `mpv_render_context_create` with `MPV_RENDER_API_TYPE_OPENGL`, an ANGLE EGL context whose backing device is D3D11, rendering to a texture shared with the swap chain. ANGLE ships with the Windows App SDK, so this adds no new redistributable.
- **There is no D3D11 render API to prefer.** Spike 0.1 confirmed `render.h` offers only `opengl` and `sw`. Re-check on a libmpv upgrade; if one ever appears it removes a translation layer and is worth adopting.
- **Debug-only fallback — `MPV_RENDER_API_TYPE_SW`** into a `WriteableBitmap`. Acceptable for diagnosing a broken GPU path. Never shipped as the default; it burns CPU and will not hold 1080i.

Keep the presentation layer behind an interface (`IVideoPresenter`) with the swap-chain plumbing on one side and mpv on the other, so the backend decision stays swappable if a future mpv release changes what is offered.

### Threading

`mpv_wait_event` blocks on a thread you own — run one dedicated long-running thread per handle, not a thread-pool task. The render update callback fires on **mpv's** thread. Neither may touch XAML.

Marshal everything out through a `Channel<MpvEvent>` exposed by `Iptv.Mpv`. The ViewModel layer consumes it and hops to the UI thread once via `DispatcherQueue.TryEnqueue`. Do not scatter dispatcher calls through the interop layer.

### Options for live TS

```
hwdec=auto-safe
vo=libmpv
keep-open=yes
idle=yes
profile=low-latency          # live only
cache=yes
cache-pause-initial=no       # live only; measured 11% faster to first frame
demuxer-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=2
demuxer-max-bytes=32MiB      # live; raise substantially for VOD
demuxer-readahead-secs=2     # live; 20+ for VOD
deinterlace=auto
```

Apply a different option set for VOD — large readahead, no low-latency profile, seeking enabled. Switching profiles per content kind is a real quality difference.

**Do not add aggressive `probesize` / `analyzeduration` overrides.** They are the obvious tuning knob and they were measured: `probesize` of 250KB, 1MB and FFmpeg's 5MB default all produced the same open time, because that time is network round-trips and keyframe wait rather than probing. A smaller probe buys nothing and risks FFmpeg mis-detecting a stream, trading a reliable second for an occasional failure. See [0008](docs/decisions/0008-channel-change-latency.md).

After the first frame, read `hwdec-current` and log it.

**Treat any value other than absent or `no` as hardware decoding.** An earlier version of this document said to accept `d3d11va` or `d3d11va-copy` specifically. That is too narrow and was wrong in practice: with `hwdec=auto-safe`, mpv picks the best backend for the adapter, and on an NVIDIA card that is **`nvdec`**, not `d3d11va`. Measured against a real stream, matching on `d3d11` alone reported working hardware decode as software — which would have sent someone optimising a non-problem. Expect `nvdec`, `cuda`, `d3d11va`, `dxva2`, `vulkan` and their `-copy` variants.

A `-copy` suffix still means hardware decode, but frames cross system memory on the way back. Worth showing in Diagnostics, not worth failing over.

Software decode on 1080i presents to users as "the app is slow" — surface it rather than letting it pass silently. This is not hypothetical: across five identical runs against the same stream, four reported `nvdec` and one reported `no`. Hardware decode is **not deterministic**, so the app must show the live value rather than assume the answer it got at startup still holds.

### Property observation

Observe at minimum: `pause`, `time-pos`, `duration`, `demuxer-cache-time`, `cache-buffering-state`, `video-params`, `hwdec-current`, `eof-reached`, `core-idle`. Route mpv log messages at `warn` into Serilog.

**Exit criteria**
- Harness plays a raw MPEG-TS live stream and an MP4 VOD file in the same session.
- Video composites correctly under a semi-transparent XAML overlay.
- `hwdec-current` resolves to a hardware decoder — anything other than absent or `no` — on Intel, NVIDIA, and AMD test machines, and which one is logged per machine. **Partially met:** verified on NVIDIA (RTX 5080, `nvdec`, 4 runs of 5). Intel and AMD remain untested, as does the software fallback on a machine with no usable driver.
- No crash after 200 sequential `loadfile` calls (leak check on render context lifetime).

---

## 9. Phase 6 — Vertical slice

Stop and build something usable. One provider, the channel list, and playback. No EPG grid, no failover, no dedup UI, no VOD, no settings beyond what is needed to add one provider.

Everything before this point is validated by harnesses, which are excellent at proving throughput and useless at exposing the problems that only appear with real providers and a real person operating the app. Ship this to yourself and use it as your TV for a week before starting the expensive UI work.

Scope:
- Add one provider, sync, ingest its EPG.
- Virtualized channel list with logo and now/next from the Phase 4 mapping.
- Play, stop, volume, fullscreen. Overlay with channel name and current programme.
- Diagnostics showing EPG coverage, `hwdec-current`, and time-to-first-frame.

What a week of use is meant to surface, and what a harness cannot:
- Whether the Phase 4 coverage figure holds against *your* providers, and whether the failures are systematic (fixable in matching) or random (needing the manual UI).
- Whether channel-change latency is dominated by connection setup, demuxer probing, or decode — which decides how much of Phase 7 is worth building.
- Whether compositing survives real GPU drivers, sleep/resume, display change, and multi-monitor.

**Exit criteria**
- Used as a primary TV app for five consecutive days.
- A written list of what actually hurt, and Phases 7–9 amended in response.
- Baseline cold-start channel-change latency recorded, broken down into connect / first-packet / first-frame.

---

## 10. Phase 7 — Fast channel change

Sub-second channel change has a cheap half and an expensive half. Do the cheap half first and measure before building the expensive half.

### Step 1 — tune the cold path

This helps **every** channel change, including the ones no predictor anticipates, and costs no extra provider connection. Using the Phase 6 baseline, tune and measure individually:

- `demuxer-lavf-o=probesize=...,analyzeduration=...` — the single biggest lever on TS streams. mpv's defaults are tuned for correctness on unknown files, not for a stream whose codec you already know from a previous tune-in. Cache per-channel codec parameters and reuse them.
- `cache-pause-initial=no`, minimal `demuxer-readahead-secs` for live.
- Connection reuse to the provider host across channel changes.

Record the p50 after tuning. If it is already near target, the dual-handle work below is optional complexity and should be reconsidered.

### Step 2 — dual handles

Two `mpv_handle` instances, each with its own render context. One is **active** and presenting; one is **warm** and prebuffering a predicted next channel.

Channel change sequence:
1. User selects a channel.
2. If the warm handle is already loaded with it → present the warm context, swap roles, unpause. This is the sub-second path.
3. Otherwise → issue `loadfile` on the warm handle, wait for first video frame, then present it.
4. After every change, issue `loadfile` + `pause` on the now-idle handle for the new prediction.

Both handles need `idle=yes` and `keep-open=yes` so a stream ending doesn't tear down an instance you're about to reuse.

### Presentation swap mechanics

A `SwapChainPanel` holds one swap chain, so "retarget the panel" is not free — calling `SetSwapChain` mid-playback will show a black frame or a tear, which defeats the point of the whole feature. Pick one:

- **Two stacked `SwapChainPanel`s**, one per handle, swapped by opacity or visibility once the incoming handle reports its first frame. Simplest, costs one extra panel.
- **One presentation swap chain** that both render contexts blit into, with the compositor choosing the source. Cleaner, more interop work.

Prototype both in the harness and measure the visible transition, not just the time to first frame.

### Prediction

Prediction v1 is **recency and favourites**, not "next channel in sort order". Sequential surfing is a minority of channel changes in practice; most arrive via favourites, number entry, or the EPG. Rank candidates by: last-watched channel (for back-and-forth flipping, the single most common pattern), then most-watched favourites, then sort-order neighbours.

Provider connection limits matter here — two live handles consume two connections, two streams of bandwidth, and two decodes. Make prebuffering a setting, default on, and disable it automatically when `max_connections` is 1.

**Exit criteria**
- Cold-path p50 recorded before and after Step 1 tuning, both logged.
- Measured p50 channel change logged by the harness over 50 changes that follow a realistic mix of favourites, number entry, and sequential steps — not 50 sequential steps, which flatters the predictor. **The target depends on the account, and a single number cannot describe both cases honestly:**
  - **Multi-connection account: p50 under 800ms, p95 under 2s.** The warm handle has already paid the connect-and-probe cost before the user presses anything, so this is the case the 800ms figure was always about.
  - **Single-connection account: p50 under 1.2s.** Prebuffering is unavailable, and measurement shows this provider takes **777–882ms just to accept a connection and produce a decodable keyframe** — before this application does anything. That is roughly the entire 800ms budget, it does not move when `probesize` is varied twenty-fold, and no client option shortens a network round-trip or the wait for the next keyframe. See [0008](docs/decisions/0008-channel-change-latency.md).
- No visible black frame or tear during the presentation swap.
- Prebuffering disabled → app still works correctly, just slower.

---

## 11. Phase 8 — Failover and health

When a stream fails, try the next candidate for the same `channel_key` before showing any error to the user.

Failure detection:
- Connection or HTTP error on `loadfile`
- No first video frame within 4s
- `core-idle` true with `cache-buffering-state` stalled for more than 5s during playback
- Decode error events

These thresholds are deliberately tighter than they look. They are additive in the worst case — a slow first attempt followed by a stall means the user has already waited a long time before anything visible happens — so start the failover indicator immediately on detection rather than after the switch succeeds.

Candidate ordering: provider `priority`, then rolling 7-day success rate from `stream_health`, then quality preference (user setting: prefer highest / prefer stable).

### Failover safety guard

Normalization deliberately collapses `US: ESPN` and `UK| ESPN` onto one `channel_key`, which means naive failover can silently play a completely different channel. That is worse than an error, because the user believes what the UI tells them.

A candidate is only eligible when:
- the country agrees, or is absent on both sides, **and**
- the `channel_key` was derived from `tvg_id`, or the candidate's `normalized_title` matches exactly.

Fuzzy-derived keys are good enough for grouping in the UI and not good enough for silently substituting a stream. Candidates failing the guard are excluded and the exclusion is logged.

**Amended after measurement** — see [0009](docs/decisions/0009-failover-guard-cost.md).

The country comparison is against **the stream that will actually be opened**, not against `channels.country`. That column is one denormalized value for a key that may span several differently-titled streams, which is the exact case the guard is for; the anchor candidate's own country prefix is the accurate question. Where the two agree the result is identical.

Measured on the reference library: the guard refuses 45.8% of alternative streams, all on country, and every refusal sampled was a genuinely different channel (Afghan vs Georgian 1TV; Ukrainian vs Russian 5 Kanal). 53.9% survive, so failover keeps something to work with across 3,625 channels. The title rule fires 19 times in 6,563 — rare, and exactly the `ESPN 2` / `ESPN2` collisions that are invisible until they happen.

Failover must **wait** for a provider connection slot rather than being refused one. A stream that fails 19ms after opening is always inside the minimum interval between opens, so the refusing path would show an error while a working alternative sat unused. A user clicking a channel still gets the refusing path: silently waiting on a click reads as a dead button.

Show a subtle non-blocking indicator when a failover occurs ("switched to Provider B"). Only surface a hard error after all candidates are exhausted.

Write every attempt to `stream_health`, subject to the Phase 1 retention policy. Surface it in a diagnostics view: per-channel success rate, per-provider reliability, average time-to-first-frame. This data is genuinely useful and nothing on the market has it.

**Exit criteria**
- Killing a stream mid-playback (block the host in the firewall) results in automatic recovery on another provider within 10s.
- A fixture with two same-named channels from different countries produces no failover between them.
- Diagnostics view shows accurate per-provider stats after a synthetic run.

Status: the second is covered by unit tests. The third is met — the diagnostics panel shows
per-provider channel counts, success rate and average time to first frame, and
`dotnet run --project src/Iptv.Harness -- diagnostics` prints the same view model headlessly
so the numbers can be checked without reading them off a screenshot. Against the reference
library it reports `71% of 94 attempts · 1027ms to first frame`.

That output also corroborated the stall bug from the other direction: 14 recorded stalls
averaging **19,831ms**, which is the frame-based detector waiting out mpv's buffer, measured
from real use rather than from a drill.

The first is now measured against a real mid-stream cut by
`dotnet run --project src/Iptv.Harness -- killswitch`, which plays a real channel through a
local TCP forwarder and severs it — the same failure a firewall rule produces, without
needing elevation. **It failed.** Recovery took 8.3s, 12.1s and 19.0s on three runs, scaling
with how much mpv had buffered, because the frame-based detector could not start counting
until a 32MiB buffer had played out. Detection now watches the buffer draining at real time
and lands at 5.16s / 5.20s / 6.0s, constant regardless of buffer depth. See
[0012](docs/decisions/0012-stall-detection.md).

Still unproved: **cross-provider** failover, which the criterion also asks for. One account
is configured, and `harness probe` confirms the second host on record does not resolve — so
this needs a second subscription and nothing in the build can substitute for it. What is
proved is detection and recovery inside the budget against a genuine cut, failing over to a
second candidate for the same channel.

---

## 12. Phase 9 — UI

WinUI 3, MVVM via `CommunityToolkit.Mvvm`. Use `ObservableProperty` and `RelayCommand` source generators.

### Views

- **Player** — SwapChainPanel, auto-hiding overlay with now/next, transport controls, channel number entry, audio/subtitle track pickers.
- **Channels** — category sidebar, virtualized channel list with logo, now-playing programme, progress bar per row, and an explicit "no guide data" state linking to the EPG mapping UI.
- **EPG grid** — the hard one, see below.
- **VOD / Series** — poster grid, detail view with TMDB metadata, season/episode tree, and a continue-watching row driven by `playback_state`.
- **Search** — unified across channels, VOD, series, programmes.
- **Providers** — add/edit/test/sync, connection limit display, sync status and last sync summary (added / updated / deactivated).
- **EPG mapping** — unmapped channels, ranked candidates, bulk prefix/suffix rules, coverage figure. Specified in Phase 4.
- **Diagnostics** — stream health, EPG coverage, decode info (including `hwdec-current` and whether it is a copy path), ingest timings, log viewer.
- **Settings** — playback profiles, buffering, EPG horizon and per-provider offset, prebuffering toggle, theme.

First run: the app opens with no providers and must say so usefully — an add-provider flow with a Test button that validates credentials, reports `max_connections`, and reports whether an EPG URL was found and how many channels it maps. Test is the user's first impression of whether the app works; make it explain, not just pass or fail.

### EPG grid requirements

Virtualize on **both axes**. Do not render the full timeline. Implement as a custom `Panel` with explicit realization windows:
- Vertical: realize only visible channel rows plus a small buffer.
- Horizontal: realize only programme blocks intersecting the visible time window.
- Query the database per visible window (`WHERE epg_channel_id IN (...) AND stop_utc > @from AND start_utc < @to`), not by loading all programmes into memory.
- Recycle block containers. Allocation per scroll frame must be near zero.

Target: smooth scrolling at monitor refresh rate with 20k channels and the whole stored guide loaded.

**Amended after measurement** — see [0010](docs/decisions/0010-guide-horizon.md). The original target said "a 14-day guide", which was an assumption about providers rather than a measurement of one. The reference provider publishes roughly 2.7 days centred on the moment it is fetched, reaching about 24 hours ahead. The grid therefore bounds itself to `min(start_utc)`/`max(stop_utc)` rather than to a fixed horizon: offering a fortnight of columns would be offering thirteen days of blank.

The guide is dense where it exists — 3,341 of 20,479 channels have a programme on air at any given moment, holding within 2.5% out to twelve hours — so the grid is worth building. The windowed query costs 3ms for a 60-channel, 2-hour rectangle against 164,661 programmes, so the horizontal window is re-read on scroll rather than cached.

**Automatic EPG refresh is required, not optional.** A guide reaching 24 hours ahead is empty for anyone who opens the app two days after a sync. Measured on a two-day-old guide: 10 channels on air, falling to 0 within twelve hours, while coverage still reported 16.8% — because coverage counts channels with *any* guide and says nothing about whether it covers the time anyone is looking at. Report both numbers together.

### Lists

`ItemsRepeater` with `IncrementalLoadingCollection` for channel and VOD lists. Standard `ListView` will not hold up at 50k items.

### Input

Full keyboard control is a differentiator: arrow navigation, number entry for direct channel access, space/K for pause, F for fullscreen, `/` for search, PageUp/PageDown for channel stepping, I for info overlay. Also handle media keys and remote-style input, since many Windows HTPC users have an IR remote presenting as a keyboard.

**Exit criteria**
- EPG grid holds 60fps scroll with 20k channels in a release build.
- Search returns results within 80ms of keystroke on a 50k-item library.
- Entire app navigable by keyboard only.

---

## 13. Phase 10 — Packaging

- **Velopack** for installer and delta auto-update. It is the maintained successor to Squirrel and is substantially less friction than MSIX for self-distribution.
- Code signing certificate is required, not optional. Unsigned installers trigger SmartScreen warnings that will stop most users cold. Note that OV and EV code signing certificates both now require hardware or HSM key storage, so a token is a baseline requirement rather than an upgrade; EV builds SmartScreen reputation faster.
- Publish self-contained per-RID with `PublishReadyToRun=true`: `win-x64` and `win-arm64`, each carrying the matching `libmpv-2.dll`. Shipping the x64 binary in an ARM64 package fails at load time with an error that reads as a missing dependency rather than an architecture mismatch.
- Expect roughly **337MB installed and 133MB downloaded**. This corrects an earlier estimate of 120–180MB, which counted libmpv and the app but not the two runtimes underneath them: a self-contained .NET 10 publish plus `WindowsAppSDKSelfContained` account for most of the difference, and ReadyToRun adds precompiled native code on top of the IL it does not replace. `WindowsAppSDKSelfContained` is not optional — without it the installed app also needs the Windows App SDK runtime, and someone unzipping a portable folder has no installer to pull it in. Measured in [0011](docs/decisions/0011-packaging.md).
- Delta updates make the size tolerable: one build to the next moved **176KB**, patching 8 files of 518.
- Ship an unpackaged build too — HTPC users often want a portable folder.
- Build with `scripts/publish.ps1`; fetch the native binaries first with `scripts/fetch-libmpv.ps1 -Rid both`. The publish script reads the PE machine field of both the executable and `libmpv-2.dll` and refuses to package a mismatch, per the failure Phase 0.3 identified.

---

## 14. Security and privacy

- Provider credentials encrypted at rest with DPAPI (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`). Never plaintext in the database or settings file.
- Redact credentials from all logs. Stream URLs contain username and password in the path — write a scrubbing formatter and apply it to Serilog output and to any diagnostics export. This includes the synthetic-id hashing in Phase 2: hash the credential-stripped URL, never log the raw one.
- No telemetry to any server. All health data stays local.
- TMDB enrichment is opt-in and requires a user-supplied API key. Display the TMDB attribution their API terms require wherever TMDB metadata is shown.

---

## 15. Conventions for the coding agent

Write these into `CLAUDE.md` at the repository root — Claude Code reads it from there, not from `docs/`.

- .NET 10, latest C#, nullable enabled, `TreatWarningsAsErrors` on. Scope it per-project; WinUI 3 generated code will need exclusions and that is expected.
- `async`/`await` throughout the IO paths; no `.Result` or `.Wait()` anywhere.
- All long-running operations take a `CancellationToken`.
- No `System.Text.RegularExpressions` in any parse loop that runs per-line or per-record. Span-based scanning instead.
- No LINQ in per-frame or per-row hot paths.
- One prepared command reused for bulk inserts; never string-concatenated SQL.
- xUnit for tests, fixture files under `tests/Fixtures/` for M3U and XMLTV samples.
- BenchmarkDotNet for the parser benchmarks; keep the results committed so regressions are visible.

**Standing prohibitions:**
- Do not add bundled playlists, sample providers, or any content source.
- Do not use `--wid` for video embedding, including as a temporary workaround.
- Do not load full XMLTV or VOD JSON payloads into memory.
- Do not reference UI packages from `Iptv.Core`.
- Do not touch XAML objects from mpv callback threads.
- Do not delete user-owned rows (`channels`, `epg_map`, `playback_state`) during a provider sync.
- Do not write an EPG mapping when the match is ambiguous.

---

## 16. Suggested build order for Claude Code

1. Phase 0 spikes: render API inventory, compositing, ARM64, ingest throughput. Amend this document with the results.
2. Solution scaffold, project references, `CLAUDE.md`, CI build.
3. SQLite schema, migrations, pragmas, FTS5 setup + tests.
4. Xtream client + M3U parser + stable identity + `Iptv.Harness` timing output.
5. Channel identity, normalization, cross-provider merge, re-sync semantics + tests.
6. XMLTV pull-parser and ingest pipeline; benchmark to the Phase 3 targets.
7. EPG channel matching, coverage metric, ambiguity rules.
8. `Iptv.Mpv` P/Invoke layer; console playback smoke test.
9. Render context → SwapChainPanel using the Phase 0 path; verify overlay compositing.
10. **Vertical slice.** One provider, channel list, playback. Use it for a week. Amend the remaining phases.
11. Cold-path latency tuning, then dual-handle player service and channel-change measurement.
12. Failover policy, safety guard, and `stream_health` recording.
13. WinUI shell, navigation, player view, first-run flow.
14. Channel list and category navigation.
15. EPG grid with 2D virtualization, EPG mapping UI.
16. VOD/series browsing, continue-watching, TMDB enrichment.
17. Search, settings, diagnostics.
18. Velopack packaging, signing, update channel.

Phases 0, 4, and 7 are where this project is won or lost. Phase 0 tells you whether the architecture is real, Phase 4 is the difference between a guide and an empty grid, and Phase 7 is the headline feature. If those are right, the UI is ordinary work.
