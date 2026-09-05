# 0007 — First real stream playback

**Status:** Phase 5 decode criterion partially met. Establishes the Phase 7 baseline.
**Date:** 2026-09-05

First playback of a real MPEG-TS stream from the reference provider through the full
pipeline: mpv, OpenGL via WGL, shared D3D11 texture.

## Results

Channel `CA - TFO`, H.264 960x540, five runs.

| Measure | Result |
| --- | --- |
| Time to first frame | 1168, 1231, 1309, 1578, 2319 ms |
| Sustained rendering | 301 frames in ~11.6s |
| `hwdec-current` | `nvdec` in 4 runs, `no` in 1 |
| GPU | NVIDIA RTX 5080, OpenGL 4.6.0 |

The `-copy` distinction did not arise here: `nvdec` is the zero-copy path.

## Finding 1: the PRD's decode criterion was too narrow, and I applied it wrongly

The document said to accept `d3d11va` or `d3d11va-copy`. The harness matched on `d3d11`,
so the first successful run — decoding in hardware via `nvdec` — was reported as
**SOFTWARE**.

With `hwdec=auto-safe`, mpv chooses the best backend for the adapter, and on NVIDIA that is
`nvdec` rather than `d3d11va`. Both the document and the check were wrong. Had this shipped,
the Diagnostics view would have told users their hardware decoding was broken when it was
working, and someone would have spent real time optimising a non-problem.

The rule is now: anything other than absent or `no` is hardware. `-copy` variants still
count, with the copy-back noted since frames cross system memory.

## Finding 2: hardware decode is not deterministic

Across five identical runs against the same stream, four reported `nvdec` and one reported
`no`. The outlier coincided with stream corruption at join time — `non-existing PPS 0
referenced`, `no frame!` — which is normal when joining a live transport stream before a
keyframe, and appears to have made mpv fall back.

This is exactly the failure the PRD predicted: silent fallback to software decode presenting
as "the app is slow". It means the app must **show the live value** rather than sampling it
once at startup and assuming it holds, and a single good reading is not evidence the path is
reliable.

## Finding 3: the Phase 7 baseline is 1.2–2.3s, and prebuffering is unavailable

Time to first frame ranged 1168–2319ms against a target p50 of **800ms**.

The reference account permits one connection, so the dual-handle prebuffering that Phase 7
was designed around cannot be used at all here. Everything must come from cold-path tuning:
`probesize` and `analyzeduration`, `cache-pause-initial=no`, minimal readahead, and
connection reuse.

That is now measured rather than assumed, which makes Phase 7 Step 1 the whole of the
feature on this provider rather than a warm-up. The gap is roughly 400ms–1.5s, and closing
it is real work.

## What is still untested

- **Intel and AMD.** One NVIDIA card is not a decode matrix, and `auto-safe` will choose
  differently on each.
- **The software fallback**, on a machine with no usable GL driver. It exists and is wired
  in, but has never run in anger.
- **1080i deinterlacing**, which is where software decode actually hurts. This stream was
  540p progressive.
- **Sustained playback beyond ~12 seconds.** Long-run stability, reconnection and the
  stall detection Phase 8 depends on are not covered by a 12-second smoke test.
