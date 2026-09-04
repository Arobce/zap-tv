# PRD — Windows IPTV Player

**Target agent:** Claude Code
**Platform:** Windows 10 1809+ / Windows 11, x64 and ARM64
**Stack:** .NET 9, C#, WinUI 3 (Windows App SDK), libmpv render API, SQLite + FTS5

---

## 0. How to use this document

Build in the phase order given. Each phase ends with an **Exit criteria** block — do not start the next phase until every item passes. Phases 1–3 have no UI; they are validated by a console harness and tests. This is deliberate. The riskiest parts of this project are EPG ingest performance and mpv interop, and both are far cheaper to get wrong in a console app than inside a XAML view.

When a decision in this document conflicts with a plausible-looking tutorial or Stack Overflow answer, follow this document. Several of the constraints below exist specifically because the obvious approach fails.

---

## 1. Product summary

A desktop IPTV player that connects to user-supplied providers (Xtream Codes API or M3U/XMLTV URLs) and plays live TV, VOD, and series.

The app ships no content, no bundled playlists, no default provider, and no channel discovery. It is bring-your-own-source only. Do not add a "free channels" list, sample playlist, or provider directory at any point.

### What makes it better than existing players

These four items are the product. Everything else is table stakes.

1. **Sub-second channel change.** Existing players take 3–6 seconds. Target p50 under 800ms via a warm second decoder.
2. **Multi-provider merge with automatic failover.** Users commonly hold 2–3 subscriptions. Deduplicate channels across them, and when a stream stalls, transparently switch to another provider's copy of the same channel.
3. **A genuinely fast EPG grid.** 2D-virtualized, scrolls at refresh rate with 20k channels loaded.
4. **Instant search.** FTS5 across channels, VOD, series, and programme titles. Results as you type, no debounce longer than 80ms.

### Explicit non-goals for v1

Recording/DVR, transcoding, casting, mobile or TV builds, account sync, plugin system.

---

## 2. Solution structure

```
Iptv.sln
  src/
    Iptv.Core/            net9.0 — no UI package references, ever
      Xtream/             player_api.php client + DTOs
      Playlists/          M3U/M3U8 streaming parser
      Epg/                XMLTV pull-parser, ingest pipeline
      Data/               SQLite context, migrations, repositories
      Sources/            provider merge, channel identity, failover policy
      Metadata/           TMDB enrichment (optional feature)
      Models/             domain records
    Iptv.Mpv/             net9.0-windows — P/Invoke, render context, D3D11 interop
    Iptv.App/             WinUI 3, MVVM, packaged
    Iptv.Harness/         console app for timing ingest and playback smoke tests
  tests/
    Iptv.Core.Tests/
    Iptv.Mpv.Tests/
  docs/
    CLAUDE.md
```

`Iptv.Core` must not reference `Microsoft.WindowsAppSDK`, `Microsoft.UI.Xaml`, or `CommunityToolkit.Mvvm`. Enforce this with a build check. If core logic needs to notify the UI, it exposes `IAsyncEnumerable<T>`, `Channel<T>`, or plain events — never `DispatcherQueue`.

### Key NuGet packages

- `Microsoft.WindowsAppSDK` (WinUI 3)
- `CommunityToolkit.Mvvm` (App layer only)
- `CommunityToolkit.WinUI.Collections` — `IncrementalLoadingCollection`
- `Microsoft.Data.Sqlite` — must be the SQLitePCLRaw bundle with FTS5 enabled (`SQLitePCLRaw.bundle_e_sqlite3`)
- `System.Threading.Channels`
- `Serilog` + `Serilog.Sinks.File`
- `Velopack` (packaging/update, Phase 8)

Use raw ADO.NET via `Microsoft.Data.Sqlite` for the bulk ingest path. EF Core is acceptable for CRUD on settings and providers but must not be used for programme inserts.

---

## 3. Phase 1 — Data layer and schema

### Schema

