# 0010 — How far ahead the guide actually reaches

Status: accepted
Date: 2026-09-08

## Question

Phase 9 sets the EPG grid a target of "smooth scrolling at monitor refresh rate with 20k
channels and a 14-day guide loaded". Before building a custom both-axes virtualizing panel
against that target, two things needed checking: whether the windowed query is fast enough,
and whether there is a fortnight of guide to scroll through.

## What the guide contains

`dotnet run --project src/Iptv.Harness -- guide`, immediately after a fresh ingest.

```
spans      2026-09-07 12:00 to 2026-09-10 05:00 UTC   (2.7 days)
now        2026-09-08 22:03 UTC
remaining  30.9h ahead, 34.1h behind

programmes                    164,661
guide channels                  3,471
mapped channels                 3,450

on air, by hour from now
  + 0h    3,341
  + 1h    3,339
  + 6h    3,270
  +12h    3,261
  +24h        7
  +48h        0
```

Two facts, and they pull in opposite directions.

The guide is **dense where it exists**: 3,341 channels have a programme on air right now,
and that holds within 2.5% out to twelve hours. This is a grid worth building — a sixth of
the library has something to draw.

The guide is **short**: it reaches about 24 hours ahead and then stops. The provider
publishes roughly 2.7 days centred on the moment it is fetched, not a fortnight. The 14-day
target in Phase 9 is not reachable against this provider, and a grid offering a fortnight of
columns would be offering thirteen days of blank.

## The query

The PRD requires a query per visible window rather than loading programmes into memory.
Measured over a 60-channel realization window against the 164,661-programme guide, three
runs each:

```
epg window,  2h    3ms   103 blocks
epg window, 24h    1ms   604 blocks
```

Comfortably inside a 16.7ms frame, so the horizontal window can be re-read on scroll rather
than cached. `ix_programmes_lookup(epg_channel_id, start_utc, stop_utc)` serves it as it
stands; no new index was needed.

Both bounds are half open — `stop > from AND start < to` — so a programme straddling either
edge is drawn, and one ending exactly as the window opens is not.

## Decisions

**The grid bounds itself to the guide's real span**, read from `min(start_utc)` and
`max(stop_utc)`, rather than to a fixed horizon. Scrolling into a week of empty columns
because the provider publishes two days is a worse answer than stopping where the data does.

**Phase 9's 14-day exit criterion is amended to the stored span.** The performance question
it was really asking — does the grid stay smooth over the whole guide — is unchanged; the
number was an assumption about providers rather than a measurement of one.

**Automatic refresh is required, not optional.** A guide that reaches 24 hours ahead is
empty for anyone who opens the app two days after a sync — which is exactly what the first
run of this survey found, before the re-ingest:

```
+ 0h    10        (stale guide, ingested two days earlier)
+12h     0
```

Ten channels out of 3,450. Coverage was still reported as 16.8%, because coverage counts
channels with *any* guide and says nothing about whether that guide covers the time anybody
is going to look at. The two numbers need reporting together.

## Not established

- Whether other providers publish longer. The horizon here is one provider's habit, not a
  property of XMLTV.
- Scroll smoothness itself. That needs the panel, which this decision clears the way for.
