# 0.3 — ARM64 feasibility

**Status:** Resolved. ARM64 is viable; the PRD's conditional wording can be lifted.
**Date:** 2026-09-04

## Question

The PRD promised a `win-arm64` self-contained publish while also forbidding compiling mpv.
If no maintained ARM64 libmpv build existed, those two constraints were in conflict and
Phase 10 had to drop the RID.

## Result

A maintained ARM64 build exists and ships in the same release as the x64 one:

```
mpv-dev-aarch64-20260903-git-69e63f425a.7z
mpv-dev-x86_64-20260903-git-69e63f425a.7z
```

shinchiro's `mpv-winbuild-cmake` publishes aarch64 alongside x86_64, i686 and an x86_64-v3
variant, on the same cadence.

## Decision

**Keep `win-arm64`.** Phase 10 publishes both RIDs, each carrying the matching
`libmpv-2.dll`. No mpv compilation is required, so the standing prohibition holds.

This corrects an assumption in the PRD, which stated that shinchiro's builds are x64 and
made ARM64 conditional on this spike. The assumption was wrong: it was never verified, and
checking took one API call.

## Caveats

- Not yet validated on ARM64 hardware. The binary exists and the RID will publish, but
  hardware decode on Qualcomm and Snapdragon GPUs is unproven, and the Phase 5 exit
  criterion covering Intel, NVIDIA and AMD says nothing about them. Treat ARM64 as
  buildable-and-shippable, not as tested, until someone runs it.
- The per-RID build must select the matching native binary. Shipping the x64 `libmpv-2.dll`
  in an ARM64 package fails at load time with an error that reads as a missing dependency
  rather than an architecture mismatch, which is a slow thing to diagnose.
