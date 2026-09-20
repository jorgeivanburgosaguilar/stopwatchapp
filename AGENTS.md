# AGENTS.md — Stopwatch (Windows 11 tray time tracker)

This file is the **authoritative, standing** instruction set for any agent working on this
codebase. Work on this codebase should be possible with *only* this file loaded in context.

Keep this document synchronized with the product. Every change to functionality or architecture
must update every affected description, contract, file reference, acceptance criterion, and
decision rationale in this `AGENTS.md` as part of the same change. Do not copy implementation code
into this document when that code already exists in the repository; describe the invariant or
decision and reference the authoritative project file instead. Code blocks are reserved for
commands, documentation-only examples, and pseudocode that is not duplicated from a source file.

---

## 1. Product objective, current functionality, and scope

Stopwatch is a small, single-user Windows 11 desktop time tracker. Its purpose is to make timing a
task frictionless from either the main window or the system tray, while keeping all information on
the user's machine. It is intentionally a focused stopwatch and record browser, not a general
project-management, invoicing, monitoring, or cloud-sync product.

### 1.1 Current functionality

- Start, pause, continue, add lap splits, and stop a session.
- Show elapsed time in the main window and in a runtime-rendered tray icon. The icon shows minutes
  below one hour, an `h:mm` label from one hour through nine hours, whole hours from ten hours
  through one day, and whole days thereafter; its tooltip always shows the full unbounded `HH:mm:ss`
  duration.
- Keep the main window out of the taskbar when closed or minimized, and restore it with one left
  click on the tray icon. The tray menu mirrors the valid timer actions and is the explicit exit
  path.
- Persist completed records in SQLite, show the five newest records in the main window, and expose
  the full history in one modeless records manager with ten records per page, confirmed deletion,
  and confirmed clear-all.
- Preserve laps in memory and in a saved live-session snapshot. A running session is periodically
  autosaved at the interval configured in `settings.json` (five minutes by default); an explicit
  pause also saves immediately. Restoring either snapshot intentionally returns the session in the
  paused state.
- Ask for confirmation before Stop once a session has reached the threshold configured in
  `settings.json` (`StopConfirmationAfterMinutes`, five minutes by default, `0` disables): the clock
  pauses, a dialog asks, Stop proceeds on confirm and a running clock resumes on cancel.
- Enforce one process instance. Starting a second copy activates the existing window, including
  when it is hidden in the tray.
- Support window-scoped shortcuts: `Space` toggles Start/Pause/Continue, `Shift+Space` adds a lap,
  and `Enter` stops.
- Follow the live Windows light/dark setting, scale for per-monitor DPI, size the main window to its
  content, center it whenever it is opened, and use the embedded application icon for the executable
  and live window.
- Surface the application version in the window title.

### 1.2 Hard product constraints

- **Offline by construction.** No network calls, no telemetry, no analytics, no auto-update, no
  crash reporting — ever. New code must never add any of these.
- All data lives in one local SQLite file at `%LOCALAPPDATA%\StopwatchApp\stopwatch.db`.
- **Windows 11 only.** Do not add compatibility shims for Windows 10 or earlier.
- In scope: window-scoped keyboard shortcuts, single-instance enforcement, close-to-tray, tray
  context menu.
- Out of scope (do not add): global/system-wide hotkeys, run-at-login/startup registration,
  restoration directly into a running state, exact recovery beyond the most recent checkpoint,
  idle detection, CSV/JSON export, cloud sync, telemetry, installer/MSIX.
- **No taskbar integration** — no overlay badge, no thumbnail toolbar buttons, no progress bar,
  no title-bar clock. The tray icon is the only out-of-window surface.
- A live-session autosave is a recovery snapshot, not a running-state restore. Recovery always
  opens paused so elapsed time never grows while the application was closed.

### 1.3 Architecture and why it was chosen

The application uses a small layered architecture with hand-written constructor injection:

`Program` → `MainForm` orchestration → controls/services → `IStopwatchStore` → SQLite.

- **WinForms** is the native desktop UI layer because the product is Windows 11-only, needs a
  `NotifyIcon`, and benefits from the mature Windows message-loop and tray integration without a
  browser runtime or cross-platform abstraction.
- **`MainForm` is a composition root and thin orchestrator.** It owns application-level wiring,
  window lifecycle, and cross-component refreshes, but not timer or persistence rules. This keeps
  behavior out of event handlers and makes it testable.
- **Controls render and translate input.** They own layout, painting, button wiring, and UI-side
  timers. They communicate through events or callback delegates and never query SQLite directly.
- **`StopwatchTimer` is the UI-free state machine.** It takes `TimeProvider` and
  `IStopwatchStore`, allowing deterministic tests with fake time and an in-memory store. The real
  `System.Windows.Forms.Timer` remains in `StopwatchControl`, so tests never sleep.
- **`IStopwatchStore` is the persistence boundary.** The timer does not depend on SQLite, and the UI
  does not depend on SQL. `Database` is the only runtime implementation.
- **SQLite + Dapper** fit the deliberately small local schema. Dapper removes repetitive reader
  mapping without introducing EF Core's change tracking, design-time tooling, generated migrations,
  or analyzer/formatter friction. Hand-written, append-only `PRAGMA user_version` migrations keep
  schema evolution explicit.
- **One long-lived SQLite connection** is sufficient for a single-user, single-instance desktop
  process. Every storage operation is asynchronous, and disposal clears the SQLite pool so tests and
  shutdown release the file handle.
- **Central `Palette` and `Typography` modules** keep owner-drawn controls consistent across light
  and dark modes and DPI settings. Shared `ButtonFactory` and rounded-rectangle code avoid subtly
  different copies of the same visual behavior.
- **x64 is the only supported architecture.** Both projects and the solution explicitly map to
  `x64`, and publishing targets `win-x64`. Supporting `AnyCPU`, x86, ARM64, Windows 10, macOS,
  Linux, mobile, or the broader set of platforms supported by .NET is not a project goal.

---

## 2. Verified environment

| Item | Value |
|---|---|
| .NET SDK | `10.0.400` installed and verified (`dotnet --version`) |
| Target framework | `net10.0-windows` |
| OS | Windows 11 Pro (build 10.0.26200) |
| Shell | PowerShell 7 primary; POSIX bash also available |
| Repo root | `D:\Desarrollo\stopwatch` (git repo) |
| Distribution runtime | **.NET 10 Desktop Runtime** on the target machine (framework-dependent publish — WinForms ships with the desktop bundle, not the base runtime) |

---

## 3. Repo layout and component contracts

```
.editorconfig              style + naming rules — from the user's config repo (§5), plus project overrides
.gitattributes             from the user's config repo (§5)
.gitignore                 bin/, obj/, publish/, *.db — base file from the user's config repo (§5), *.db added
AGENTS.md                  this file
StopwatchApp.slnx          solution, XML format (the .NET 10 SDK default); x64 is the only solution platform
StopwatchApp/
  StopwatchApp.csproj      net10.0-windows, WinForms, nullable, analyzers-as-errors, x64
  Program.cs               single-instance mutex, ApplicationConfiguration.Initialize, SetColorMode, Application.Run
  MainForm.cs              thin orchestrator — wires controls and services, no business logic
  Assets/
    app.ico                window/taskbar/.exe icon (§10.3), embedded resource
    emoji/                 calendar/stopwatch/hourglass color PNGs for the row icons (§8.5), embedded
    NOTICE.md              Fluent System Icons (app.ico) and Noto Color Emoji (emoji/) attribution
  Controls/                rendering + event wiring only, no untestable business logic
    StopwatchControl.cs    timer state (§8) + control row (§8.5) + keyboard shortcuts (§10.4)
    StopwatchShortcut.cs   the three window-scoped shortcuts (§10.4)
    ButtonFactory.cs       shared stock, palette-colored Button used across the application
    RecordsListControl.cs  records and laps list rendering
    IconTextLayout.cs      the one measure/wrap/draw engine for record and lap rows (§8.5)
    IconTextLabel.cs       display-only label that draws a row through IconTextLayout
    RowIcon.cs             the three row icons; RowIconSet.cs owns their decoded bitmaps
    ClearRecordsDialog.cs  confirm dialog
    StopConfirmationDialog.cs  Stop confirmation dialog (§8.3/§8.5)
    DeleteRecordDialog.cs  per-record delete confirmation
    ManageRecordsForm.cs   modeless, paginated full-history window
  Models/                  one record type per file (§5)
    StopwatchRecord.cs     a persisted record (§8.1/§9)
    Lap.cs                 an in-memory/restored split (§8.1)
    PausedSession.cs       the single saved-session snapshot (§9)
  Services/                UI-free, unit-testable
    AppSettings.cs         validated settings.json loader (autosave cadence, stop-confirmation threshold)
    StopwatchTimer.cs      tick loop, autosave checkpoints, transitions, and records ownership (§8)
    TrayIconService.cs     NotifyIcon, rendered icon, context menu (§10)
    IStopwatchStore.cs     the data-access interface (§3.1/§9)
    Database.cs            SQLite access via Dapper (§9)
    SchemaMigrations.cs    versioned schema steps, PRAGMA user_version (§9)
  settings.json            deployable configuration: autosave cadence and stop-confirmation threshold
  Formatting/
    TimeFormat.cs          the four formatters (§8.4)
  Theme/
    Palette.cs             light/dark color tables (§11)
    RoundedRectangle.cs    shared rounded-path geometry for owner-drawn controls
    Typography.cs          type scale + monospace font resolution (§11)
StopwatchApp.Tests/        xUnit — state, settings, layout helpers, UI-free rendering rules, SQLite
```

