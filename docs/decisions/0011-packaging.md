# 0011 — Packaging: what shipping actually costs

**Status:** Resolved. Both RIDs build, install and update. Signing is outstanding and is not
something the build can solve.
**Date:** 2026-09-08

## Question

Phase 10 specifies Velopack, self-contained per-RID publishes with ReadyToRun, a portable
folder, and estimates "roughly 120–180MB installed once libmpv is included". None of that
had been run.

## Result

Both architectures publish, the payload is verified, and delta updates work. Measured:

| | win-x64 | win-arm64 |
|---|---|---|
| Published folder | 337.5 MB | 324.1 MB |
| Installer (`Setup.exe`) | 137 MB | — |
| Full package (`.nupkg`) | 133 MB | — |
| Delta package, one build apart | **176 KB** | — |

The delta is the number that matters. Velopack patched 8 files of 518 and left 510
unchanged, so a routine update moves 176KB rather than 133MB. Without deltas, self-updating
an app that carries a 115MB native library would be indefensible.

### The PRD's size estimate was low

"Roughly 120–180MB installed" is off by about a factor of two: **337MB installed**, 133MB
downloaded. The estimate counted libmpv and the app, and not the two runtimes underneath
them — a self-contained .NET 10 publish plus `WindowsAppSDKSelfContained` together account
for most of the difference, and ReadyToRun adds precompiled native code on top of the IL it
does not replace.

`WindowsAppSDKSelfContained` is not optional here. Without it the installed app also
requires the Windows App SDK runtime, and an HTPC user who unzipped a portable folder has no
installer to pull it in.

### The application PRI is not published

Found by running the output rather than by reading it. The published app exited immediately
with `0xC000027B` — a stowed WinRT exception — before reaching any code that could log why.

The framework PRIs are published; the application's own `Iptv.App.pri` is not. It is added
to the publish set by the MSIX tooling, which this project switches off because it ships a
Velopack installer and a portable folder rather than an MSIX. `dotnet build` copies it and
`dotnet publish` does not, so the failure appears only in a release build, and only when
run.

Fixed with a `PublishApplicationPri` target in `src/Iptv.App/Iptv.App.csproj`, and asserted
in `scripts/publish.ps1` so it cannot regress quietly. It is the kind of thing that would
otherwise be rediscovered by whoever installs the release.

### The architecture guard earns its place

Decision 0003 warned that the x64 `libmpv-2.dll` in an ARM64 package fails at load time with
an error that reads as a missing dependency. `scripts/publish.ps1` now reads the PE machine
field of both the executable and the native library and refuses to package a mismatch. The
two fetched binaries were confirmed genuinely different — PE machine `x64` and `ARM64` — so
the guard is checking something real rather than asserting a tautology.

## Decision

- **Ship both RIDs from `scripts/publish.ps1`.** It publishes, verifies the payload, and
  hands off to `vpk`.
- **`scripts/fetch-libmpv.ps1` pins the mpv release.** Decision 0001 makes re-running the
  render API inventory the condition for moving to a newer build; floating on "latest" would
  move the binary underneath that check.
- **Amend the PRD's size estimate** to 337MB installed / 133MB downloaded.

## Not done

**Code signing.** OV and EV certificates both require hardware or HSM key storage, so this
needs a purchased token and cannot be produced by the build. Until then every release is
unsigned and SmartScreen will warn on first run — which the PRD is right to call the thing
that stops most users cold. `scripts/publish.ps1` takes `-SigningParams` and forwards them
to `vpk`, so the only missing piece is the certificate itself.

**ARM64 is built, not tested.** This carries decision 0003's caveat forward unchanged: the
package is correct and no ARM64 hardware has run it. Hardware decode on Qualcomm and
Snapdragon GPUs remains unproven.

**No update feed.** `vpk` writes `releases.win.json` and `RELEASES` into
`artifacts/releases/<rid>`, which is the feed an installed app would poll, but nothing hosts
it yet and no code checks it. The updater is packaged and unused until there is somewhere to
publish releases to.
