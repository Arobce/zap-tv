# 0005 — Reconnaissance against a real Xtream provider

**Status:** Informational — amends expectations for Phases 3, 4 and 7.
**Date:** 2026-09-04

First contact with a real Xtream panel. Every number here is measured, not estimated.
Credentials and provider-identifying hostnames are deliberately absent; the account is
the user's own and lives in a gitignored local file.

## Account shape

| Field | Value | Note |
| --- | --- | --- |
| `auth` | `1` | JSON **number** |
| `status` | `"Active"` | |
| `max_connections` | `"1"` | JSON **string** containing a number |
| `allowed_output_formats` | `["m3u8","ts"]` | |
| `exp_date`, `created_at`, `is_trial`, `active_cons` | strings | all numeric values as strings |
| `timestamp_now` | number | inconsistent with the above |

## Catalogue size

| Endpoint | Size | Records |
| --- | --- | --- |
| `get_live_categories` | 28 KB | 389 |
| `get_live_streams` | 8.1 MB | 28,285 |
| `get_series` | **75.3 MB** | — |
| `get_vod_categories` | 14 KB | — |
| `get_series_categories` | 6 KB | — |

## Findings that change the plan

### 1. `max_connections` is 1 — Phase 7 prebuffering cannot be used here

The headline sub-second channel change feature is built on two mpv handles, which consume
two connections. This account permits one.

The PRD already requires disabling prebuffering automatically when `max_connections` is 1,
so the design is correct. What changes is the expectation: on this provider the flagship
feature degrades to the cold-path tuning in Phase 7 Step 1 alone. That makes Step 1 the
primary work rather than a warm-up for Step 2, and it means the p50 target must be
demonstrated **without** prebuffering before Step 2 is worth building.

Single-connection accounts appear to be common at the consumer end of the market. Phase 7
should be validated against one throughout, not only against a multi-connection account.

### 2. 72.7% of live channels have no EPG id

Of 28,285 live streams, **20,556 have no usable `epg_channel_id`**; only 7,729 carry one.

An earlier pass over the raw JSON counted 20,249 by grepping for the literal
`"epg_channel_id":null`. Deserializing properly raises it by 307, because those entries
send `""` rather than `null`. Both mean "no EPG id", which is exactly why the string
converter normalizes empty to null — otherwise 307 channels would carry an EPG id that
matches nothing and would be counted as covered.

This inverts the Phase 4 design assumption. Tier 1 (exact `tvg_id` match) has a hard
ceiling of 28% coverage on this provider. Name-based matching is not a fallback — it is
the primary mechanism for roughly seven channels in ten.

Consequences:

- The Phase 4 exit criterion of 85% automatic coverage cannot be met by `tvg_id` matching
  and depends almost entirely on tiers 2-4 and on the quality of the EPG source's
  `display-name` values. Treat 85% as unvalidated until measured against a real XMLTV.
- `channel_key` will take the `name:` form for ~72% of channels, which makes normalization
  stability far more load-bearing than anticipated. The versioned-migration machinery in
  Phase 1 is not belt-and-braces; it is on the critical path.
- The manual remap UI is not a nicety for the long tail. It is a primary surface.

### 3. `get_series` is 75 MB

Nearly double the 40 MB the PRD anticipated for the largest payload. Streaming
deserialization via `DeserializeAsyncEnumerable` is mandatory, and the harness should
assert peak memory during a full sync rather than only timing it.

### 4. Type inconsistency is confirmed, and it is inconsistent *within* one response

`get_live_streams` returns `stream_id` as a number and `category_id` as a string, in the
same object. `get_live_categories` returns `category_id` as a string and `parent_id` as a
number. `added` is a string containing a unix timestamp.

Tolerant converters accepting both forms per field are required, exactly as the PRD says.
There is no single rule to apply globally.

### 5. The catalogue contains substantial non-content

1,094 entries are separator rows (`##### GOLDEN EVENTS #####`) and dozens are placeholders
(`VIP - NO EVENT`). These are not channels; they are visual dividers in the provider's
category listing.

Not currently handled anywhere in the PRD.

**Decision: keep them visible, but classify them.** They are the provider's own section
headings, and hiding them flattens a grouping the user finds useful. Suppressing them
would be discarding information the provider deliberately encoded.

They are not channels, though, so they are marked `is_separator` at ingest and excluded
from search results, cross-provider dedup, failover candidacy, and the EPG coverage
denominator. Without that exclusion, 1,094 non-channels would depress the Phase 4 coverage
figure and offer themselves as failover targets that can never play.

Detection is structural rather than a keyword list: a run of three or more identical
punctuation or symbol characters (`#####`, `---`, `===`, `***`, `▬▬▬`) marks a decorative
row. Keyword matching would need a per-provider vocabulary and would misfire on real
channels.

Note that `VIP - NO EVENT` placeholders are a different case: they are real streams that
are idle between events, not decorative rows, and are left alone.

## Environment note

The account came with two hostnames. The primary does not resolve at all — NXDOMAIN from
public DNS, not merely unreachable. The secondary works, and is where these numbers come
from. Providers rotating and abandoning hostnames is normal, so the app should surface a
clear "host unreachable" state rather than a generic sync failure, and must not mark a
provider's streams inactive on a DNS failure — that would deactivate an entire catalogue
because a domain lapsed.

Separately, the dev machine's router DNS fails to resolve most domains. Captures were made
by pinning the resolved IP per request. Unrelated to the app, but it will affect any
future capture work on this machine.