Controls hold rendering and event wiring only — no logic that can't be tested outside a form.
`MainForm` never contains business logic. UI code never touches SQLite directly; everything goes
through `Services/Database.cs`. Parent/child communication happens through events or callback
delegates with no-op defaults, never shared mutable state.

### Architecture map and most important files

- `StopwatchApp/Program.cs` is the entry point. It acquires the single-instance mutex, activates an
  existing instance when necessary, initializes WinForms, reads `settings.json`, and constructs the
  main form.
- `StopwatchApp/MainForm.cs` is the composition root. It creates the database, stopwatch control,
  records view, records manager, and tray service; wires their events; applies theme/DPI changes;
  and owns close/minimize/open/exit behavior.
- `StopwatchApp/Services/StopwatchTimer.cs` is the authoritative state machine and owner of the
  in-memory laps and records collections. Business behavior belongs here, not in a form.
- `StopwatchApp/Controls/StopwatchControl.cs` presents the chrono and state-dependent controls. Its
  one-second WinForms timer calls `Tick()` and then the autosave checkpoint method.
- `StopwatchApp/Services/IStopwatchStore.cs` is the fixed persistence contract. Changes to it are
  architecture changes and must be reflected in the fake store and both consumers.
- `StopwatchApp/Services/Database.cs` is the sole SQLite access layer. It owns connection lifetime,
  Dapper queries, snapshot serialization, and the intentional storage-error policy.
- `StopwatchApp/Services/SchemaMigrations.cs` is the append-only database history. Never rewrite an
  already-shipped migration; append a new numbered migration.
- `StopwatchApp/Controls/RecordsListControl.cs` renders laps and the five-record preview;
  `ManageRecordsForm.cs` renders the complete paginated history and routes mutations back through
  `StopwatchTimer`.
- `StopwatchApp/Services/TrayIconService.cs` owns `NotifyIcon`, its native icon handle, icon text,
  tooltip, click behavior, and context menu. It must release both the icon handle and tray icon.
- `StopwatchApp/Theme/Palette.cs` and `Typography.cs` are the design tokens. Do not introduce
  one-off colors, font sizes, or font-family selection in individual controls.
- `StopwatchApp.Tests/FakeStopwatchStore.cs` is the shared in-memory persistence test double;
  `DatabaseTests.cs` is the integration boundary that exercises real temporary SQLite files.

### 3.1 Component contracts

These contracts are fixed architecture, not suggestions; changing them is a design change, not a
refactor. Their authoritative declarations are in
`StopwatchApp/Services/IStopwatchStore.cs`, `StopwatchApp/Services/StopwatchTimer.cs`,
`StopwatchApp/Controls/StopwatchControl.cs`, `StopwatchApp/Services/TrayIconService.cs`,
`StopwatchApp/Controls/RecordsListControl.cs`, `StopwatchApp/Controls/ClearRecordsDialog.cs`,
`StopwatchApp/Controls/StopConfirmationDialog.cs`,
`StopwatchApp/Formatting/TimeFormat.cs`, and `StopwatchApp/Theme/Palette.cs`. Consult those files
for exact signatures rather than duplicating them here.

`IStopwatchStore` is the §9 data-access boundary. `Database` implements it and `StopwatchTimer`
depends only on it, so the state machine remains independently testable. The retained window
position methods are migration-era persistence surface; the current UI deliberately does not use
them.

`StopwatchTimer` is UI-free and owns the timer state, laps, and newest-first records collection.
Its asynchronous method suffixes are load-bearing: pausing persists a snapshot and stopping awaits
a record write, so callers must await them and must never replace that flow with `.Wait()` or
`.Result` (forbidden by §5).

**Clock and tick separation.** The constructor takes a `TimeProvider`, and the tick body reads
`_time.GetUtcNow().ToUnixTimeMilliseconds()` — identical to `DateTimeOffset.UtcNow` under
`TimeProvider.System`, so §8.2's wall-clock rule holds. The actual
`System.Windows.Forms.Timer` lives in `StopwatchControl`, which calls the public `Tick()` once a
second; `StopwatchTimer` itself owns no WinForms `Timer`. This split is what makes §13's "never
`Thread.Sleep`/`Task.Delay` in a test" rule achievable — tests advance a `FakeTimeProvider` and call
`Tick()` directly.

**Records ownership.** §8.3's duplicate guard reads `Records[0].StartTimestamp`, so
`StopwatchTimer` — not `MainForm`, not `RecordsListControl` — owns the records list and its reload
(both through `IStopwatchStore`), and raises `RecordsChanged` on every change. `RecordsListControl`
only renders from lists pushed to it and raises `ClearAllRequested`; it never reads
`IStopwatchStore` itself. `MainForm` wires the two together and stays a thin orchestrator.
`RestoreAsync` reloads `Records` (in addition to restoring a paused session, if any) so the list is
populated before the caller ever reads it — a fresh `StopwatchTimer` has no other point at which
`Records` is first loaded.

The tray, shortcuts, and buttons all route through `StopwatchControl`'s shared action methods so
transitions and display refreshes cannot diverge. `TrayIconService` takes callbacks rather than a
`MainForm` reference. `RecordsListControl` accepts pushed record and lap lists, raises requests for
record-management actions, and never reads `IStopwatchStore`. The clear-records dialog returns Yes
only for explicit confirmation; Cancel, Escape, and closing the window all cancel the operation.

---

## 4. Commands and post-change checklist

| Command | Purpose |
|---|---|
| `csharpier format .` | Formats the whole repo (whitespace, wrapping, brace style) per `.editorconfig`. |
| `csharpier check .` | Check-only; exits `1` if anything is unformatted. Use before presenting any change as complete. |
| `dotnet build -c Release` | Compiles with analyzers enabled and warnings treated as errors. |
| `dotnet test` | Runs the full test suite. |

**Formatting is CSharpier, not `dotnet format`.** This project uses the CLI
(`csharpier format`/`check`) exclusively — there is no `CSharpier.MSBuild` package
reference, so formatting is not enforced at build time and must be run explicitly. **Never run
`dotnet format`** on this repo: it fights CSharpier over whitespace and brace placement and will
undo or conflict with its output. CSharpier is installed as a **global** dotnet tool
(`dotnet tool install -g csharpier`, currently `1.3.0`), so the command is bare `csharpier`, not
`dotnet csharpier` — the latter requires a local tool manifest, which this repo deliberately does
not have.

Run after **every** completed change, before presenting the result as done:

1. `csharpier format .`
2. `csharpier check .` — must exit `0`
3. `dotnet build -c Release` — must produce **zero warnings**, not just zero errors
4. `dotnet test` — all tests green
5. If tray, hotkey, or window-hiding code changed: launch the app once and manually confirm the
   tray icon renders and updates, the tooltip shows `HH:MM:SS`, both close and minimize hide the
   window, and Exit leaves no ghost icon behind.

---

## 5. Code style

- C# 13, target `net10.0-windows`. `<Nullable>enable</Nullable>` — no `#nullable disable` anywhere,
  and no `!` null-forgiving operator used to silence a warning; fix the actual flow instead.
- `<ImplicitUsings>enable</ImplicitUsings>`, file-scoped namespaces, one type per file.
- `var` only when the type is obvious from the right-hand side; explicit types otherwise.
- `readonly` fields by default; `sealed` on classes not designed for inheritance; immutable
  `record` types for data models (`StopwatchRecord`, `Lap`, `PausedSession`).
- `async`/`await` all the way down for SQLite access — **never** `.Result` or `.Wait()` on the UI
  thread. Suffix async methods with `Async`.
- Proper `IDisposable`/`IAsyncDisposable` on anything holding a native handle, timer, or database
  connection; prefer `using` declarations over manual `Dispose()` calls.
- No static mutable state. Construct services once (in `Program`/`MainForm`) and pass them
  explicitly — hand-rolled constructor injection; a DI container is unnecessary at this size.
- Catch exceptions narrowly. The storage layer's swallow-everything behavior (§9) is a deliberate,
  specified design — keep the comment that says so next to the code; don't let it read as sloppy
  error handling.
- All cross-thread UI updates go through `Control.Invoke` — tray icon and global hotkey callbacks
  can arrive off the UI thread.
- UI event handlers that `await` a `Services/` method (e.g. `StopwatchTimer.PauseAsync`) must not
  use `ConfigureAwait(false)` on that outer `await` — letting it capture the UI
  `SynchronizationContext` is what guarantees code after the `await` (a `UpdateDisplay()`-style
  refresh) runs back on the UI thread, even though the awaited method's own internals use
  `ConfigureAwait(false)` (§9) and may complete off-thread. For the same reason, controls must not
  subscribe UI-mutating code directly to a `Services/` type's events (`StopwatchTimer.OnPause`, etc.)
  — those fire from inside the service's own `ConfigureAwait(false)` continuation and may not be on
  the UI thread; call the refresh explicitly after the awaited call instead.
- XML doc comments on every public member of `Services/` and `Formatting/`.
- Every format/parse call passes `CultureInfo.InvariantCulture` explicitly (CA1305 flags
  omissions).

### `.editorconfig`, `.gitattributes`, `.gitignore` — source and summary

