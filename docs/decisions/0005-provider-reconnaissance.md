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

> **⚠ The conclusion drawn below was wrong. See [decision 0006](0006-epg-coverage-ceiling.md).**
>
> This section reasoned from the provider's catalogue alone, without the guide. The
> channels lacking a `tvg_id` are overwhelmingly the same channels the guide carries no
> data for, so matching them by name cannot help. Exact id matching already recovers 99.1%
> of what any matcher could achieve. The numbers below are accurate; the inference from
> them is not.

This inverts the Phase 4 design assumption. Tier 1 (exact `tvg_id` match) has a hard
ceiling of 28% coverage on this provider. Name-based matching is not a fallback — it is
the primary mechanism for roughly seven channels in ten.

Consequences:

- SUPERSEDED by decision 0006: see the coverage ceiling. The Phase 4 exit criterion of 85% cannot be met by any matcher
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

**Correction.** An earlier version of this note claimed the dev machine's DNS was broken
and that captures required pinning IPs. That was wrong. The canary domain used to test
resolution (`example.com`) happens to fail on this network, and a dead provider hostname
failed at the same time; generalising from those two produced a confident conclusion from
two unlucky data points. The working provider hostname resolves normally from both curl
and .NET, and a full sync runs end to end with no DNS changes and no pinned IPs.

The lesson worth keeping: test a diagnosis against more than one sample before acting on
it, especially when the conclusion is "the user's environment is broken".

## Live sync results

Full end-to-end run through the real client, not fixtures.

| Measure | First sync | Second sync |
| --- | --- | --- |
| Live streams fetched | 28,285 in 4.5s | 28,285 in 15.9s |
| Merge | 28,285 added, 0.79s | 0 added, 28,285 updated, 0.77s |
| Channels after refresh | 20,478 | 20,478 |
| Distinct channel keys | 21,510 | 21,510 |

The second run is the one that matters: re-syncing identical data adds nothing, deactivates
nothing, and leaves the channel count unchanged. That is the merge guarantee demonstrated
against 28,285 real rows rather than fixtures.

Fetch time varies widely between runs (4.5s vs 15.9s) against an identical payload, so it
is provider-side throughput, not client cost. Worth remembering when Phase 3 measures EPG
ingest: the download will dominate and must be timed separately from the parse.

### Intra-provider duplication is substantial

28,285 streams collapse to 21,510 distinct `channel_key` values, so roughly 6,800 entries -
24% - are duplicates *within a single provider*. These are mostly quality variants of one
channel (HD beside FHD beside SD), which is exactly what normalization is meant to collapse
while `streams.quality` retains the distinction.

This matters more than it looks. The PRD frames dedup as a cross-provider feature, but a
quarter of the deduplication value is available with one provider configured. It also means
the failover candidate list is populated even for a single-provider user, since the
alternates are the other quality variants of the same channel.

Of the 21,510 keys, 20,478 become channels; the remaining ~1,032 belong solely to separator
rows, which are stored and shown but never promoted to channels.
