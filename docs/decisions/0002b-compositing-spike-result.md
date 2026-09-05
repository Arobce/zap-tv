# 0.2 — Compositing spike result

**Status:** Passed. The architecture holds. Phase 0 is complete.
**Date:** 2026-09-05

## The question

Does XAML composite **above** video presented through a `SwapChainPanel`?

Everything downstream assumes yes. Channel overlays, the EPG panel, transport controls and
every context menu draw over video. If the answer were no, the presentation design would be
wrong and no z-order change could fix it — that is the airspace violation HWND embedding
causes, and the reason `--wid` is prohibited.

## Result: yes

A minimal WinUI 3 window with a `SwapChainPanel`, running the full chain:

```
mpv  ->  OpenGL (WGL)  ->  shared D3D11 texture  ->  DXGI swap chain  ->  SwapChainPanel
```

Confirmed visually:

- **Alpha blending is real.** A 60%-opaque `Border` over the video reads purple where it
  crosses a red bar, pale blue over cyan, and green over yellow. An opaque panel would
  have looked identical whether it composited or merely covered the video, which is why
  the test overlay is deliberately translucent.
- **Multiple siblings composite.** A `Border`, an `Ellipse` and a second `Border` all draw
  above the video. This matters: the real UI stacks many overlays, and one working sibling
  would not have proved the general case.
- **Presentation is continuous.** Two captures a second apart differed in 2,217 of 4,945
  sampled pixels — the video is animating, not stuck on a single frame.

## What this closes

Phase 0 is now complete, and every one of its four spikes changed something:

| Spike | Outcome |
| --- | --- |
| 0.1 render API | No D3D11 backend exists. The PRD's primary video path was fiction. |
| 0.2 compositing | Passed, via WGL rather than the ANGLE the PRD assumed was available. |
| 0.3 ARM64 | Viable after all; the PRD's assumption that it was not was never checked. |
| 0.4 ingest throughput | 409k rows/s, clearing the floor the Phase 3 budget needed. |

Three of the four contradicted a stated constraint. Had the original build order been
followed, each would have surfaced after the code depending on it was already written.

## Honest limits of this result

- **One machine, one GPU.** NVIDIA RTX 5080, driver 610.74. `WGL_NV_DX_interop2` is
  supported by current Intel and AMD desktop drivers too, but that is documentation, not
  measurement. The software fallback remains the path for machines this one does not
  represent, and it has not been exercised on hardware that actually fails the check.
- **Resize and DPI change are not yet verified.** The PRD requires surviving both without
  corruption. The window was resized during this spike with raw `MoveWindow`, which does
  not drive a normal XAML relayout — elements stayed positioned for the original window
  size. That is an artifact of how the spike was driven, not evidence either way, so both
  requirements remain untested. The swap chain also does not yet resize with the panel.
- **The test source is `lavfi:testsrc`, not a live stream.** Decode of real MPEG-TS through
  this path is covered by the Phase 5 exit criteria, not here.

## Follow-up before Phase 5 can close

1. Resize the swap chain when the panel's size or DPI changes, and confirm no corruption.
2. Verify the software fallback on a machine without the extension.
3. Play a real MPEG-TS stream and confirm `hwdec-current` resolves to `d3d11va` or
   `d3d11va-copy`.

## Aside worth keeping

The XAML compiler exits with code 1 and **prints nothing at all** when a comment contains a
double hyphen, which is illegal in XML. A comment mentioning `--wid` cost a bisect to find.
If the XAML compiler ever fails silently, check the comments first.