Canonical source: `https://github.com/jorgeivanburgosaguilar/Configuraciones/tree/main/dotnet`. All
three files are copied from there into the repo root as part of initial scaffolding, then two
project-specific additions are layered on top:

- `.gitignore` gets `*.db` appended (the runtime SQLite file is user data, never committed).
- `.editorconfig` gets `dotnet_diagnostic.IDE0055.severity = none` appended under `[*.{cs,vb}]` —
  **required**, not optional: `IDE0055` is the built-in formatting rule, and with
  `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` (§6) it will fight CSharpier's output and
  break `dotnet build -c Release` unless silenced.

Real settings from the upstream `.editorconfig` (do not "correct" these to a generic default):
root (`[*]`) is `indent_style = space`, `indent_size = 2`, `end_of_line = lf`, `charset = utf-8`;
`[*.{cs,vb}]` overrides to `end_of_line = crlf`, `charset = utf-8-bom`,
`insert_final_newline = false`, keeps `indent_size = 2`; `max_line_length = 100` everywhere.
`using` directives sorted system-first; braces required on every block
(`csharp_prefer_braces = true`); expression-bodied members only for genuinely single-line members;
naming: `I`-prefixed PascalCase for interfaces, PascalCase for types and non-field members,
`_camelCase` for private fields.

CSharpier reads `.editorconfig` directly for `indent_size`, `end_of_line`, and
`max_line_length` (used as its print width) — there is no separate `.csharpierrc`; changing wrap
width or indentation means editing `.editorconfig`, not adding a CSharpier-specific config file.

---

## 6. Required `.csproj` properties

The authoritative values are in `StopwatchApp/StopwatchApp.csproj`; do not duplicate its XML here.
Keep its target framework, WinForms settings, nullable and implicit-usings settings, x64 platform
and runtime selection, framework-dependent publishing, version metadata, unsafe-block allowance,
high-DPI settings, application icon, analyzer gates, package references, embedded icon and row-emoji images (§8.5), and
deployable `settings.json` configuration intact unless the corresponding requirement changes.

- `<Platforms>` only declares which platforms exist for the project; it does not select the active
  one. `<Platform>x64</Platform>` pins that selection so a direct `dotnet build <csproj>` (no
  solution involved) also lands in `bin\x64\...` — the solution file (`StopwatchApp.slnx`) separately
  declares x64 as its only platform, so a solution-level build stays consistent with it. Both are
  required; setting only one leaves the other build path on `AnyCPU`.
- `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` and `<PublishSelfContained>false</PublishSelfContained>`
  make plain publishing produce the supported Windows x64 framework-dependent artifact. Do not add
  additional runtime identifiers as speculative platform support.
- `EnableNETAnalyzers` defaults on for .NET 5+, but set it explicitly so intent survives future
  edits.
- `EnforceCodeStyleInBuild` promotes `IDExxxx` code-style rules from IDE-only hints to build
  diagnostics — without it, `.editorconfig` rules are invisible outside an IDE.
- `TreatWarningsAsErrors` gives every rule real teeth.
- Escape hatch: `<CodeAnalysisTreatWarningsAsErrors>false</CodeAnalysisTreatWarningsAsErrors>`
  demotes only `CAxxxx` code-quality rules, not compiler warnings. Any suppression must be
  narrow — a justified `[SuppressMessage]` attribute or a scoped
  `#pragma warning disable ... / restore ...` pair — **never** a blanket `<NoWarn>` list.
- The version lives in exactly one place (`<Version>` in the application project) and is surfaced through
  `Application.ProductVersion` in the window title. Bump it in the same change as any user-visible
  behavior change. Keep `IncludeSourceRevisionInInformationalVersion` false so a Git hash does not
  leak into that user-facing title.
- **CSharpier vs. these analyzers:** whitespace/formatting is owned entirely by CSharpier (§4), not
  by this block. `EnableNETAnalyzers`/`AnalysisLevel`/`EnforceCodeStyleInBuild` still enforce every
  `IDExxxx`/`CAxxxx` *code-quality* rule at build time — only `IDE0055` (pure formatting) is turned
  off, in `.editorconfig` (§5), so the CSharpier gate and the analyzer gate don't deadlock over
  whitespace. There is no `CSharpier.MSBuild` package reference in this project — CLI only, run
  explicitly per §4's checklist.

---

## 7. .NET 10 WinForms rules

- Call `ApplicationConfiguration.Initialize()` in `Program.Main` — it is source-generated from the
  `.csproj` properties in §6. **Never hand-write** `Application.EnableVisualStyles()`,
  `Application.SetCompatibleTextRenderingDefault(false)`, or `Application.SetHighDpiMode(...)` —
  the WinForms compiler analyzers (`WFO0001`/`WFO0002`/`WFO0003`) flag this, and with
  `TreatWarningsAsErrors` it fails the build.
- High DPI is configured via the `.csproj` (`ApplicationHighDpiMode`), **not** an `app.config`
  `System.Windows.Forms.ApplicationConfigurationSection` block — that path is .NET Framework
  legacy and does not apply to this SDK-style project. Handle the form's `DpiChanged` event to
  re-render the tray icon at the new size when DPI changes at runtime:
  `MainForm.OnDpiChanged` calls `TrayIconService.RefreshIcon()`.
- Live OS dark/light-mode detection and reaction (`Application.IsDarkModeEnabled`,
  `SystemEvents.UserPreferenceChanged`) filters to the relevant color/general preference changes.
- Keep the initialization call order implemented in `StopwatchApp/Program.cs`: initialize the
  generated application configuration, select the system color mode, and then run `MainForm`.
- `Application.SetColorMode(SystemColorMode.System)` — `SystemColorMode` is
  `Classic | System | Dark`. This is a **stable** API in .NET 10 (it required an experimental
  `WFO5001` opt-in in .NET 9 only; that restriction no longer applies). It themes window chrome
  and stock controls, but **not** custom-painted surfaces — anything drawn manually must read its
  colors from `Theme/Palette.cs` (§11). Compare `Color` values via `.ToArgb()`, not `==`.
- Do not reach for .NET 10's newer WinForms surface area unless a requirement calls for it — async
  forms (`ShowAsync`/`ShowDialogAsync`), the new clipboard APIs, and `ScreenCaptureMode` are out of
  scope for this app.

---

## 8. Behavioral contract

All timer state lives in one control (`StopwatchControl`/`StopwatchTimer`); nothing is computed
reactively — every field is written imperatively by the transition methods below.

### 8.1 State fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `IsRunning` | bool | `false` | drives the timer |
| `IsPaused` | bool | `false` | distinct from idle (neither running nor paused) |
| `ElapsedMs` | long | `0` | **written only by the 1-second tick**; never zeroed by Stop |
| `StartTime` | epoch ms | `0` | anchor = `Now - ElapsedMs`, re-set on every start/resume |
| `SessionStartMs` | epoch ms | `0` | wall-clock start of the whole session; survives pause; set to `0` at the end of Stop; doubles as the "a session exists" guard |
| `Records` | list | empty | loaded from SQLite, newest first |
| `Laps` | list | empty | in-memory splits, newest first (each new lap is prepended) |
| `LastLapElapsed` | long | `0` | `ElapsedMs` value at the last lap |
| `LastLapTimestamp` | epoch ms | `0` | wall clock of the last lap |
| `RestoredPausedAtMs` | epoch ms | `0` | greater than `0` only after restoring a saved paused session |
| `ShowClearDialog` | bool | `false` | confirm-dialog visibility |

The authoritative record declarations are `StopwatchApp/Models/StopwatchRecord.cs`,
`StopwatchApp/Models/Lap.cs`, and `StopwatchApp/Models/PausedSession.cs`.

### 8.2 Timing loop

The `System.Windows.Forms.Timer` in `StopwatchApp/Controls/StopwatchControl.cs` has a 1000 ms
interval and is enabled only while running. Each tick delegates to
`StopwatchApp/Services/StopwatchTimer.cs`, which replaces `ElapsedMs` with the absolute difference
between the current Unix-millisecond clock and the start anchor. It raises the tick callback at
positive five-second elapsed boundaries.

After `Tick()`, `StopwatchControl` awaits `SaveAutosaveIfDueAsync()`. While running, that method
persists the current session into the same single snapshot slot whenever accumulated running time
reaches the next configured interval. The cadence comes from `settings.json` beside the executable;
missing, unreadable, malformed, zero, or negative values fall back to five minutes. Paused time does
not advance the checkpoint. Snapshot writes and Stop's snapshot deletion share a `SemaphoreSlim`, so
an in-flight autosave can never recreate a recovery snapshot after Stop clears it.

Clock source is `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` — **not**
`System.Diagnostics.Stopwatch`. The wall-clock anchor is deliberate: a late/delayed tick
self-corrects on the next tick instead of accumulating drift, and elapsed time is defined against
real time, not a monotonic counter.

Dispose the timer on form close. No ticks may fire after teardown.

### 8.3 State transitions

The authoritative transition implementation is in
`StopwatchApp/Services/StopwatchTimer.cs`; its deterministic coverage is in
`StopwatchApp.Tests/StopwatchTimerTests.cs`. Preserve these behavioral invariants:

- Starting while already running is a no-op. A fresh start resets elapsed time and laps; continuing
  a paused session preserves them and re-anchors the clock so paused time is excluded.
