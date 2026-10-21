# Archive

Retired code lives here instead of being deleted.

Everything under this directory is **not** part of any project: no `.csproj`
globs it, so nothing here is compiled, trimmed or shipped. It exists so a
decision can be re-read later with its original context intact.

## What goes here

* Members that no call site reaches any more, but whose intent is worth keeping
  (a table, a rule, a formula somebody verified against FFmpeg).
* Implementations superseded by a better one, kept next to the reason why the
  replacement is equivalent.

## What does not go here

* Dead branches inside live code — those are removed in place.
* Anything still referenced from `src/`, `tests/` or `docs/`.
* Build output, test media or log files.

## Current contents

| File | Retired in | Why |
| --- | --- | --- |
| `retired/RetiredConstants.cs` | v7.8.1 | Unreferenced lookup tables and helpers: `Constants.DefaultFpsLevels`, `Constants.BtbnGplReleaseUrl`, `Constants.LegacyEncoderPatterns` (superseded by one source-generated regex), `EncoderMath.FallbackTransportFormat()`, `AudioCatalog.Names` / `IsKnown` / `KnownFor`, `CliOptions.EffectiveRecover`. |
| `retired/RetiredTurbo.cs` | v7.9 | The Turbo mode: `RealtimeOptions.Turbo`, the three slot-loop branches plus `FrameBufferPool.WaitForFrame`/`Close`, the `--turbo` CLI plumbing and its seven localisation keys per language. Removed outright after two releases of deprecation — it fought the live-recording simulation the tool exists to produce. |

Every entry carries the version it was retired in and what replaced it, so
restoring one is a copy-paste rather than an archaeology exercise.
