# AGENTS.md — Stopwatch (Windows 11 tray time tracker)

This file is the **authoritative, standing** instruction set for any agent working on this
codebase. Work on this codebase should be possible with *only* this file loaded in context.

---

## 1. Project overview and hard constraints

A single-user Windows 11 tray time tracker: start/pause/resume/stop with lap splits, a persisted
records list, and a system-tray presence showing elapsed time while the main window is hidden.

- **Offline by construction.** No network calls, no telemetry, no analytics, no auto-update, no
  crash reporting — ever. New code must never add any of these.
- All data lives in one local SQLite file at `%LOCALAPPDATA%\StopwatchApp\stopwatch.db`.
- **Windows 11 only.** Do not add compatibility shims for Windows 10 or earlier.
- In scope: global hotkeys, single-instance enforcement, close-to-tray, tray context menu.
- Out of scope (do not add): run-at-login/startup registration, crash recovery of a running
  (unpaused) session, idle detection, CSV/JSON export, cloud sync, telemetry, installer/MSIX.
- **No taskbar integration** — no overlay badge, no thumbnail toolbar buttons, no progress bar,
  no title-bar clock. The tray icon is the only out-of-window surface.
- Pausing is the *only* action that persists a live session. A session that runs and is never
  paused before the app closes is unrecoverable — intentional, not a gap to fill.

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
StopwatchApp/
  StopwatchApp.csproj      net10.0-windows, WinForms, nullable, analyzers-as-errors, x64
  Program.cs               single-instance mutex, ApplicationConfiguration.Initialize, SetColorMode, Application.Run
  MainForm.cs              thin orchestrator — wires controls and services, no business logic
  Controls/                rendering + event wiring only, no untestable business logic
    StopwatchControl.cs    timer state (§8) + control row (§8.5)
    RecordsListControl.cs  records and laps list rendering
    ClearRecordsDialog.cs  confirm dialog
  Services/                UI-free, unit-testable
    StopwatchTimer.cs      tick loop + transitions (§8)
    TrayIconService.cs     NotifyIcon, rendered icon, context menu (§10)
    HotkeyService.cs       RegisterHotKey P/Invoke (§10)
    Database.cs            SQLite access (§9)
  Formatting/
    TimeFormat.cs          the four formatters (§8.4)
  Theme/
    Palette.cs             light/dark color tables (§11)
StopwatchApp.Tests/        xUnit — formatters, transitions, database round-trip
```

Controls hold rendering and event wiring only — no logic that can't be tested outside a form.
`MainForm` never contains business logic. UI code never touches SQLite directly; everything goes
through `Services/Database.cs`. Parent/child communication happens through events or callback
delegates with no-op defaults, never shared mutable state.

### 3.1 Component contracts

These signatures are fixed architecture, not suggestions — a change to any of them is a design
change, not a refactor. They exist so components stay decoupled and independently testable.

**`IStopwatchStore`** — the §9 data-access surface as an interface. `Database` implements it;
`StopwatchTimer` depends only on the interface, so the state machine is unit-testable without a
temp database:

```csharp
public interface IStopwatchStore
{
    Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs);
    Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync();
    Task ClearAllRecordsAsync();
    Task SavePausedSessionAsync(PausedSession session);
    Task<PausedSession?> LoadPausedSessionAsync();
    Task ClearPausedSessionAsync();
}
```

**`StopwatchTimer` public surface** — the state machine (§8) is UI-free and exposes:

```csharp
public sealed class StopwatchTimer
{
    public StopwatchTimer(IStopwatchStore store, TimeProvider time);

    public bool IsRunning { get; }
    public bool IsPaused { get; }
    public long ElapsedMs { get; }
    public IReadOnlyList<Lap> Laps { get; }
    public IReadOnlyList<StopwatchRecord> Records { get; }
    public long RestoredPausedAtMs { get; }

    public void Start();
    public Task PauseAsync();
    public void Lap();
    public Task StopAsync();
    public void Tick();                 // called once/second by the UI-side Timer
    public Task RestoreAsync();         // startup: loads a saved paused session, if any

    public event Action<long>? OnStart;
    public event Action<long>? OnPause;
    public event Action<long>? OnTick;
    public event Action<long, long, long>? OnStop;
    public event Action? RecordsChanged;
}
```

The `Async` suffixes are load-bearing, not stylistic: `Pause` persists a snapshot and `Stop` awaits
a record write, so callers must `await` them — never wire a `void` handler that later needs
`.Wait()` (forbidden by §5).

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

**Other fixed shapes**, so they aren't re-derived differently by different agents:

```csharp
public static class TimeFormat            // §8.4 — all four formatters, pure, static
{
    public static string FormatTime(long ms);
    public static string FormatDate(long unixMs);
    public static string FormatTimeOnly(long unixMs);
    public static string FormatElapsed(long minutes);
}

