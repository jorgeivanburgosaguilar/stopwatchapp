# IconGen

Regenerates `StopwatchApp/Assets/app.ico` (S14, `AGENTS.md` §17) from the vendored Fluent System
Icons "Timer" (filled) SVG in `vendor/fluent-timer/` — see that folder's `NOTICE.md` for source and
license. Not part of the shipped app and not referenced by `StopwatchApp.slnx` — run manually, from
the repo root:

```
dotnet run --project tools/IconGen -- StopwatchApp/Assets/app.ico
```