```sql
CREATE TABLE providers (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,              -- 'xtream' | 'm3u'
  base_url TEXT NOT NULL,
  username TEXT, password TEXT,    -- DPAPI-encrypted blob, see Security
  epg_url TEXT,
  priority INTEGER NOT NULL DEFAULT 0,   -- lower wins during failover
  max_connections INTEGER,
  enabled INTEGER NOT NULL DEFAULT 1,
  last_sync_utc TEXT
);

CREATE TABLE streams (
  id INTEGER PRIMARY KEY,
  provider_id INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  provider_stream_id TEXT NOT NULL,      -- Xtream stream_id or M3U tvg-id
  kind TEXT NOT NULL,                    -- 'live' | 'vod' | 'series_episode'
  title TEXT NOT NULL,
  normalized_title TEXT NOT NULL,        -- see Channel identity
  tvg_id TEXT,
  logo_url TEXT,
  category_id TEXT,
  url TEXT NOT NULL,
  container TEXT,                        -- 'ts' | 'm3u8' | 'mp4' | 'mkv'
  channel_key TEXT NOT NULL,             -- cross-provider identity
  UNIQUE(provider_id, provider_stream_id, kind)
);
CREATE INDEX ix_streams_channel_key ON streams(channel_key);
CREATE INDEX ix_streams_kind_category ON streams(kind, category_id);

CREATE TABLE channels (              -- merged logical channel across providers
  channel_key TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  logo_url TEXT,
  epg_channel_id TEXT,
  user_sort_order INTEGER,
  is_favorite INTEGER NOT NULL DEFAULT 0,
  is_hidden INTEGER NOT NULL DEFAULT 0
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
  title, description, content='programmes', content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);
```

Keep FTS tables in sync with triggers on insert/update/delete, but **drop the triggers before bulk ingest and rebuild the index afterward** with `INSERT INTO streams_fts(streams_fts) VALUES('rebuild')`. Trigger-per-row on a 50k insert is a large fraction of ingest time.

### Connection configuration

Open with these pragmas, in this order, on every connection:

```
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA mmap_size=268435456;
PRAGMA cache_size=-64000;
```

WAL matters because the EPG refresh writes while the UI reads. Without it, the grid stutters during background sync.

Store the database at `%LOCALAPPDATA%\IptvPlayer\library.db`. Logs beside it in `logs\`.

**Exit criteria**
- Migrations run from empty to current on a fresh machine.
- A test inserts 200k programme rows and queries "what is on channel X at time T" in under 5ms.
- FTS5 confirmed available at runtime; app fails loudly at startup if not.

---

## 4. Phase 2 — Provider ingest

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

Forward-only line reader over the response stream. Never `ReadToEnd`. Parse `#EXTINF` attributes (`tvg-id`, `tvg-name`, `tvg-logo`, `group-title`, `catchup`, `catchup-source`) with a span-based scanner rather than regex — a 50k-entry playlist is where naive regex parsing costs seconds.

### Channel identity (cross-provider dedup)

`channel_key` derivation, in priority order:
1. If `tvg_id` present and non-empty → `tvg:{lowercased tvg_id}`
2. Otherwise → `name:{normalized_title}`

Normalization for `normalized_title`: lowercase, strip diacritics, remove quality markers (`HD`, `FHD`, `UHD`, `4K`, `SD`, `H265`, `HEVC`, `RAW`, `[...]`, `(...)`), strip country prefixes when they duplicate a group (`US:`, `UK|`, `CA -`), collapse whitespace, remove non-alphanumerics.

Keep quality as a separate parsed field — do not discard it. Users want to prefer the UHD copy while still failing over to the HD one.

**Exit criteria**
- `Iptv.Harness` syncs a real Xtream account end to end and prints counts and elapsed time per stage.
- A 50k-line M3U parses in under 1.5s and allocates under 100MB peak.
- Two providers with overlapping channels produce a merged channel list with correct dedup, verified by a unit test with fixture data.

---

## 5. Phase 3 — EPG ingest

This is the phase most likely to be built badly. The requirement is a 120MB XMLTV file parsed and indexed in **under 8 seconds** on a mid-range machine.

### Rules

- `XmlReader` with `Async = true`, `DtdProcessing = DtdProcessing.Prohibit`, `IgnoreWhitespace = true`. Never `XDocument`, `XmlDocument`, or `XmlSerializer` over the whole document.
- Detect and stream-decompress `.gz` transparently (`GZipStream`), since most EPG URLs serve gzip.
- Parse XMLTV timestamps (`20260904183000 +0000`) with a hand-written parser. `DateTime.ParseExact` with multiple candidate formats in a hot loop is measurably slow at this volume. Store as unix seconds.
- Producer/consumer over `Channel.CreateBounded<Programme>(10_000)` with `SingleReader = true, SingleWriter = true, FullMode = Wait`. The bound provides backpressure so the parser can't run far ahead of the writer and balloon memory.
- Consumer batches 5,000 rows per transaction using **one** prepared `SqliteCommand`, resetting parameter values per row rather than recreating the command.
- Ingest into a staging table, then swap. A failed or partial EPG download must never leave the user with a half-empty guide.
- Delete programmes older than 24h and further out than the configured horizon (default 14 days) after each ingest.

### Timezone

XMLTV offsets are frequently wrong or absent. Store UTC, display local, and expose a per-provider offset correction in settings. Users will need it.