public static class Palette               // §11 — light/dark + button color triplets
{
    public static Color Text(bool dark);
    // ... one accessor per role/button in §11's tables; exact member list is not fixed,
    // but every value in §11 must be reproduced exactly, compared via .ToArgb().
}

public static class ClearRecordsDialog    // §8.5 — the confirm dialog
{
    public static DialogResult ShowConfirm(IWin32Window owner);
}
```

---

## 4. Commands and post-change checklist

| Command | Purpose |
|---|---|
| `dotnet csharpier format .` | Formats the whole repo (whitespace, wrapping, brace style) per `.editorconfig`. |
| `dotnet csharpier check .` | Check-only; exits `1` if anything is unformatted. Use before presenting any change as complete. |
| `dotnet build -c Release` | Compiles with analyzers enabled and warnings treated as errors. |
| `dotnet test` | Runs the full test suite. |

**Formatting is CSharpier, not `dotnet format`.** This project uses the CLI
(`dotnet csharpier format`/`check`) exclusively — there is no `CSharpier.MSBuild` package
reference, so formatting is not enforced at build time and must be run explicitly. **Never run
`dotnet format`** on this repo: it fights CSharpier over whitespace and brace placement and will
undo or conflict with its output.

Run after **every** completed change, before presenting the result as done:

1. `dotnet csharpier format .`
2. `dotnet csharpier check .` — must exit `0`
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

Keep these set — they make lint and analyzers run on every `dotnet build` and unskippable:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <Version>1.0.0</Version>

    <!-- WinForms application configuration (source-generates ApplicationConfiguration.Initialize) -->
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
    <ApplicationVisualStyles>true</ApplicationVisualStyles>
    <ApplicationUseCompatibleTextRendering>false</ApplicationUseCompatibleTextRendering>

    <!-- Analyzers as real build gates -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

</Project>
```

- `EnableNETAnalyzers` defaults on for .NET 5+, but set it explicitly so intent survives future
  edits.
- `EnforceCodeStyleInBuild` promotes `IDExxxx` code-style rules from IDE-only hints to build
  diagnostics — without it, `.editorconfig` rules are invisible outside an IDE.
- `TreatWarningsAsErrors` gives every rule real teeth.
- Escape hatch: `<CodeAnalysisTreatWarningsAsErrors>false</CodeAnalysisTreatWarningsAsErrors>`
  demotes only `CAxxxx` code-quality rules, not compiler warnings. Any suppression must be
  narrow — a justified `[SuppressMessage]` attribute or a scoped
  `#pragma warning disable ... / restore ...` pair — **never** a blanket `<NoWarn>` list.
- The version lives in exactly one place (`<Version>` above, starting at `1.0.0`) and must also be
  surfaced in the app's UI (e.g. a footer or an About entry in the tray menu). Bump it in the same
  change as any user-visible behavior change.
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
  re-render the tray icon at the new size when DPI changes at runtime.
- Call order in `Program.Main`:
  ```csharp
  ApplicationConfiguration.Initialize();
  Application.SetColorMode(SystemColorMode.System);
  Application.Run(new MainForm());
  ```
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

```csharp
public sealed record StopwatchRecord(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
public sealed record Lap(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
```

### 8.2 Timing loop

`System.Windows.Forms.Timer`, interval **1000 ms**, enabled only while `IsRunning`. Each tick:

```
ElapsedMs = NowUnixMs() - StartTime;          // absolute delta, no accumulation
totalSeconds = ElapsedMs / 1000;
if (totalSeconds > 0 && totalSeconds % 5 == 0) OnTick(ElapsedMs);
```

Clock source is `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` — **not**
`System.Diagnostics.Stopwatch`. The wall-clock anchor is deliberate: a late/delayed tick
self-corrects on the next tick instead of accumulating drift, and elapsed time is defined against
real time, not a monotonic counter.

Dispose the timer on form close. No ticks may fire after teardown.

### 8.3 State transitions (exact — reproduce this logic, don't reinterpret it)