- Pausing is a no-op unless running. It freezes the displayed elapsed time and saves the complete
  recovery snapshot before raising the pause event.
- Adding a lap is a no-op unless running. Each newest-first lap represents only the interval since
  the previous lap, not a cumulative duration.
- Stopping clears the recovery snapshot through the same persistence gate used by autosave. If
  laps exist, it adds any remaining positive partial split. A positive session is saved once,
  guarded against duplicating the newest record after recovery, and the records collection is
  reloaded.
- **Stop confirmation.** `StopwatchControl.StopTimerAsync` is the single gate every Stop entry point
  (button, `Enter`, all tray-menu variants) passes through. When
  `StopwatchControl.RequiresStopConfirmation` is true — threshold above zero, a session is running
  or paused, and elapsed time has reached `StopConfirmationAfterMinutes` — a running clock is first
  paused through the normal `PauseTimerAsync` (snapshot saved, display frozen), then the
  `ConfirmStop` callback (supplied by `MainForm`, which restores a hidden window first) is asked.
  Confirming performs the normal stop. Cancelling resumes through `StartTimer` if the clock was
  running (paused time excluded, as with any pause/continue) and leaves an already-paused clock
  paused. A threshold of `0` (or a negative configured value) disables the confirmation; below the
  threshold, and while idle, Stop behaves exactly as before.

**There is no Reset button.** Reset is implicit in the next `Start()`.

### 8.4 Formatters

Four pure functions. All use `CultureInfo.InvariantCulture` explicitly.

| Function | Signature | Output | Notes |
|---|---|---|---|
| `FormatTime` | `(long ms) → string` | `HH:mm:ss` | Hours are **not** clamped — 100 hours renders as `100:00:00`. Build the string manually; **do not** use `TimeSpan.ToString(@"hh\:mm\:ss")`, which wraps at 24 hours. |
| `FormatDate` | `(long unixMs) → string` | `yyyy-MM-dd` | **Local** time (convert from stored epoch-ms UTC to local before formatting). |
| `FormatTimeOnly` | `(long unixMs) → string` | `HH:mm:ss` | **Local** time, 24-hour clock. |
| `FormatElapsed` | `(long minutes) → string` | `hh:mm` | **Hard 1-minute floor**: input `0` still renders `"00:01"`. Display-only floor — the *stored* value may legitimately be `0`. |

Worked examples for `FormatElapsed`: `0 → "00:01"`, `1 → "00:01"`, `60 → "01:00"`, `90 → "01:30"`.

The authoritative implementation is `StopwatchApp/Formatting/TimeFormat.cs`; formatter behavior is
covered by `StopwatchApp.Tests/TimeFormatTests.cs`.

### 8.5 UI inventory

Control row by state — exact labels and order:

| State | Buttons shown (left → right) | Color intent |
|---|---|---|
| idle (not running, not paused) | Start, Stop | green, red |
| running | Pause, Lap, Stop | yellow, blue, red |
| paused | Continue, Stop | green, red |

Rules:

- No button is ever disabled. State is expressed purely by which buttons are present.
- Stop while idle is a harmless no-op (the `ElapsedMs > 0 && SessionStartMs > 0` guard in `Stop()`
  absorbs it).
- **Type scale, defined centrally in `Theme/Typography.cs`:** display (elapsed-time
  readout) 72pt/96px bold; body (buttons, headers, dialogs, list rows) 12pt/16px; caption 9pt/12px —
  all at the 96dpi design baseline (`1pt = 4/3px`), scaling further with the OS DPI setting via
  `MainForm`'s `AutoScaleMode.Dpi`. The caption size has had no consumer since the version footer was
  removed, but stays defined — it is still §11's third type-scale step, and
  `TypographyTests` still covers `Typography.CreateCaptionFont`.
- The large elapsed-time display and the records/laps rows both use a monospace, tabular-figure font
  (`Typography.MonospaceFamilyName` — Cascadia Mono or Consolas) so digits don't shift width as they
  change and so dates/times column-align down the list.
- **Centering:** the elapsed-time display and the button row are both centered on the
  stopwatch card's horizontal axis via `Anchor = AnchorStyles.None` inside a `TableLayoutPanel` cell
  (not `Dock` + `TextAlign`, which is a no-op on an `AutoSize` control). The button row re-centers
  automatically as buttons swap per state.
- A **Laps** panel is shown only when `Laps.Count > 0`, newest first, scrollable — capped to 3 rows
  tall (the tallest 3 rows actually shown, not a flat multiple of a single-line height).
- A **Records** panel is always shown, newest first, with an empty state reading `No records yet`
  when there are none. **Only the most recent 5 are listed**
  (`RecordsListControl.MaxDisplayedRecords`); the records manager provides the full list with
  deletion. This preview panel is capped and not scrollable.
- **Header row:** the `Records` label and the two action buttons below share one line —
  `Records` left-aligned, `Manage Records` then `Clear All Records` right-aligned — replacing the former
  stacked label-then-button-row layout, per the repo owner's markup of a screenshot: "the buttons and
  the title 'Records List' should be on the same line."
- **Row word-wrap, not clip:** a records/laps row past the sized-for worst case (a session
  over `MainForm.WorstCaseElapsedMinutes`, a lap id past 3 digits) word-wraps to a second line
  (`RecordsListControl.MeasureRowHeight`/owner-drawn `DrawMode.OwnerDrawVariable`), instead of the former
  `EndEllipsis` clipping. Each list box's, and the records host panel's, height is recomputed on
  every `UpdateRecords`/`UpdateLaps` call from the real (possibly wrapped) height of the rows actually
  shown — never a flat per-row constant — so the window (§10.3) can size itself to match.
- A `Clear All Records` button is visible only when `Records.Count > 0` (the *actual* total, not the
  capped display count) and, when clicked and confirmed, clears every persisted record — not just
  the 5 shown. Styled red (`Palette.StopButton`) via the shared `ButtonFactory` (below),
  matching the destructive-action color used elsewhere.
- A **`Manage Records`** button sits beside `Clear All Records`, is always enabled, and
  opens one modeless manager window. Styled blue (`Palette.LapButton`).
- The manager lists every saved record newest first, 10 per page, with Previous/Next navigation and
  confirmed per-row `Delete` actions. Its width is a fixed budget for a worst-case row with a `23:59` duration
  (`ManageRecordsForm.WorstCaseElapsedMinutes`), not a function of the page shown; a longer row wraps
  to a second line instead of widening the window. Its header also has a confirmed `Clear All Records` action;
  the main card's own Clear All shortcut remains.
- **`ButtonFactory`:** every button in the app (`Controls/ButtonFactory.cs`) is a stock
  `Button` with `FlatStyle.Flat` and a `Palette` base/hover/pressed triplet applied to
  `BackColor`/`FlatAppearance.MouseOverBackColor`/`MouseDownBackColor`. This replaced an
  owner-drawn rounded button (`GlyphButton`, removed) so buttons get the native keyboard-focus
  indicator and dark-mode/high-contrast behavior for free — at the cost of square corners, the
  one visual it gave up (§17). Disabled buttons use the stock disabled rendering, not a
  palette-driven blend.
- A confirm dialog, titled `Clear All Records`, body text
  `Are you sure you want to clear all records? This action cannot be undone.`, buttons `Cancel` and
  `Clear All`, both from the shared `ButtonFactory` — `Clear All` red (`Palette.StopButton`),
  `Cancel` dark slate (`Palette.CancelButton`, a new non-destructive-action token) — with equal
  top/bottom margins so the two sit on the same baseline (the former mismatched default/explicit margins
  had misaligned them). Confirming clears the records table and reloads the (now empty) list.
- The Stop confirmation dialog (`Controls/StopConfirmationDialog.cs`), titled `Stop Stopwatch`,
  body text `The stopwatch is at {FormatTime(elapsed)}. Stop it and save this session as a record?`,
  buttons `Cancel` (dark slate, `Palette.CancelButton`) and `Stop` (red, `Palette.StopButton`), laid
  out like the Clear All dialog. It has no `AcceptButton` — `Enter` is the Stop shortcut, so the
  keypress that opened it must not also confirm it; Cancel, Escape, and the close box all cancel.
- A note reading `Resumed from a pause on {date} at {time}` (via `FormatDate`/`FormatTimeOnly` on
  `RestoredPausedAtMs`) appears under the timer only after restoring a saved session. It persists
  through Continue and is cleared by Stop or a fresh Start.

Row text templates, literal (including the emoji):

```
Lap:    📅 {date} ⏱ {start}-{end} ⏳ Lap {id}: {formatElapsed}
Record: 📅 {date} ⏱ {start}-{end} ⏳ Duration: {formatElapsed}
```

Where `{date}` = `FormatDate(startTimestamp)`, `{start}`/`{end}` = `FormatTimeOnly(...)`, and
`{formatElapsed}` = `FormatElapsed(elapsedMinutes)`.

The strings above are the row *text* contract (`RecordsListControl.FormatRecordRow`/`FormatLapRow`
still produce them verbatim), but the three emoji are **rendered as embedded full-color images**,
not as text. `TextRenderer` (GDI) cannot paint color font tables, so drawing the literal glyphs
yields the color font's monochrome fallback outline tinted with the row's text color. Every row —
in the main window's `ListBox`s and in `ManageRecordsForm` — is therefore measured and drawn through
`Controls/IconTextLayout.cs`, which tokenizes the row into words, spaces, and icons; word-wraps
greedily; treats each icon as an atomic square of the font's line height (so DPI scaling is
inherited from the already-scaled font); and draws icons from `Controls/RowIconSet.cs`. Anything
that budgets a row's width (`MainForm.RequiredClientWidth`, `ManageRecordsForm.RequiredClientWidth`)
must measure through `IconTextLayout`, never plain `TextRenderer`, or the icons' extra width is
missed. `ManageRecordsForm` uses `Controls/IconTextLabel.cs`, not a stock `Label`.

