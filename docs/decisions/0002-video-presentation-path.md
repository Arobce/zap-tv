# 0.2a — How to obtain a GL context for the render API

**Status:** Decided and validated — **A with C as fallback**. GL rendering proven end to end; texture sharing into a `SwapChainPanel` remains.
**Date:** 2026-09-05

## What is already settled

- Spike 0.1: libmpv offers `MPV_RENDER_API_TYPE_OPENGL` and `MPV_RENDER_API_TYPE_SW` only.
  There is no D3D11 render API.
- The P/Invoke layer works. libmpv loads, initialises, accepts the live profile options,
  and survives 200 create/dispose cycles.
- The render API interop works. Using the SW backend, mpv decodes a real frame and writes
  pixels into a managed buffer in 52ms, through the same `mpv_render_param` array the GL
  path uses.

So the remaining unknown is narrow and specific: **the OpenGL path needs a GL context, and
Windows does not hand one over.**

## What was assumed, and why it was wrong

Decision 0001 stated that ANGLE ships with the Windows App SDK. It does not. Every
`Microsoft.WindowsAppSDK.*` package was searched — base, foundation, winui,
interactiveexperiences, runtime, dwrite and the rest — and none contains `libEGL.dll` or
`libGLESv2.dll`. WinUI 3 may use ANGLE internally, but it does not redistribute it for
application use.

The available ANGLE packages on NuGet (`ANGLE.WindowsStore` and its variants) are
UWP-era, last meaningfully updated a decade ago, and are not a foundation for a 2026
desktop app.

## Options

### A. Native WGL plus `WGL_NV_DX_interop2`

Create a real OpenGL context with `opengl32.dll` on a hidden window, then share a D3D11
texture into GL through `WGL_NV_DX_interop2`, render mpv into it, and present that texture
through the `SwapChainPanel`.

- No new redistributable; `opengl32.dll` is part of Windows.
- The extension is supported by current NVIDIA, AMD and Intel desktop drivers, and this is
  the technique several production mpv-based Windows players use.
- **Fails where no vendor driver is present.** Microsoft Basic Display Adapter provides
  OpenGL 1.1, which mpv cannot use, so a machine with no GPU driver — a fresh install, some
  VMs, RDP sessions — has no video at all.
- Most interop code of the three options.

### B. Bundle ANGLE

Ship `libEGL.dll` and `libGLESv2.dll` built from Chromium's ANGLE, giving GL ES backed by
D3D11.

- Works everywhere, including on machines with no vendor GL driver, because ANGLE targets
  D3D11 rather than GL.
- Roughly 5–10MB added to the installer, against a 120–180MB budget that already includes
  a 120MB libmpv.
- Requires either building from source with `depot_tools` or tracking a trustworthy
  prebuilt, and then owning that as a supply-chain dependency and a security-update
  obligation.

### C. Software rendering into a `WriteableBitmap`

Already built and working.

- No GPU dependency at all.
- Copies every frame through system memory. Will not hold 1080i, which is most of live TV.
- **Not viable as the shipping path.** Retained as the diagnostic fallback and as the way
  to prove the rest of the pipeline while the GL path is being built.

## Recommendation

**A, with C as the automatic fallback.**

Option A costs no redistributable and no supply-chain obligation, and its failure mode is
confined to machines with no GPU driver — which is also a machine that will struggle with
1080i regardless. Detect `WGL_NV_DX_interop2` at startup, fall back to C with a visible
diagnostic saying why, and revisit B only if that fallback turns out to fire on real
users' machines.

The order matters: A and C together cover every machine, and B can be added later without
changing the `IVideoPresenter` seam. Committing to B now would take on a permanent
dependency to solve a problem that may not exist.

## What this does not change

`--wid` remains prohibited. It is the one option that would make this easy and it is
excluded for a reason that has not changed: the video HWND renders above all XAML, so
every overlay, the EPG panel and every context menu would be unable to draw over video.
The prohibition matters most at exactly this moment, when the sanctioned paths are
turning out to be work.

---

## Outcome

**A was built and works. No ANGLE, no redistributable.**

A WGL context is created on a hidden window, mpv accepts it through
`MPV_RENDER_API_TYPE_OPENGL`, and rendering runs sustained.

| Measure | Result |
| --- | --- |
| Driver reported | NVIDIA RTX 5080, OpenGL 4.6.0, driver 610.74 |
| `WGL_NV_DX_interop2` | Present |
| mpv accepting a WGL context | Yes |
| First GL frame | 186ms after `loadfile` |
| Sustained rendering | 100 frames in 1656ms (~60fps, the source rate) |
| 25 create/free cycles | Clean |

Sustained rendering matters more than the first frame. A single frame can succeed while
the `get_proc_address` delegate is collected moments later; mpv holds that pointer for the
life of the context and calls it during rendering, so a crash from a collected delegate
appears at a random later frame rather than at the mistake. Rendering a hundred frames is
what shows the callback lifetime is right.

Two details that would each have produced a confusing failure:

- **`wglGetProcAddress` returns null for core GL 1.1 functions**, which live as ordinary
  exports in `opengl32.dll`. A resolver asking only WGL hands mpv a null pointer for
  functions that plainly exist. mpv's own header warns about this; the resolver tries both.
- **Some drivers return 1, 2, 3 or -1** rather than null for an unsupported entry point.
  Treating those as valid means calling into address `0x1`.

## What remains

Rendering into FBO 0 of a hidden window proves the pipeline. It does not put pixels on
screen. Still to do:

1. Share a D3D11 texture into GL via `WGL_NV_DX_interop2`, render mpv into it.
2. Present that texture through `SwapChainPanel` with `ISwapChainPanelNative.SetSwapChain`.
3. Confirm a semi-transparent XAML overlay composites above it — the actual point of the
   whole exercise, and the thing `--wid` cannot do.

The risky unknowns are now behind us: libmpv loads, the render API interop is correct, and
the driver supports the extension the design depends on. What is left is assembly work
against APIs that are known to exist.

## Caveat on the measurement

One machine, and a high-end NVIDIA GPU. `SupportsHardwarePath` returning true here says
nothing about Intel integrated graphics, AMD, or a driverless VM. The fallback to software
rendering is therefore not optional polish — it is the path for every machine this one does
not represent, and it needs testing on hardware that actually fails the check.
