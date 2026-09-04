# 0006 — The EPG coverage ceiling, and a correction to 0005

**Status:** Decided — amends Phase 3 and substantially re-scopes Phase 4.
**Date:** 2026-09-04

First ingest of the reference provider's real 67.5MB XMLTV guide.

## Phase 3 budget: comfortably met

| Measure | Result | Target |
| --- | --- | --- |
| Parse + insert + swap | **1.3s** | 8s |
| Total including index and FTS rebuild | **1.6s** | 15s |
| Peak working set | **202MB** | under 400MB |
| Programmes | 168,578 | — |

Roughly 6x inside the parse budget and 2x inside the memory budget. The Phase 0.4
throughput floor held.

Note the download took **45–51 seconds** for the same 67.5MB, and varied between runs. It
dwarfs the parse by a factor of thirty. Any figure that combines the two is measuring the
provider's upload capacity, not this application, which is why the harness reports them
separately.

## The finding: coverage is capped at 17.6%, and matching is not the constraint

| Measure | Channels | Share of library |
| --- | --- | --- |
| User's channels | 20,478 | 100% |
| Matched by exact `tvg_id` | 4,871 | 23.8% |
| Matched **and** the guide carries programmes | 3,566 | 17.4% |
| **Ceiling for any matcher whatsoever** | **3,599** | **17.6%** |

The guide declares 15,548 `<channel>` elements, but 10,580 of them carry `id=""` and
collapse to a single row. Of the 4,968 that survive, only **3,599 have any programmes at
all**.

So exact id matching already recovers **3,566 of the 3,599 achievable channels — 99.1% of
the ceiling**. Every other matching tier in Phase 4 combined can add at most 33 channels,
or 0.16% of the library.

## Correction to decision 0005

Decision 0005 concluded that because 72.7% of the provider's channels carry no `tvg_id`,
"name-based matching is not a fallback — it is the primary mechanism for roughly seven
channels in ten". **That was wrong**, and it would have directed significant effort at the
wrong problem.

The error was reasoning from one side of the join. Channels without a `tvg_id` are
overwhelmingly the same channels the guide has no data for. Matching them by name more
cleverly does not help, because there is nothing on the other side to match to. Name
matching cannot invent programmes the guide does not carry.

The lesson: a coverage number computed from the provider's catalogue alone is not a
coverage number. It needed the guide, and the guide was not measured until now.

## Consequences

### The 85% exit criterion is unreachable and must be replaced

Phase 4's exit criterion of 85% automatic coverage cannot be met by any matcher against
this guide. Replace it with two criteria that are actually about the matcher:

1. **Recovery against the ceiling** — of the channels the guide *can* serve, at least 95%
   are matched. Currently 99.1% with id matching alone.
2. **Zero false positives** — no channel is shown a guide belonging to a different channel.

Report coverage to the user as a fraction of what the guide can supply, alongside the raw
figure, or the app appears broken when it is working correctly.

### Phase 4 shrinks

Tiers 2–4 (display-name, normalized, fuzzy) are worth at most 33 channels here. They stay,
because a different provider or a better guide changes the arithmetic entirely, but they
are no longer the phase's centre of gravity and should not be built before the rest of the
app works. The manual remap UI keeps its priority: for a user whose specific favourites
fall in the unmatched set, remapping one channel by hand is worth more than any automatic
tier.

### The real fix is more guide sources, which the PRD does not have

The constraint is guide completeness, and the only way to raise the ceiling is more or
better data. This needs a feature the PRD does not currently include: **multiple EPG
sources per library, independent of providers**, so a user can add a good third-party
XMLTV alongside their provider's thin one. `epg_channels.source_provider_id` already
allows for it; the ingest and the settings UI do not yet.

This is a genuine scope addition rather than a detail, and it should be decided
deliberately rather than absorbed.

### The 14-day horizon is aspirational

The guide spans **2.6 days** (2026-09-03 13:30 to 2026-09-06 04:00), not 14. The horizon
setting is still correct as a cap, but the EPG grid must render a guide that ends two days
out without looking broken, and the default horizon should not imply data that does not
exist.

## Also: DtdProcessing.Prohibit had to change

The PRD specified `DtdProcessing.Prohibit`. The real guide opens with
`<!DOCTYPE tv SYSTEM "xmltv.dtd">`, which is standard for XMLTV, and `Prohibit` throws on
the declaration itself — it would have rejected essentially every real guide.

Changed to `DtdProcessing.Ignore` with `XmlResolver = null`, which keeps both security
properties that mattered: the DTD is skipped rather than processed, so internal entities
are never expanded (billion-laughs), and no external subset is ever fetched (XXE). Both are
covered by tests.