### 8.6 Callbacks

Four events with no-op defaults so the control works standalone:

| Event | Fires when | Payload |
|---|---|---|
| `OnStart` | End of `Start()`, when it actually transitions to running | `ElapsedMs` |
| `OnPause` | End of `Pause()`, only if it was running, after the session snapshot is saved | `ElapsedMs` |
| `OnTick` | Inside the 1-second tick, only at 5 s, 10 s, 15 s… of elapsed time, never while paused or stopped | `ElapsedMs` |
| `OnStop` | In `Stop()`, before the database write, only when `ElapsedMs > 0 && SessionStartMs > 0` | `ElapsedMs`, `SessionStartMs`, `EndTimestamp` |

---

## 9. Data layer

SQLite via `Microsoft.Data.Sqlite`, database file at `%LOCALAPPDATA%\StopwatchApp\stopwatch.db`,
created on first run if it doesn't exist. Rows are mapped via **Dapper** — by column name, against
the model records' own positional constructors — not by hand-written ordinal `reader.Get*` calls.
Dapper is a thin extension-method layer over `Microsoft.Data.Sqlite`'s `SqliteConnection`; it does
not replace the provider, add a design-time tool, or generate any code, so it carries no build-gate
cost. Parameters use SQLite's `@name` form (Dapper does not recognize the `$name` form used before
this layer was introduced).

The authoritative DDL is migration 1 in `StopwatchApp/Services/SchemaMigrations.cs`, not code run
unconditionally on every startup. It defines the completed-record table and the single-slot paused
session table; see "Schema versioning" below.

Migration 2 in the same file adds the single-slot window-position table originally created for
position persistence (§10.6).

### Schema versioning

`Database.InitializeAsync` brings the database up to date using SQLite's `PRAGMA user_version` as a
version stamp, against the ordered, append-only list in `SchemaMigrations.All`:

- Each entry is a `Migration(Version, Sql)`. On startup, every migration whose `Version` exceeds the
  database's current `user_version` runs, in order, each inside its own transaction, then stamps
  `user_version` to that migration's version.
- **Never edit a shipped migration.** A schema change is always a new entry appended with the next
  version number — editing an existing one silently no-ops on any database that already recorded
  that version as applied.
- **Migration 1 keeps `IF NOT EXISTS`; migration 2 onward does not.** Migration 1 reproduces the DDL
  above verbatim so a database that predates schema versioning (already has both tables, but
  `user_version = 0`) adopts them as its v1 baseline instead of failing on a duplicate table. Once
  the version stamp exists, every later migration is guaranteed to run exactly once, so plain
  `CREATE TABLE` / `ALTER TABLE` is correct and `IF NOT EXISTS` would only hide an ordering bug.
- `PRAGMA user_version = N` cannot be parameterized; `N` always comes from the hardcoded `Version` on
  a `Migration`, never user input, so the interpolated statement carries no injection surface (keep
  the comment next to it saying so — this is exactly the shape `CA2100` flags).
- `InitializeAsync` is idempotent: a second call against an already-open connection does not reopen
  it, and a database already at `SchemaMigrations.Current` applies nothing.

The deliberately minimal API is declared in `StopwatchApp/Services/IStopwatchStore.cs` and
implemented in `StopwatchApp/Services/Database.cs`. There is no record-update or range-query
surface. It supports inserting and loading newest-first records, deleting one or all records,
single-slot paused-session persistence, and the retained migration-compatible window-position
methods that the current UI intentionally does not call. Record insertion floors elapsed
milliseconds to whole minutes before storage, and an unreadable saved session loads as `null`.

Every storage method is wrapped in try/catch and **swallows errors**: a corrupt or unreadable saved
session must return `null` rather than throw, and a failed write must never surface an exception to
the UI. **This is deliberate — keep a comment next to the code saying so**, so it doesn't read as
an oversight.

Restore runs once at startup, after the database connection opens: if a paused session exists, the
UI shows the frozen elapsed time, a `Continue` button, the restored laps, and the "resumed from a
pause" note (§8.5). **Time spent away while the app was closed is never counted** — resuming
re-anchors `StartTime` from the current clock, exactly as an in-app pause/resume does.

---

## 10. Windows integration

### 10.1 Tray icon rendering

Render a 32×32 icon with GDI+ and convert it to an `HICON`. It uses one simple, unbounded
progression while the tooltip remains authoritative:

- **Elapsed time under one hour**: a single large two-digit **MM** readout, centered and sized to
  fill as much of the 32×32 canvas as legibility at the 16 px scaled-down display size allows.
- **Elapsed time from one hour through 9:59:59**: a single large `h:mm` label — an unpadded hour, a
  colon, and zero-padded minutes (`1:00` through `9:59`); it changes every minute, restoring the
  minute resolution the whole-hour label below would otherwise lose for up to an hour at a time.
- **Elapsed time from ten hours through 23:59:59**: a single large unpadded whole-hour label, `10H`
  through `23H`; it changes only at the next whole hour.
- **Elapsed time from one day onward**: a single large unpadded whole-day label, `1D`, `2D`, and so
  on; it changes only at the next whole day.

All four layouts share one auto-fit rule: the largest Segoe UI Bold size, from a fixed max down to
a fixed floor, whose **glyph ink** — a `GraphicsPath` built with `AddString`, measured via
`GetBounds()` — fits the 32×32 canvas. Measure ink, not `MeasureString` output: `MeasureString`
(especially with `StringFormat.GenericDefault`) reports a padded advance width and the full
line-box height rather than the glyph's actual extent, so it rejects sizes that would truly fit —
that gap, not `23H` genuinely needing more room, is what previously made `1H` render at a fraction
of the minutes readout's size. Sharing one rule means the common `1D`/`10H` labels reach essentially
the same size as `45`; the four-glyph `h:mm` label (`1:01`, etc.) is genuinely smaller than every
other layout, because four glyphs don't fit the canvas at the size two- or three-glyph labels reach
— this is an accepted, intentional trade-off for restoring minute resolution, not a fit bug to
chase. A paused stopwatch freezes whichever layout it reached; running, paused, and idle may differ
in tint but not layout.

Update the icon at most once per second, and only when the displayed value or active layout changes
— track both explicitly in the dirty-check state.

**Call `DestroyIcon` on the previous handle every time you replace it.** Forgetting this is the
single most common bug in this pattern and leaks GDI handles until the process is killed.

Tooltip text is `FormatTime(ElapsedMs)` — the full, unbounded `HH:MM:SS` value — plain text, no
prefix or emoji, regardless of which layout is active.

### 10.2 Tray context menu

Order: `Open`, separator, the state-appropriate action(s) from §8.5 (`Start`/`Pause`/`Continue`/
`Lap`/`Stop`), separator, `Exit`. Single left-clicking the tray icon opens (restores and activates)
the main window; right-click remains reserved for the context menu. `Exit` is the only way to quit
the application.

### 10.3 Window behavior

Intercept `FormClosing` when `e.CloseReason == CloseReason.UserClosing`: cancel the close, hide the
form, set `ShowInTaskbar = false`. Do the same on minimize (`WndProc` intercepting `WM_SYSCOMMAND`
with `SC_MINIMIZE`, or handling `Resize` when `WindowState == FormWindowState.Minimized`) — both
the close button and the minimize button hide the window to the tray with no taskbar button
remaining. Opening from the tray restores and re-activates the window.

`Application.Exit()` is called only from the tray menu's `Exit` item, and only **after** disposing
the `NotifyIcon` — otherwise a ghost icon lingers in the tray until the user hovers over its former
location.