**Exit criteria**
- Harness ingests a 100MB+ XMLTV in under 8s with peak working set under 400MB.
- Ingest runs concurrently with reads without blocking a simulated UI query loop (WAL verification).
- Cancellation mid-ingest leaves the previous EPG intact.

---

## 6. Phase 4 — mpv interop

Build this as a standalone library validated by a console harness that opens a window and plays a stream, before any XAML exists.

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

**Do not use `--wid` HWND embedding.** It creates an airspace violation: the video HWND renders above all XAML, so channel overlays, the EPG panel, and context menus cannot draw on top of the video. This is not fixable with z-order.

Instead: create the mpv render context, render into a D3D11 texture, and present it through a **`SwapChainPanel`** using `ISwapChainPanelNative.SetSwapChain`. XAML composites correctly above and below the panel.

If the D3D11 render API path proves unstable in your mpv build, the fallback order is (a) OpenGL render API via ANGLE into a shared surface, then (b) `sw` render API into a `WriteableBitmap` for a debug-only path. Do not fall back to `--wid`.

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
demuxer-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_delay_max=2
demuxer-max-bytes=32MiB      # live; raise substantially for VOD
demuxer-readahead-secs=2     # live; 20+ for VOD
deinterlace=auto
```

Apply a different option set for VOD — large readahead, no low-latency profile, seeking enabled. Switching profiles per content kind is a real quality difference.

After the first frame, read `hwdec-current` and log it. Silent fallback to software decode on 1080i content is a common failure that presents as "the app is slow."

### Property observation

Observe at minimum: `pause`, `time-pos`, `duration`, `demuxer-cache-time`, `cache-buffering-state`, `video-params`, `hwdec-current`, `eof-reached`, `core-idle`. Route mpv log messages at `warn` into Serilog.

**Exit criteria**
- Harness plays a raw MPEG-TS live stream and an MP4 VOD file in the same session.
- Video composites correctly under a semi-transparent XAML overlay.
- `hwdec-current` resolves to `d3d11va` on Intel, NVIDIA, and AMD test machines.
- No crash after 200 sequential `loadfile` calls (leak check on render context lifetime).

---

## 7. Phase 5 — Fast channel change

Two `mpv_handle` instances, each with its own render context. One is **active** and presenting; one is **warm** and prebuffering a predicted next channel.

Channel change sequence:
1. User selects a channel.
2. If the warm handle is already loaded with it → retarget the SwapChainPanel to the warm context, swap roles, unpause. This is the sub-second path.
3. Otherwise → issue `loadfile` on the warm handle, wait for first video frame, then retarget.
4. After every change, issue `loadfile` + `pause` on the now-idle handle for the new prediction.

Prediction v1: next channel in the current sort order. Prediction v2 (optional): most recently watched neighbour.

Both handles need `idle=yes` and `keep-open=yes` so a stream ending doesn't tear down an instance you're about to reuse.

Provider connection limits matter here — two live handles consume two connections. Make prebuffering a setting, default on, and disable it automatically when `max_connections` is 1.

**Exit criteria**
- Measured p50 channel change under 800ms and p95 under 2s on a working provider, logged by the harness over 50 changes.
- Prebuffering disabled → app still works correctly, just slower.

---

## 8. Phase 6 — Failover and health

When a stream fails, try the next candidate for the same `channel_key` before showing any error to the user.

Failure detection:
- Connection or HTTP error on `loadfile`
- No first video frame within 6s
- `core-idle` true with `cache-buffering-state` stalled for more than 8s during playback
- Decode error events

Candidate ordering: provider `priority`, then rolling 7-day success rate from `stream_health`, then quality preference (user setting: prefer highest / prefer stable).

Show a subtle non-blocking indicator when a failover occurs ("switched to Provider B"). Only surface a hard error after all candidates are exhausted.

Write every attempt to `stream_health`. Surface it in a diagnostics view: per-channel success rate, per-provider reliability, average time-to-first-frame. This data is genuinely useful and nothing on the market has it.

**Exit criteria**
- Killing a stream mid-playback (block the host in the firewall) results in automatic recovery on another provider within 10s.
- Diagnostics view shows accurate per-provider stats after a synthetic run.

---

## 9. Phase 7 — UI

WinUI 3, MVVM via `CommunityToolkit.Mvvm`. Use `ObservableProperty` and `RelayCommand` source generators.

### Views

- **Player** — SwapChainPanel, auto-hiding overlay with now/next, transport controls, channel number entry, audio/subtitle track pickers.
- **Channels** — category sidebar, virtualized channel list with logo, now-playing programme, and progress bar per row.
- **EPG grid** — the hard one, see below.
- **VOD / Series** — poster grid, detail view with TMDB metadata, season/episode tree.
- **Search** — unified across channels, VOD, series, programmes.
- **Providers** — add/edit/test/sync, connection limit display, sync status.
- **Diagnostics** — stream health, decode info, ingest timings, log viewer.
- **Settings** — playback profiles, buffering, EPG horizon and offset, prebuffering toggle, theme.

### EPG grid requirements

Virtualize on **both axes**. Do not render the full timeline. Implement as a custom `Panel` with explicit realization windows:
- Vertical: realize only visible channel rows plus a small buffer.
- Horizontal: realize only programme blocks intersecting the visible time window.
- Query the database per visible window (`WHERE epg_channel_id IN (...) AND stop_utc > @from AND start_utc < @to`), not by loading all programmes into memory.
- Recycle block containers. Allocation per scroll frame must be near zero.

Target: smooth scrolling at monitor refresh rate with 20k channels and a 14-day guide loaded.

### Lists

`ItemsRepeater` with `IncrementalLoadingCollection` for channel and VOD lists. Standard `ListView` will not hold up at 50k items.

### Input

Full keyboard control is a differentiator: arrow navigation, number entry for direct channel access, space/K for pause, F for fullscreen, `/` for search, PageUp/PageDown for channel stepping, I for info overlay. Also handle media keys and remote-style input, since many Windows HTPC users have an IR remote presenting as a keyboard.

**Exit criteria**
- EPG grid holds 60fps scroll with 20k channels in a release build.
- Search returns results within 80ms of keystroke on a 50k-item library.
- Entire app navigable by keyboard only.

---

## 10. Phase 8 — Packaging

- **Velopack** for installer and delta auto-update. It is the maintained successor to Squirrel and is substantially less friction than MSIX for self-distribution.
- Code signing certificate is required, not optional. Unsigned installers trigger SmartScreen warnings that will stop most users cold. An OV certificate on a hardware token is the minimum; EV builds reputation faster.
- Publish self-contained per-RID (`win-x64`, `win-arm64`) with `PublishReadyToRun=true`. Expect roughly 120–180MB installed once libmpv is included.
- Ship an unpackaged build too — HTPC users often want a portable folder.

---

## 11. Security and privacy

- Provider credentials encrypted at rest with DPAPI (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`). Never plaintext in the database or settings file.
- Redact credentials from all logs. Stream URLs contain username and password in the path — write a scrubbing formatter and apply it to Serilog output and to any diagnostics export.
- No telemetry to any server. All health data stays local.
- TMDB enrichment is opt-in and requires a user-supplied API key.