```
Start():
  if IsRunning: return                       // no-op
  if not IsPaused:                           // fresh start, not a resume
    Laps = []
    LastLapElapsed = 0
    LastLapTimestamp = 0
    RestoredPausedAtMs = 0
    ElapsedMs = 0
    SessionStartMs = Now()
  // else: resuming — Laps, SessionStartMs, RestoredPausedAtMs are all preserved
  StartTime = Now() - ElapsedMs
  IsRunning = true
  IsPaused = false
  OnStart(ElapsedMs)

Pause():
  if not IsRunning: return                   // no-op
  IsRunning = false
  IsPaused = true
  SavePausedSession({ ElapsedMs, SessionStartMs, Laps, LastLapElapsed, LastLapTimestamp, PausedAt: Now() })
  OnPause(ElapsedMs)
  // display freezes at the last tick's ElapsedMs

Lap():
  if not IsRunning: return                   // no-op
  splitMs = ElapsedMs - LastLapElapsed
  newLap = {
    Id: Laps.Count + 1,
    StartTimestamp: LastLapTimestamp != 0 ? LastLapTimestamp : SessionStartMs,
    EndTimestamp: Now(),
    ElapsedMinutes: splitMs / 60000
  }
  Laps = [newLap, ...Laps]                   // prepend — newest first
  LastLapElapsed = ElapsedMs
  LastLapTimestamp = newLap.EndTimestamp
  // each lap is the interval since the previous lap, NOT a cumulative total

Stop():
  IsRunning = false
  IsPaused = false
  RestoredPausedAtMs = 0
  ClearPausedSession()
  endTimestamp = Now()

  if Laps.Count > 0 && ElapsedMs > LastLapElapsed:
    // final partial lap covering the time since the last lap
    splitMs = ElapsedMs - LastLapElapsed
    finalLap = {
      Id: Laps.Count + 1,
      StartTimestamp: LastLapTimestamp != 0 ? LastLapTimestamp : SessionStartMs,
      EndTimestamp: endTimestamp,
      ElapsedMinutes: splitMs / 60000
    }
    Laps = [finalLap, ...Laps]
    LastLapElapsed = ElapsedMs
    LastLapTimestamp = endTimestamp

  if ElapsedMs > 0 && SessionStartMs > 0:
    OnStop(ElapsedMs, SessionStartMs, endTimestamp)
    isDuplicate = Records.Count > 0 && Records[0].StartTimestamp == SessionStartMs
    if not isDuplicate:
      await SaveRecordAsync(SessionStartMs, endTimestamp, ElapsedMs)
      await ReloadRecordsAsync()

  SessionStartMs = 0
```

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

Reference implementation — keep this exact:

```csharp
public static class TimeFormat
{
    public static string FormatTime(long ms)
    {
        long totalSeconds = ms / 1000;
        long hours = totalSeconds / 3600;
        long minutes = (totalSeconds % 3600) / 60;
        long seconds = totalSeconds % 60;
        return $"{hours.ToString("D2", CultureInfo.InvariantCulture)}:" +
               $"{minutes.ToString("D2", CultureInfo.InvariantCulture)}:" +
               $"{seconds.ToString("D2", CultureInfo.InvariantCulture)}";
    }

    public static string FormatDate(long unixMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static string FormatTimeOnly(long unixMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatElapsed(long minutes)
    {
        long displayMinutes = minutes < 1 ? 1 : minutes;
        long hours = displayMinutes / 60;
        long mins = displayMinutes % 60;
        return $"{hours.ToString("D2", CultureInfo.InvariantCulture)}:" +
               $"{mins.ToString("D2", CultureInfo.InvariantCulture)}";
    }
}
```

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
- The large elapsed-time display uses a monospace, tabular-figure font (Cascadia Mono or Consolas)
  so digits don't shift width as they change.
- A **Laps** panel is shown only when `Laps.Count > 0`, newest first, scrollable.
- A **Records** panel is always shown, newest first, scrollable, with an empty state reading
  `No records yet` when there are none.
- A `Clear All Records` button is visible only when `Records.Count > 0`.
- A confirm dialog, titled `Clear All Records`, body text
  `Are you sure you want to clear all records? This action cannot be undone.`, buttons `Cancel` and
  `Clear All`. Confirming clears the records table and reloads the (now empty) list.
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
created on first run if it doesn't exist.

```sql
CREATE TABLE IF NOT EXISTS records (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  startTimestamp INTEGER NOT NULL,   -- epoch ms
  endTimestamp   INTEGER NOT NULL,   -- epoch ms
  elapsedMinutes INTEGER NOT NULL    -- floor(elapsedMs / 60000)
);

CREATE TABLE IF NOT EXISTS paused_session (
  id               INTEGER PRIMARY KEY CHECK (id = 1),  -- single slot, enforced by the CHECK
  elapsedTime      INTEGER NOT NULL,
  sessionStartTime INTEGER NOT NULL,
  lapsJson         TEXT    NOT NULL,   -- JSON-serialized List<Lap>
  lastLapElapsed   INTEGER NOT NULL,
  lastLapTimestamp INTEGER NOT NULL,
  pausedAt         INTEGER NOT NULL
);
```

API surface — deliberately minimal, and it **is** `IStopwatchStore` (§3.1), which `Database`
implements. **No update, no delete-by-id, no range query:**