**The window is non-resizable by the user, but not a fixed size.**
`FormBorderStyle.FixedSingle`, `MaximizeBox = false` — the frame cannot be dragged wider or taller.
Width is `MainForm.RequiredClientWidth` (a session that ran a full 24 hours,
`MainForm.WorstCaseElapsedMinutes`, is the design worst case a row is sized for; anything longer
word-wraps per §8.5 instead of widening the window further), scaled to the live device DPI. **Height
is content-driven** (`MainForm.ResizeToContent`) — the window ends a few pixels below
whatever is actually shown (idle vs. running, how many records/laps), recomputed on every content
change (`RefreshRecords`, `RefreshLaps`, the stopwatch card's `StateChanged`, `OnDpiChanged`) by
asking the real, live control tree for its preferred size, rather than a single constant sized
for the worst case of everything visible at once. Both dimensions are clamped to
`Screen.PrimaryScreen.WorkingArea` on every resize — at higher OS scaling the requested width could
otherwise exceed the working area with no way for the user to shrink it back — and the window's
`Location` is nudged back on-screen (`ClampLocationToWorkingArea`) if a resize would otherwise push
part of it past the working area's edge. This sizing behavior is independent of the
`ControlStyles.ResizeRedraw` rule that stops stale-border repaint artifacts — the two are not
the same thing.

**Window and taskbar icon.** `MainForm.Icon` is set from the embedded
`Assets/app.ico` resource (`StopwatchApp.Assets.app.ico`) at construction; see §6 for why both
`<ApplicationIcon>` and the `<EmbeddedResource>` item are needed. This is a static identity icon and
does not conflict with §1's "no taskbar integration" constraint, which bans dynamic taskbar
features (badges, thumbnail toolbars, progress, a title-bar clock) — not an ordinary app icon. The
animated tray `NotifyIcon` (§10.1) is unrelated and unchanged.

### 10.4 Keyboard shortcuts

**Global (system-wide) hotkeys are explicitly out of scope (§1) — do not add `RegisterHotKey`.**
This app is mouse-driven; the only shortcuts are ordinary, window-scoped keystrokes, active only
while the main window has focus:

| Key | Action |
|---|---|
| `Space` | Start / Pause / Continue — whatever the primary button currently does |
| `Shift+Space` | Lap |
| `Enter` | Stop |

`StopwatchControl.MapShortcut(Keys keyData)` is the pure, unit-tested key table (bare `Space` →
`Toggle`, `Shift+Space` → `Lap`, `Enter` → `Stop`, everything else → `null` — any other modifier on
these keys, e.g. `Ctrl+Space`, is deliberately unmapped). `MainForm` overrides `ProcessCmdKey`
(not `KeyDown`/`AcceptButton`) to call it and dispatch to the existing `StartTimer()`/
`PauseTimerAsync()`/`AddLap()`/`StopTimerAsync()` action methods (§3.1) — the same ones the tray
menu uses. `ProcessCmdKey` runs before a focused control's own key handling, so returning `true`
both dispatches the shortcut and stops it from also clicking whatever button has focus. No
wrong-state guards are needed: `Lap()`'s and `Stop()`'s own no-op guards (§8.3) already absorb a
shortcut pressed in a state where it doesn't apply. `Enter` reaches the same
`StopTimerAsync` confirmation gate as the Stop button (§8.3), so past the threshold it opens the
Stop confirmation dialog instead of stopping immediately.

### 10.5 Single instance

Named `System.Threading.Mutex` created at startup. If a second instance detects the mutex already
exists, it locates the first instance's window with `FindWindow` (matched by `MainForm.WindowTitle`
— **fixed for the life of one build**, not literally constant text: the title includes
`$"Stopwatch {Application.ProductVersion}"`, §8.5 —
but both the running instance and the one calling `FindWindow` are the same build, so the strings
always match) and sends it a registered window message (via `RegisterWindowMessage` + `PostMessage`)
asking it to restore and activate itself, then exits immediately — never runs a second copy.

**Not `PostMessage(HWND_BROADCAST, ...)`**: once hidden to tray, §10.3's
`ShowInTaskbar = false` gives the window an
owner (the mechanism WinForms uses to drop its taskbar button), and Windows excludes owned windows
from `HWND_BROADCAST` delivery regardless of visibility — so a broadcast posted while the window is
hidden is silently never delivered, in exactly the one state single-instance activation exists to
handle. A direct, title-targeted `FindWindow` lookup is not subject to that exclusion.

### 10.6 Window position (always centered)

**The window always centers itself and never remembers a position.** Position memory was explored
and deliberately rejected because the requested behavior is consistent centering. Every "Open"
transition (first launch, tray Open/single left-click, single-instance activation, restore-from-minimize)
calls the same `PositionWindowCentered()`, unconditionally, with no history or saved state involved.

The `IStopwatchStore.SaveWindowPositionAsync`/`LoadWindowPositionAsync` methods, `Database`'s
implementation, and the `window_position` table (migration 2, `SchemaMigrations.cs`) all still exist
but are **no longer called by any application code path** — per this file's own "never edit a
shipped migration" rule (§9), a released migration is not retroactively removed, so the table stays
in the schema for any database that has already applied it. `DatabaseTests` still covers this
persistence machinery directly (it still functions correctly), even though nothing in `MainForm`
exercises it anymore.

---

## 11. Theme

The app follows the OS light/dark setting via `Application.SetColorMode(SystemColorMode.System)`
(§7). It themes window chrome and stock controls but not custom-painted surfaces, so an explicit
color table drives anything drawn manually:

| Role | Light | Dark |
|---|---|---|
| Text | `#111827` | `#F3F4F6` |
| Muted text | `#6B7280` | `#9CA3AF` |
| Card background | `#F9FAFB` | `#1F2937` |
| Row background | `#FFFFFF` | `#111827` |
| Border | `#E5E7EB` | `#374151` |
| Accent (links, durations) | `#2563EB` | `#60A5FA` |
| Empty-state text | `#9CA3AF` | `#6B7280` |

Button colors (base / hover / pressed), same in both themes:

| Button | Base | Hover | Pressed |
|---|---|---|---|
| Start / Continue (green) | `#16A34A` | `#15803D` | `#166534` |
| Pause (yellow) | `#CA8A04` | `#A16207` | `#854D0E` |
| Lap (blue) — also `Manage Records` | `#2563EB` | `#1D4ED8` | `#1E40AF` |
| Stop (red) — also `Clear All Records`/dialog `Clear All` | `#DC2626` | `#B91C1C` | `#991B1B` |
| Cancel (dark slate) — dialog `Cancel` | `#334155` | `#1E293B` | `#0F172A` |

`Theme/Palette.cs` holds the color and spacing tokens above; `Theme/Typography.cs` is the
second token file, holding the type scale (§8.5) and the shared monospace-family resolution.

---

## 12. Intentional behaviors — do not "fix" these

These are the specified contract, not oversights. A change that "corrects" one of these breaks the
contract:

- `ElapsedMs` is written only by the 1-second tick, so all recorded durations are truncated to
  whole seconds — stopping at 1.9 s stores 1.0 s.
- A session stopped before its first tick (under 1000 ms) leaves `ElapsedMs == 0`: **no record is
  saved and `OnStop` never fires**.
- Laps exist only in memory and in the single recovery snapshot; they are not normalized into their
  own permanent database table.
- There is exactly **one** saved-session slot. Pausing or reaching an autosave checkpoint overwrites
  the previous snapshot.
- Autosave checkpoints are based on accumulated running time and do not pause the timer. Restoring
  an autosaved running session still opens it paused; this is intentional recovery semantics.
- Elapsed time is defined against the system clock (`DateTimeOffset.UtcNow`), so a manual clock
  change or a DST transition mid-run shifts the reported elapsed time.
- The "every 5 seconds" tick filter (`totalSeconds % 5 == 0`) can skip a bucket if a tick is delayed
  past a 5-second boundary — acceptable, do not "correct" with a reference-counting workaround.

---

## 13. Testing

xUnit, in a `StopwatchApp.Tests` project.

- All timer logic, transitions, formatters, and data access must live in UI-free classes so they
  are testable without a Windows message loop. `Controls/` holds rendering and event wiring only.
- **Never use `Thread.Sleep` or `Task.Delay` to wait out a timer in a test.** Inject an abstracted
  clock (`System.TimeProvider`, with `Microsoft.Extensions.TimeProvider.Testing`'s
  `FakeTimeProvider` in tests) and advance it explicitly. Real sleeps make the suite slow and
  flaky and don't test the actual tick logic — they just wait alongside it.
- Every test must contain at least one assertion.
- Database tests run against a temporary file-backed database created and deleted per test class —
  never against the real `%LOCALAPPDATA%` file.
- Anything that depends only on `IStopwatchStore` (e.g. `StopwatchTimer`) is tested against
  `StopwatchApp.Tests/FakeStopwatchStore.cs`, an in-memory fake, not a real `Database` — no temp
  file, no SQLite, faster and simpler than round-tripping through disk for pure state-machine
  behavior. Reuse it rather than adding a second fake.
- §14's acceptance criteria are the test list. Implement those; don't invent a parallel suite that
  drifts from the spec.

---

## 14. Acceptance criteria (standing regression checklist)

- Initial display is exactly `00:00:00`; Start and Stop are both visible at idle.
- The Lap button exists only while running — absent at idle and while paused.
- Consecutive laps are splits, not cumulative totals: a lap after 61 s shows `00:01`; a second lap
  121 s later shows `00:02` (not `00:03`).
- Laps clear when a fresh session starts after Stop.
- Laps and the Continue state survive an app restart after Pause (round-trip through the
  `paused_session` table).
- Stop after at least one lap adds a final partial lap; clicking Stop again afterward (with no new
  Start in between) never appends another lap.
- Pause → Stop → restart shows **Start**, not Continue (stopping clears the saved session).
- Stopping twice in a row without restarting yields exactly one record (the duplicate guard in
  `Stop()` holds).
- Clear-all: the button is hidden when there are zero records, `Cancel` leaves records untouched,
  `Clear All` empties the list back to the `No records yet` empty state.
- A session under one minute stores `elapsedMinutes == 0` but still displays `00:01` via
  `FormatElapsed`'s floor.
- `GetAllRecordsAsync()` on an empty table returns an empty list; records saved in order 0, 1, 2
  (by insertion) read back as 2, 1, 0 (newest first).
- A corrupt or missing saved-session row returns `null` from `LoadPausedSessionAsync()` with no
  exception thrown.
- A database created by an earlier build (tables present, no `user_version` stamp) opens without
  data loss and ends up stamped at the current schema version.
