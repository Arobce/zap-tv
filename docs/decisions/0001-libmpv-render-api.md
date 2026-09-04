# 0.1 — libmpv render API inventory

**Status:** Resolved. The PRD's original video path does not exist; the amended one is correct.
**Date:** 2026-09-04
**Build inspected:** shinchiro `mpv-dev-x86_64-20260903-git-69e63f425a`, client API 2.5

## Question

The video presentation design depends on which render backends libmpv actually exposes.
The PRD originally specified rendering "into a D3D11 texture" through the mpv render API,
with OpenGL-via-ANGLE listed as a fallback. This spike establishes which of those is real
before any interop is written.

## Method

Extracted the dev package and read the headers for the exact build to be shipped.

## Result

`include/mpv/` contains `client.h`, `render.h`, `render_gl.h` and `stream_cb.h`. There is
no `render_d3d11.h` or equivalent, and no header mentions D3D11 or DXGI at all.

`render.h` defines the complete set of render API types:

```c
#define MPV_RENDER_API_TYPE_OPENGL "opengl"
#define MPV_RENDER_API_TYPE_SW     "sw"
```

The header's own "Supported backends" section lists exactly these two.

## Decision

**There is no D3D11 render API.** The PRD as originally written specified a video path
that libmpv does not provide, and Phase 5 would have hit a wall after the interop layer
was already built.

The presentation path is therefore:

- **Primary: OpenGL render API over ANGLE.** `mpv_render_context_create` with
  `MPV_RENDER_API_TYPE_OPENGL` and an ANGLE EGL context whose backing device is D3D11,
  rendering into a texture shared with a `SwapChainPanel`. ANGLE ships with the Windows
  App SDK, so this adds no new redistributable.
- **Debug only: `MPV_RENDER_API_TYPE_SW`** into a `WriteableBitmap`, for diagnosing a
  broken GPU path. Never shipped as the default; it burns CPU and will not hold 1080i.
- **Never `--wid`.** Unchanged, and the prohibition matters more now that the convenient
  path is gone: an airspace violation cannot be fixed by z-order, and reaching for it
  under pressure would forfeit every overlay in the app.

D3D11 remains available *internally* to mpv via `--gpu-api=d3d11`, but that is mpv
choosing its own backend for its own window. It does not hand the embedder a texture, and
it is not reachable through the render API.

## Why this spike existed

This is the one that justified restructuring the PRD around Phase 0. The original document
stated the D3D11 path as a settled constraint — "the critical constraint", with the
alternatives listed as fallbacks — and building in that order would have deferred this
discovery until after the P/Invoke layer, the presenter abstraction, and the dual-handle
design had been written against an API that does not exist.

The cost of finding out here was reading two header files.

## Follow-up

`IVideoPresenter` keeps the swap-chain plumbing on one side and mpv on the other, so the
backend stays swappable if a future mpv release adds one. Re-run this inventory when
upgrading libmpv; it is a one-line grep for `MPV_RENDER_API_TYPE`.