```csharp
Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs);
  // floors elapsedMs / 60000 internally before storing; returns the new row id

Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync();
  // ORDER BY id DESC — newest first

Task ClearAllRecordsAsync();
  // DELETE FROM records

Task SavePausedSessionAsync(PausedSession session);
  // upsert into the single-row paused_session table (id = 1)

Task<PausedSession?> LoadPausedSessionAsync();
  // returns null if no row exists, or if the row fails to deserialize

Task ClearPausedSessionAsync();
  // DELETE FROM paused_session WHERE id = 1
```

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

Render a 32×32 icon with GDI+ and convert it to an `HICON`: two stacked rows of two digits — hours
on top, minutes below — so digits stay legible when Windows scales down to 16 px. Update the icon
at most once per second, and only when the displayed minute actually changes (don't regenerate the
bitmap every tick if the visible value hasn't changed).

**Call `DestroyIcon` on the previous handle every time you replace it.** Forgetting this is the
single most common bug in this pattern and leaks GDI handles until the process is killed.

Tooltip text is `FormatTime(ElapsedMs)` — the full `HH:MM:SS` value — plain text, no prefix, no
emoji. Idle, paused, and running states may differ in icon tint but must keep the same digit
layout.

### 10.2 Tray context menu

Order: `Open`, separator, the state-appropriate action(s) from §8.5 (`Start`/`Pause`/`Continue`/
`Lap`/`Stop`), separator, `Exit`. Double-clicking the tray icon opens (restores and activates) the
main window. `Exit` is the only way to quit the application.

### 10.3 Window behavior

Intercept `FormClosing` when `e.CloseReason == CloseReason.UserClosing`: cancel the close, hide the
form, set `ShowInTaskbar = false`. Do the same on minimize (`WndProc` intercepting `WM_SYSCOMMAND`
with `SC_MINIMIZE`, or handling `Resize` when `WindowState == FormWindowState.Minimized`) — both
the close button and the minimize button hide the window to the tray with no taskbar button
remaining. Opening from the tray restores and re-activates the window.

`Application.Exit()` is called only from the tray menu's `Exit` item, and only **after** disposing
the `NotifyIcon` — otherwise a ghost icon lingers in the tray until the user hovers over its former
location.

### 10.4 Global hotkeys

`RegisterHotKey`/`UnregisterHotKey` via P/Invoke on `user32.dll`, handled by overriding `WndProc`
and checking for `WM_HOTKEY`. Default bindings:

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+S` | Start / Continue |
| `Ctrl+Alt+P` | Pause |
| `Ctrl+Alt+L` | Lap |
| `Ctrl+Alt+X` | Stop |

Registration can fail if another app already owns a combination. Fail soft — log it and surface a
tray balloon notification — never throw or crash the app over a hotkey conflict. Call
`UnregisterHotKey` for every `RegisterHotKey` on shutdown.

### 10.5 Single instance

Named `System.Threading.Mutex` created at startup. If a second instance detects the mutex already
exists, it sends a registered window message (via `RegisterWindowMessage` +
`PostMessage(HWND_BROADCAST, ...)`) asking the first instance to restore and activate its window,
then exits immediately — never runs a second copy.

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
| Lap (blue) | `#2563EB` | `#1D4ED8` | `#1E40AF` |
| Stop (red) | `#DC2626` | `#B91C1C` | `#991B1B` |

---

## 12. Intentional behaviors — do not "fix" these

These are the specified contract, not oversights. A change that "corrects" one of these breaks the
contract:

- `ElapsedMs` is written only by the 1-second tick, so all recorded durations are truncated to
  whole seconds — stopping at 1.9 s stores 1.0 s.
- A session stopped before its first tick (under 1000 ms) leaves `ElapsedMs == 0`: **no record is
  saved and `OnStop` never fires**.
- Laps exist only in memory and in a paused-session snapshot; a run that goes to Stop without ever
  being paused has no recovery point if the app is killed mid-run.
- There is exactly **one** saved-session slot. Pausing a second time overwrites the first snapshot.
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
- Tray: the icon updates while running and reflects the current hour/minute; the tooltip shows the
  full `HH:MM:SS`; both close and minimize hide the window and remove its taskbar button; Exit
  terminates the process with no icon left behind in the tray.

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
- `bin/`, `obj/`, and `publish/` are git-ignored. The runtime `.db` file is user data and must
  never be committed.
- No code signing (personal-use app). Windows SmartScreen may warn on first run for an unsigned,
  unfamiliar executable — this is expected; document it, don't try to work around it.

---

## 16. Git workflow

Never run `git commit` unless the user has explicitly asked for a commit to be created in that
turn. Completing a task, or making code changes, is never implicit permission to commit.
