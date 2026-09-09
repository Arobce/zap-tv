# 0012 — Detecting a cut stream

**Status:** Resolved. The frame-based detector missed the 10s budget on two runs in three;
watching the buffer drain holds it at ~5.2s regardless of what is buffered.
**Date:** 2026-09-08

## Question

Phase 8's exit criterion is recovery within 10s of a stream being killed mid-playback, with
the host blocked in a firewall. `harness drill` proved the recovery half offline, but it
starts from a URL that was never alive — so it fails at *connect*, which is a different
thing entirely from a stream that is playing and then stops.

Nothing had ever tested the second case.

## What the measurement showed

`harness killswitch` plays a real channel through a local TCP forwarder, cuts it
mid-stream, and times detection and recovery. Three runs of the same channel, with the
original frame-based detector:

| Buffered at the cut | Detected + recovered |
|---|---|
| 1,246 KB | 8.3s |
| 2,142 KB | 12.1s |
| 2,918 KB | **19.0s** |

Two of three missed the budget, and the pattern is unmistakable: **detection time scales
with how much happened to be buffered.** Recovery itself took 3ms every time. The entire
budget was going on noticing.

The cause is that `demuxer-max-bytes` is 32MiB while `demuxer-readahead-secs` is 2. The
readahead is a floor, not a ceiling — mpv keeps reading until the byte limit, which on a
typical channel is twenty to thirty seconds of video. Frames therefore keep being presented
long after the provider has gone, and a detector waiting for frames to stop waits for that
buffer to play out *and then* counts its own five seconds.

## Decision

**Watch the buffer draining at real time.** If the demuxer holds four fewer seconds of
video than it did four seconds ago, nothing is arriving. That is a cut, and it is true
immediately regardless of how deep the buffer is.

A stream that is merely slow still delivers *something*, so its buffer drains more slowly
than it plays and this deliberately does not fire on it — a brief hiccup must not become a
channel change. The threshold is 0.85 of real time rather than 1.0, because playback and
the sampling clock are not the same clock.

The frame rule is kept as a backstop for streams that report no cache duration.

After the change, same channel:

| Buffered at the cut | Detected + recovered |
|---|---|
| 3,483 KB | 6.0s (frame rule; the buffer was already empty) |
| 21,161 KB | 5.16s |
| 24,671 KB | 5.20s |

Constant, and inside the budget with headroom.

## Two things the drill found on the way

**The provider redirects.** A stream request to the advertised host answers `302 Found` to
a different origin, and mpv follows it — straight past the forwarder. The first version of
this drill cut a connection nothing was using, watched a perfectly healthy stream, and
reported a 40s detection. It now resolves redirects itself and puts the forwarder on the
origin, and refuses to report a measurement when the bytes did not go through it.

**The tests passed for the wrong reason.** The first implementation trimmed its sample
window to samples strictly inside it, so the oldest sample was always younger than the
window and the comparison could never be satisfied. Every unit test passed, because
synthetic samples land exactly on the boundary and the equality case saved it. A real timer
is always a few milliseconds late and never lands there, so the rule never once fired
against the real provider. The tests now jitter their timestamps the way a timer does, and
that version of the code fails three of them.

## Not addressed

Cross-provider failover. The criterion says recovery "on another provider", and one account
is configured — `harness probe` confirms the second host on record does not resolve, so
this needs a second subscription. What is proved here is detection and recovery within
budget against a genuine mid-stream cut, using a second candidate for the same channel.

The literal firewall form is also not what runs: creating a rule needs elevation, leaves
state behind if the run dies, and cannot be reproduced by anyone without those rights. A
severed TCP forwarder produces the same failure — connection dropped mid-transfer, nothing
accepting a new one — and runs anywhere.
