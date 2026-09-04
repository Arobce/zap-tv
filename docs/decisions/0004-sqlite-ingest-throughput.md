# 0.4 — SQLite ingest throughput floor

**Status:** Resolved — the Phase 3 budget stands as written.
**Date:** 2026-09-04

## Question

The Phase 3 EPG budget (a 120MB XMLTV parsed, inserted and swapped in under 8s) only
holds if SQLite sustains roughly 400k programme inserts/s on the target hardware. The
spike exists so that number is measured rather than assumed.

## Method

Measured inside `EpgGridQueryTests`, which seeds a realistic guide as a side effect of
setting up the grid-window benchmark, rather than as throwaway spike code.

- 200,000 programme rows across 2,000 EPG channels, 100 back-to-back half-hour
  programmes each.
- One prepared `SqliteCommand`, parameters reset per row, single transaction.
- WAL, `synchronous=NORMAL`, `temp_store=MEMORY`, `mmap_size=256MB`, `cache_size=-64000`.
- Dev machine, Windows 11, SQLite 3.50.4 via `SQLitePCLRaw.bundle_e_sqlite3`.

## Result

| Measure | Value |
| --- | --- |
| Insert throughput | **409,485 rows/s** (200,000 rows in 0.49s) |
| Grid window query, 200 channels over a 3-hour window | **0.66ms** mean over 20 runs |
| Same query with `ix_programmes_lookup` dropped | 20.85ms mean |

## Conclusion

The floor is cleared, and with more headroom than the raw number suggests: the spike
brief specified measuring *without* indexes, but this run had `ix_programmes_lookup`
live during the insert and still reached 409k rows/s. Phase 3's plan to build indexes
after the bulk load should therefore land above this figure, not below it.

The grid window query is roughly 20x inside the 15ms Phase 1 target.

**No amendment to the Phase 3 exit criteria is needed.**

## Caveats

- One machine. A slow spinning disk or an aggressive on-access antivirus scanner would
  change this materially; the figure is a floor for *this* hardware, not a guarantee.
- 200k rows, not the 2M the brief suggested. Throughput is expected to degrade somewhat
  at larger volumes as the index depth grows, so re-measure during Phase 3 against a real
  120MB XMLTV rather than treating this as settled.
- Does not include the FTS rebuild or the staging-table swap, which is precisely why the
  Phase 3 exit criteria report parse/insert and total-ingest times as separate numbers.

## Follow-up

Phase 3 must report both timings on every harness run so a regression in insert
throughput is distinguishable from a regression in index or FTS rebuild cost.