---

## 12. Conventions for the coding agent

Write these into `docs/CLAUDE.md`:

- .NET 9, C# 13, nullable enabled, `TreatWarningsAsErrors` on.
- `async`/`await` throughout the IO paths; no `.Result` or `.Wait()` anywhere.
- All long-running operations take a `CancellationToken`.
- No `System.Text.RegularExpressions` in any parse loop that runs per-line or per-record. Span-based scanning instead.
- No LINQ in per-frame or per-row hot paths.
- One prepared command reused for bulk inserts; never string-concatenated SQL.
- xUnit for tests, fixture files under `tests/Fixtures/` for M3U and XMLTV samples.
- BenchmarkDotNet for the parser benchmarks; keep the results committed so regressions are visible.

**Standing prohibitions:**
- Do not add bundled playlists, sample providers, or any content source.
- Do not use `--wid` for video embedding.
- Do not load full XMLTV or VOD JSON payloads into memory.
- Do not reference UI packages from `Iptv.Core`.
- Do not touch XAML objects from mpv callback threads.

---

## 13. Suggested build order for Claude Code

1. Solution scaffold, project references, `CLAUDE.md`, CI build.
2. SQLite schema, migrations, pragmas, FTS5 setup + tests.
3. Xtream client + M3U parser + `Iptv.Harness` timing output.
4. Channel identity, normalization, cross-provider merge + tests.
5. XMLTV pull-parser and ingest pipeline; benchmark to the 8s target.
6. `Iptv.Mpv` P/Invoke layer; console playback smoke test.
7. D3D11 render context → SwapChainPanel; verify overlay compositing.
8. Dual-handle player service and channel-change measurement.
9. Failover policy and `stream_health` recording.
10. WinUI shell, navigation, player view.
11. Channel list and category navigation.
12. EPG grid with 2D virtualization.
13. VOD/series browsing, TMDB enrichment.
14. Search, settings, diagnostics.
15. Velopack packaging, signing, update channel.

Phases 1–5 are the ones worth doing carefully. If those are right, the UI is ordinary work.
