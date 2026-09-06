# 0008 — Cold-path channel change latency

**Status:** Measured. The 800ms target is not reachable on a single-connection account.
**Date:** 2026-09-05

Phase 7 Step 1: tune the cold path, measure, and only then decide whether the dual-handle
work is worth building. On the reference account it cannot be built at all —
`max_connections` is 1 — so the cold path is the entire feature.

## Method

Six option profiles, five trials each, against a real stream (`CA - TFO`, H.264 960x540).
Trials run one at a time with a pause between, because the account permits one connection
and exceeding it gets it temporarily blocked.

Each trial measures two things:

- **open** — `loadfile` to `FILE_LOADED`: connect, probe, demux. Provider and FFmpeg.
- **decode** — `FILE_LOADED` to the first frame this application can present. Ours.

The split is the point. Only the second half is tunable from here.

## Results (median ms, 5 trials)

| Profile | total | open | decode | vs baseline |
| --- | --- | --- | --- | --- |
| no-initial-pause | **1116** | 777 | 339 | −11% |
| probe-1s | 1222 | 882 | 340 | −3% |
| probe-250ms | 1222 | 803 | 419 | −3% |
| combined+lowlatency | 1239 | 784 | 455 | −2% |
| baseline (PRD profile) | 1261 | 831 | 430 | — |
| combined | 1290 | 817 | 473 | +2% |

## Finding: the target is below the provider's open time

**Open is 777–882ms on every profile.** The 800ms p50 target is roughly equal to the time
this provider takes to accept a connection and produce a decodable keyframe, before this
application does anything at all.

The open time barely moves across profiles whose `probesize` differs by twenty times —
250KB, 1MB and FFmpeg's 5MB default all land in the same band. So it is not probe-bound.
It is network round-trips and waiting for the next keyframe in the transport stream, and
no client option shortens either.

Decode, the part that is ours, is 339–473ms and does respond to tuning.

**Conclusion: 800ms p50 is unreachable on this provider without prebuffering, and
prebuffering needs a second connection this account does not have.** A realistic target
here is ~1.1s. The 800ms figure remains reasonable for a multi-connection account, where
the warm handle has already paid the open cost before the user presses anything.

## Correction: my first run's conclusions were noise

An earlier three-trial run produced a different ranking, with `combined+lowlatency` fastest
at 996ms and `probe-1s` at 1006ms. At five trials those became 1239ms and 1222ms, and
`no-initial-pause` — which had looked 6% *worse* than baseline — came out best.

Three trials was not enough to distinguish these profiles, and the ranking it produced was
essentially random. Nothing should have been concluded from it, and the only reason the
error surfaced is that the second run was made before acting on the first.

Even at five trials the spread between the top four profiles is within their own
trial-to-trial variance. The honest reading is: **`no-initial-pause` is a small real win,
and the rest are indistinguishable from noise.**

## Recommendation

1. Adopt `cache-pause-initial=no` in the live profile. It is the one change with evidence
   behind it, and it is free.
2. Do not adopt the aggressive probe settings. They showed no benefit, and a smaller
   probe risks FFmpeg mis-detecting a stream, which trades a reliable second for an
   occasional failure.
3. Amend the Phase 7 exit criteria to state the target per account type, because a single
   number cannot describe both cases honestly.
4. Revisit with a multi-connection provider before judging the dual-handle design. Its
   value is exactly the open time — 777–882ms here — which is the majority of the delay
   and the part tuning cannot touch.

## Caveats

- One provider, one channel, one machine, five trials. The open time is a property of this
  provider's edge and will differ elsewhere.
- The stream is 960x540. A 1080i channel decodes more slowly, so the decode share will be
  larger and the tuning more valuable than it looks here.
- Measured over a residential connection whose throughput to this provider varied by 3.5x
  during EPG downloads. That variance is inside these numbers.