- Tray: the icon updates while running and reflects the current hour/minute; the tooltip shows the
  full `HH:MM:SS`; both close and minimize hide the window and remove its taskbar button; Exit
  terminates the process with no icon left behind in the tray.
- Minimize-to-tray (§10.3 — fixes a regression where minimize left a taskbar button, and
  closing while minimized reopened the window still minimized): minimizing via the title-bar button
  removes the taskbar button exactly like Close does, and every "Open" transition (tray Open/
  single left-click, single-instance activation) always restores the window in its normal (not minimized)
  state, regardless of whether it was minimized when last hidden.
- Tray icon layout: below one hour the icon shows large two-digit minutes; from one hour through
  `9:59:59` it shows an `h:mm` label (unpadded hour, zero-padded minutes); from ten hours through
  `23:59:59` it shows unpadded whole hours with `H`; from one day onward it shows unpadded whole days
  with `D`. The tooltip's full `HH:MM:SS` text is unaffected by the compact layout.
- Stop confirmation (§8.3/§8.5): with the default threshold, Stop before five minutes stops
  immediately with no dialog; from five minutes on, Stop pauses the clock and shows the
  confirmation. Confirm saves exactly one record and shows Start; Cancel resumes a clock that was
  running (no time lost or gained) and leaves one that was already paused paused. The same holds
  from the Stop button, `Enter`, and every tray-menu Stop (the tray path restores the hidden window
  first). Setting `StopConfirmationAfterMinutes` to `0` disables it; a missing or malformed value
  falls back to five without affecting `AutosaveIntervalMinutes`, and vice versa.
- Keyboard shortcuts: with the main window focused, `Space` starts/pauses/continues, `Shift+Space`
  laps, and `Enter` stops, matching the mouse-click behavior of the same buttons; none of the three
  fires while the window is hidden to the tray or while `ClearRecordsDialog` is open.
- Window position (§10.6): the window always opens centered on the primary screen — on first
  launch, tray Open/single left-click, single-instance activation, and restore-from-minimize alike. It
  never remembers or restores a previous position, and cannot be dragged-then-resized since the frame
  itself is non-resizable (§10.3).
- Window frame and icon (§10.3): the frame cannot be resized (no maximize button, no drag on
  any edge/corner); the stopwatch icon shows in the title bar, the taskbar button, and on the built
  `.exe` in Explorer; the chrono, buttons, and record/lap rows render at the documented type scale
  (72pt display, 12pt body) and stay centered as the button set changes state.
- Window sizes to content, not a fixed constant (§10.3): the window's width fits the
  sized-for-24-hours record/lap row and its height ends a few pixels below whatever is actually
  shown, growing/shrinking live as records are added or cleared, as laps appear (up to 3 rows tall)
  or clear, and as the "Resumed from a pause" note appears/disappears — never a band of dead space
  to the right of or below the visible rows.
- Title bar shows the version: it reads `Stopwatch {Application.ProductVersion}` (no `v` prefix).
- Records header row (§8.5): `Records` sits left-aligned on the same line as, not stacked above,
  `Manage Records` (blue) and `Clear All Records` (red), both right-aligned in that order.
- Row word-wrap (§8.5): a record/lap row longer than the sized-for worst case (over
  `MainForm.WorstCaseElapsedMinutes`, or a lap id past 3 digits) wraps to a second line inside its row
  card instead of clipping or ellipsizing.
- Records display cap (§8.5): only the 5 most recent records are listed in the main window
  regardless of how many are persisted; "Clear All Records" still clears every persisted record, and
  its own visibility still reflects the true total, not the capped display count.
- Manage Records window (§8.5): `Manage Records` is enabled and opens a single modeless,
  owner-managed window that lists all records newest first, paginated at 10 rows per page. Each row
  has a confirmed Delete action; the header has a confirmed Clear All action. Add/edit behavior is
  out of scope, and the main card's Clear All shortcut remains.
- Row icons (§8.5): record and lap rows show the calendar, stopwatch, and hourglass as full-color
  images — in the main window and in Manage Records, in light and dark mode — never as flat
  text-colored glyphs. A row past the sized-for worst case wraps without splitting an icon across
  lines or clipping it.
- Clear-all dialog styling (§8.5): `Clear All` renders red and `Cancel` renders dark slate, and
  the two sit aligned on the same baseline.
- Button focus (§8.5/§17): tabbing through any window shows the stock keyboard-focus indicator on
  whichever button has focus, matching ordinary WinForms `Button` behavior.
- Tray open gesture (§10.2): a single left-click opens and activates the main window;
  right-click remains exclusively available for the context menu.

Implement these as automated tests wherever the behavior is UI-free, and as a manual check where it
genuinely requires a running window (tray, hotkeys).

---

## 15. Packaging

Framework-dependent build — the target machine needs the **.NET 10 Desktop Runtime** installed
(not just the base .NET runtime; WinForms ships as part of the desktop bundle).

```powershell
dotnet publish StopwatchApp/StopwatchApp.csproj -c Release -r win-x64 --self-contained false
# → StopwatchApp/bin/Release/net10.0-windows/win-x64/publish/
```

Rules:

- Release builds only for anything distributed; `Debug` output is never shared.
- The version lives in exactly one place — `<Version>` in the `.csproj` (§6) — and is displayed in
  the app's UI. Bump it in the same change as any user-visible behavior change.
- Ship the publish folder as-is; there is no installer and no MSIX packaging in scope. A
  single-file, self-contained build (`-p:PublishSingleFile=true --self-contained true`) is a
  documented future option, not the current one — note in any future packaging PR that switching
  changes the runtime requirement (no separate Desktop Runtime install needed, larger output).
  Dapper maps rows via runtime IL emit: single-file/self-contained publishing is unaffected, but
  `PublishTrimmed` or NativeAOT (neither in scope) would need `Dapper.AOT` instead.
- `bin/`, `obj/`, and `publish/` are git-ignored. The runtime `.db` file is user data and must
  never be committed.
- No code signing (personal-use app). Windows SmartScreen may warn on first run for an unsigned,
  unfamiliar executable — this is expected; document it, don't try to work around it.

---

## 16. Git workflow

Git commits are allowed in this repository. A commit is an ordinary deliverable when useful for the
requested task; it is no longer prohibited by a standing project rule. Keep each commit focused,
use a descriptive message, include only files belonging to the change, and report the resulting
hash. Never commit runtime databases, build or publish output, secrets, or unrelated user changes.
Do not push, rewrite published history, force-push, or discard user work unless the user explicitly
requests that separate action.

---

## 17. Decision rationale (undated, current decisions only)

This section preserves the reasoning that matters to future work. It is not a chronological
changelog. When a decision changes, update the governing section and replace the obsolete rationale
here; do not accumulate dated implementation history.

- **Solution and publish are x64-only.** A solution-level x64 declaration is insufficient by itself:
  every project in `StopwatchApp.slnx` also needs its explicit x64 mapping, while each project pins
  `<Platform>x64</Platform>`. The runtime identifier is `win-x64`. This prevents direct project,
  solution, and publish commands from silently disagreeing about `AnyCPU`.
- **WinForms was selected as the product boundary, not as a portability compromise.** The app is a
  Windows 11 tray utility and directly uses `NotifyIcon`, Windows messages, system color mode, and
  per-monitor DPI. Cross-platform UI abstractions would add complexity without serving a supported
  target.
- **Dapper was chosen over EF Core and hand-written ordinal mapping.** The schema has only a few
  tables and fixed queries, with no relationships or unit-of-work behavior. Dapper keeps SQL
  explicit and maps by name without EF design-time tooling, generated migration files, or a large
  change-tracking layer.
- **Schema migrations remain hand-written and append-only.** `PRAGMA user_version` gives this local
  database a small, deterministic evolution path. Existing migration SQL must never be edited,
  including the now-unused window-position table, because installed databases may already be
  stamped with that version.
- **The data boundary is an interface.** `StopwatchTimer` depends on `IStopwatchStore`, not
  `Database`, so transition tests use one shared in-memory fake while database tests alone touch a
  temporary SQLite file.
- **One long-lived SQLite connection is deliberate.** The process is single-instance and
  single-user. Initialization is idempotent, async disposal clears the connection pool, and storage
  methods use the documented narrow exception policy so local persistence failures do not tear down
  the UI.
- **Timer state is independent of the UI timer.** `StopwatchTimer` reads an injected
  `TimeProvider`; `StopwatchControl` owns the one-second WinForms timer. This makes delayed UI
  ticks self-correct from a wall-clock anchor and makes tests deterministic without sleeping.
- **Autosave reuses the paused-session slot.** A second recovery table or event log would add
  competing recovery semantics. Pause and periodic running-time checkpoints serialize writes
  through one gate, and Stop clears the slot through that same gate to prevent a late write from
  resurrecting a stopped session.
- **Records are owned by the state machine.** Main-window preview, records manager, tray actions, and
  shortcuts all observe one timer-owned newest-first collection. After save/delete/clear, the timer
  reloads it and raises `RecordsChanged`; UI components never mutate a shared list or query the
  store themselves.
- **The Stop confirmation gate lives in `StopwatchControl`, not the timer or the callers.** Every
  Stop entry point already funnels through `StopTimerAsync`, so one gate covers button, `Enter`,
  and tray; `StopwatchTimer` must stay UI-free and never shows a dialog. The control asks through a
  `ConfirmStop` callback (no-op default that confirms) so `MainForm` owns the actual dialog and
  window restoration. `StopConfirmationAfterMinutes = 0` means "off" while a non-positive autosave
  interval means "use the default", because a disabled autosave has no sensible meaning but a
  disabled confirmation does. Settings validate per property so one bad value never discards the
  other, which is why the loader is `AppSettings` rather than an autosave-only type.
