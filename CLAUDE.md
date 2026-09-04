# CLAUDE.md — Windows IPTV Player

The specification is [IPTV-Player-PRD.md](IPTV-Player-PRD.md). It is the source of truth.
When this file and the PRD disagree, the PRD wins. When the PRD and a plausible-looking
tutorial disagree, the PRD wins.

## Build and test

```bash
dotnet build                      # solution is Iptv.slnx
dotnet test
dotnet run --project src/Iptv.Harness -- <command>
```

Toolchain: .NET 10 SDK. `Iptv.Core` and its tests are platform-neutral (`net10.0`);
`Iptv.Mpv`, `Iptv.Harness` and `Iptv.App` target `net10.0-windows`.

Package versions are centrally managed in `Directory.Packages.props`. Add a
`PackageVersion` there and reference it by name only from the project.

## Architecture

```
Iptv.Core      platform-neutral. Ingest, parsing, matching, failover policy, data.
Iptv.Mpv       Windows. libmpv P/Invoke, render context, GPU interop.
Iptv.App       Windows. WinUI 3, MVVM. Not yet scaffolded (needs the workload).
Iptv.Harness   Windows. Console; times ingest and runs playback smoke tests.
```

Dependencies point inward toward `Iptv.Core`. Core never references outward.

`Iptv.Core` must not reference `Microsoft.WindowsAppSDK`, `Microsoft.UI.Xaml`, or
`CommunityToolkit.Mvvm`. This is enforced by the `AssertNoUiDependencies` target in
`src/Iptv.Core/Iptv.Core.csproj`, which fails the build with `IPTV0001`. If Core needs
to notify the UI it exposes `IAsyncEnumerable<T>`, `Channel<T>`, or plain events —
never `DispatcherQueue`.

## Conventions

- Nullable enabled, `TreatWarningsAsErrors` on. Do not suppress a warning to make a
  build pass; fix the cause or justify the suppression in a comment.
- `async`/`await` throughout the IO paths. No `.Result`, no `.Wait()`, anywhere.
- All long-running operations take a `CancellationToken`.
- No `System.Text.RegularExpressions` in any parse loop that runs per-line or
  per-record. Span-based scanning instead.
- No LINQ in per-frame or per-row hot paths.
- One prepared `SqliteCommand` reused for bulk inserts, parameters reset per row.
  Never string-concatenated SQL.
- Raw ADO.NET for bulk ingest. EF Core is acceptable for settings and provider CRUD
  but must not be used for programme inserts.
- xUnit for tests. Fixture files under `tests/Fixtures/`.
- BenchmarkDotNet for parser benchmarks; commit the results so regressions are visible.

## Standing prohibitions

These exist because the obvious approach fails. Do not work around them.

- Do not add bundled playlists, sample providers, or any content source. The app is
  bring-your-own-source only.
- Do not use `--wid` for video embedding, including as a temporary workaround. It
  creates an airspace violation that no z-order fix can resolve.
- Do not load full XMLTV or VOD JSON payloads into memory.
- Do not reference UI packages from `Iptv.Core`.
- Do not touch XAML objects from mpv callback threads.
- Do not delete user-owned rows (`channels`, `epg_map`, `playback_state`) during a
  provider sync. Sync is a merge, never a replace.
- Do not write an EPG mapping when the match is ambiguous. A wrong guide is worse
  than a missing one.

## Working notes

- Phase 0 spikes gate the architecture. Their outcomes live in `docs/decisions/`.
  Read them before touching `Iptv.Mpv` — they decide the presentation backend.
- Credentials appear in stream URLs. Anything that logs, hashes, or exports a URL
  strips the credential segment first.
- Commit messages describe what changed and why. No tool or agent attribution.