- **UI actions route through `StopwatchControl`.** Buttons, tray menu items, and keyboard shortcuts
  call the same action methods so state transitions, display updates, timer enablement, and
  `StateChanged` notifications cannot drift into separate implementations.
- **UI code does not subscribe directly to service continuations that mutate controls.** Service
  methods may resume after `ConfigureAwait(false)`; outer WinForms handlers retain the UI context,
  refresh after their awaited call, and use `Control.Invoke` at cross-thread notification
  boundaries.
- **Only window-scoped shortcuts are supported.** Global hotkeys were rejected because they create
  system-wide conflicts and exceed this focused app's scope. `ProcessCmdKey` gives predictable
  behavior while the window is active and reuses the normal control actions.
- **Single-instance activation targets the existing title directly.** Broadcasting does not reliably
  reach a hidden owned window after `ShowInTaskbar = false`. `FindWindow` plus a registered direct
  message restores the one existing instance without creating a second UI.
- **The tray icon remains a square notification icon.** Windows gives third-party notification-area
  icons a square slot; a taskbar-clock-style rectangular widget is not available through the public
  tray API. The current compact display therefore uses one shared ink-bounds auto-fit rule for
  large minutes, `h:mm`, hours, or days alike, while the tooltip carries exact unbounded time. The
  four-glyph `h:mm` label (`1:01`, etc.) is the one case that stays visibly smaller than the rest —
  it was chosen over a height-first condensed/stretched rendering or a two-line stacked layout
  (dropping the colon) specifically to keep the single shared fit rule and the literal `h:mm` form,
  and that's accepted rather than solved with a special-cased second fit path.
- **Native icon ownership is explicit.** Replacing a runtime HICON destroys the previous handle, and
  application exit disposes `NotifyIcon` before ending the message loop. These rules prevent GDI
  leaks and ghost tray icons.
- **The window always reopens centered.** Remembering manually dragged positions was explored and
  rejected because the desired product behavior is deterministic centering on every Open path. The
  old persistence surface stays only because shipped migrations are immutable.
- **Window height is content-driven.** Fixed worst-case heights repeatedly created dead space or DPI
  clipping. The live control tree supplies preferred height after state/content changes; width
  budgets for a 24-hour row, and longer records or lap identifiers wrap instead of growing the
  window indefinitely.
- **Owner-drawn visuals share primitives and tokens.** `RoundedRectangle`, `Palette`, and
  `Typography` centralize geometry, interaction colors, disabled states, font fallback, and
  spacing for the remaining owner-drawn surfaces — the stopwatch/records cards' rounded outline
  and the records/laps rows. This was chosen after duplicated or stock rendering produced
  inconsistent alignment, dark-mode colors, and rounded borders.
- **Buttons moved back to a stock `Button` (`ButtonFactory`), reversing the owner-drawn
  `GlyphButton` this app used previously.** `GlyphButton` set `ControlStyles.UserPaint` and never
  called `base.OnPaint`, so it never drew a keyboard-focus indicator — a real accessibility gap.
  `FlatStyle.Flat` is fully stock (no custom `OnPaint`) and still honors `BackColor` and
  `FlatAppearance.MouseOverBackColor`/`MouseDownBackColor`, so every `Palette` button color/hover/
  press triplet carried over unchanged. The one accepted cost is square corners: WinForms has no
  stock style that gives both an arbitrary fill color and rounded corners — `FlatStyle.System`
  renders natively rounded on Windows 11 but ignores `BackColor` entirely, and a `Control.Region`
  clip (tried previously) is a hit-test mask with no anti-aliasing, so its corners come out
  jagged rather than smooth.
- **Cascadia Mono falls back to Consolas at runtime.** Both preserve stable tabular digits; resolving
  the installed family avoids silent substitution to a proportional font.
- **Manage Records reuses its row controls.** A row is a nest of panels, a table layout, and a
  button, so recreating a page on every delete or page turn (and leaving the detached rows
  undisposed) made the window lag. `ManageRecordsForm.RebuildRows` instead updates the existing
  rows' text and record id and creates or disposes rows only when the page count changes; a delete
  toggles button enablement rather than rebuilding, and `IconTextLabel` caches its layout and
  `RowIconSet.GetScaled` caches per-size icon bitmaps so painting never re-measures or resamples.
- **The main window is a five-record summary; full history has its own modeless window.** Limiting
  the dashboard keeps its size stable. The manager uses pages of ten and routes delete/clear through
  the same timer-owned records path, so both views stay synchronized.
- **The application icon is a licensed Fluent System Icons asset.** It is embedded for the live form
  and configured as `ApplicationIcon` for Explorer/publish output; attribution remains in
  `Assets/NOTICE.md`. A static identity icon is not forbidden taskbar integration.
- **Row emoji are embedded color PNGs drawn inline, not text.** GDI text rendering (`TextRenderer`,
  and equally GDI+ `DrawString`) cannot paint color font tables, so the literal emoji only ever
  produced a flat outline in the row's text color. Real color text needs DirectWrite/Direct2D with
  color-font drawing enabled, which no WinForms text API exposes; adding that COM interop and device
  lifetime management for three fixed glyphs was rejected. The images are unmodified Noto Color
  Emoji (Apache-2.0 per its README) committed under `Assets/emoji/` like `app.ico` — obtained once
  at development time, never fetched at build or run time, so the offline constraint (§1.2) holds.
  One layout engine (`IconTextLayout`) both measures and draws so a wrapped row is never sized one
  way and painted another. `RowIconSet` is a per-owner disposable instance rather than a static
  cache, because static mutable state is forbidden (§5) and the bitmaps hold native memory; the
  Manage Records window shares one set across all its rows instead of decoding per row.
- **Formatting belongs to CSharpier, while analyzers own code quality.** `IDE0055` is disabled so
  the SDK formatter cannot fight CSharpier, but compiler warnings, recommended analyzers, and all
  other enforced code-style diagnostics remain build-breaking.

---

## 18. Agent preconditions and standing project preferences

Apply this checklist before and during every change. These are owner preferences and project
preconditions, not optional suggestions.

1. Read this entire file before editing. Treat it as the sole authoritative project contract; if
   code and this document disagree, investigate and update both in the same change. Every
   functionality or architecture change must also update all affected references and descriptions
   in this file; completion requires the implementation and `AGENTS.md` to agree.
2. Inspect `git status --short` first. Preserve unrelated and pre-existing user changes; never
   discard, reset, or overwrite them to make a task easier.
3. Keep the product offline, Windows 11-only, and x64-only. Do not add network behavior, telemetry,
   startup registration, global hotkeys, speculative platform compatibility, or other out-of-scope
   features.
4. Preserve the chosen boundaries: forms orchestrate, controls render and wire events, services hold
   testable behavior, and only `Database` accesses SQLite through `IStopwatchStore`.
5. Prefer the simplest implementation that satisfies the current behavior. Do not add a DI
   container, repository layer over `IStopwatchStore`, ORM, background service, or compatibility
   abstraction without an explicit architecture decision.
6. Keep nullable reference types enabled, avoid null-forgiving suppression, default fields to
   `readonly`, seal non-inheritable classes, use immutable record models, and keep one type per
   file.
7. Keep async I/O async end-to-end. Never use `.Result` or `.Wait()` on the UI thread, and preserve
   the UI synchronization-context rules in §5.
8. Put every new user-visible string, state, keyboard path, persistence behavior, and layout edge
   case under automated test when it can be tested without a live desktop. Use a documented manual
   check for genuine tray/window/DPI behavior.
9. Never use `Thread.Sleep` or `Task.Delay` as a timer-testing strategy. Use
   `FakeTimeProvider`, call `Tick()`, and reuse `FakeStopwatchStore`.
10. Never edit a shipped migration. Append the next migration and add tests for both a fresh
    database and upgrade from the preceding schema version.
11. Any user-visible behavior change bumps `<Version>` in the application project in the same
    change. The title must continue to display that value without a source-revision suffix.
12. Format with CSharpier only. Never run `dotnet format`. Before presenting a completed change,
    run, in this order:

    ```powershell
    csharpier format .
    csharpier check .
    dotnet build -c Release
    dotnet test
    ```

    `csharpier check .` must exit 0, the Release build must contain zero warnings and zero errors,
    and every test must pass.
13. If tray, hotkey, window visibility, theme, DPI, sizing, native icon, or exit behavior changed,
    also launch the built application and manually verify the relevant §14 regression cases. A
    green unit-test suite is not proof that desktop rendering is correct.
14. Release artifacts are framework-dependent `win-x64` builds and require the .NET 10 Desktop
    Runtime. Do not add an installer, MSIX, trimming, NativeAOT, self-contained publishing, or
    multi-RID publishing unless the product scope explicitly changes.
15. Git commits are permitted. Keep them cohesive and free of generated output, user databases,
    secrets, and unrelated work; report the hash. Never push or rewrite history without an explicit
    request.
16. Record enduring decisions as present-tense rationale: update the governing section and this
    rationale section without dates, milestone labels, diary-style progress, or references to a
    deleted planning document.
