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
- In scope: window-scoped keyboard shortcuts, single-instance enforcement, close-to-tray, tray
  context menu.
- Out of scope (do not add): global/system-wide hotkeys, run-at-login/startup registration, crash
  recovery of a running (unpaused) session, idle detection, CSV/JSON export, cloud sync, telemetry,
  installer/MSIX.
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
StopwatchApp.slnx          solution, XML format (the .NET 10 SDK default); x64 is the only solution platform
StopwatchApp/
  StopwatchApp.csproj      net10.0-windows, WinForms, nullable, analyzers-as-errors, x64
  Program.cs               single-instance mutex, ApplicationConfiguration.Initialize, SetColorMode, Application.Run
  MainForm.cs              thin orchestrator — wires controls and services, no business logic
  Assets/
    app.ico                window/taskbar/.exe icon (§10.3), embedded resource
    NOTICE.md              Fluent System Icons attribution and license for app.ico
  Controls/                rendering + event wiring only, no untestable business logic
    StopwatchControl.cs    timer state (§8) + control row (§8.5) + keyboard shortcuts (§10.4)
    StopwatchShortcut.cs   the three window-scoped shortcuts (§10.4)
    GlyphButton.cs         the S11a owner-drawn rounded button (§8.5/§17); extracted (S15) from
                            StopwatchControl so RecordsListControl and ClearRecordsDialog reuse it
    RecordsListControl.cs  records and laps list rendering
    ClearRecordsDialog.cs  confirm dialog
  Models/                  one record type per file (§5)
    StopwatchRecord.cs     a persisted record (§8.1/§9)
    Lap.cs                 an in-memory/restored split (§8.1)
    PausedSession.cs       the single saved-session snapshot (§9)
  Services/                UI-free, unit-testable
    StopwatchTimer.cs      tick loop + transitions (§8)
    TrayIconService.cs     NotifyIcon, rendered icon, context menu (§10)
    IStopwatchStore.cs     the data-access interface (§3.1/§9)
    Database.cs            SQLite access via Dapper (§9)
    SchemaMigrations.cs    versioned schema steps, PRAGMA user_version (§9)
  Formatting/
    TimeFormat.cs          the four formatters (§8.4)
  Theme/
    Palette.cs             light/dark color tables (§11)
    Typography.cs          type scale + monospace font resolution (§11)
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
    Task DeleteRecordAsync(long id);
    Task ClearAllRecordsAsync();
    Task SavePausedSessionAsync(PausedSession session);
    Task<PausedSession?> LoadPausedSessionAsync();
    Task ClearPausedSessionAsync();

    // S11b — the single saved window position (§9/§10.6); same single-slot upsert pattern as
    // paused_session above.
    Task SaveWindowPositionAsync(int x, int y);
    Task<(int X, int Y)?> LoadWindowPositionAsync();
}
```

**`StopwatchTimer` public surface** — the state machine (§8) is UI-free and exposes:

```csharp
public sealed class StopwatchTimer
{
    public StopwatchTimer(IStopwatchStore store, TimeProvider time, int autosaveIntervalMinutes = 5);

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
    public Task ClearRecordsAsync();
    public Task DeleteRecordAsync(long id);
    public void Tick();                 // called once/second by the UI-side Timer
    public Task SaveAutosaveIfDueAsync(); // called after Tick by the UI-side Timer
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
`RestoreAsync` reloads `Records` (in addition to restoring a paused session, if any) so the list is
populated before the caller ever reads it — a fresh `StopwatchTimer` has no other point at which
`Records` is first loaded.

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
    // Returns DialogResult.Yes for "Clear All", DialogResult.Cancel for "Cancel" or closing the
    // dialog (Escape/X) — CancelButton is wired so both of the latter map to Cancel.
}

public sealed class StopwatchControl : UserControl   // §8.5 — display + control row
{
    // Constructor, Timer, DarkMode, and RestoreAsync are the S5 shape above §3.1's block; S8/S9
    // added the rest so the tray menu and (later) hotkeys can drive a transition without leaving
    // the window's own display stale — every action method below does exactly what its button's
    // Click handler does (UpdateDisplay() then StateChanged?.Invoke()), and there is only ever
    // one copy of each body.
    public event Action? StateChanged;   // after Start, Pause, Lap, Stop, or Restore
    public event Action? Tick;           // once a second, only while running (S8)
    public void StartTimer();
    public Task PauseTimerAsync();
    public void AddLap();
    public Task StopTimerAsync();

    // S10 — pure key-to-shortcut mapping, called from MainForm.ProcessCmdKey (§10.4); no window
    // needed to test it.
    public static StopwatchShortcut? MapShortcut(Keys keyData);
}

public sealed class TrayIconService : IDisposable   // §10.1/§10.2 — NotifyIcon owner
{
    // Takes callbacks, not a MainForm reference, per §3's "events or callback delegates, never
    // shared mutable state." Constructed with the StopwatchControl whose action methods (above)
    // the tray menu's transition items call, and whose Tick/StateChanged events MainForm relays
    // into UpdateDisplay.
    public TrayIconService(StopwatchControl control, Action onOpen, Action onExit);
    public bool DarkMode { get; set; }   // default false; S12 wires it to the OS setting
    public void UpdateDisplay(long elapsedMs, bool running, bool paused);
    public void Dispose();
}

public sealed class RecordsListControl : UserControl   // §8.5 — records/laps panels
{
    // Data is pushed in, never pulled: UpdateRecords(IReadOnlyList<StopwatchRecord>) and
    // UpdateLaps(IReadOnlyList<Lap>) each replace the displayed rows and toggle the relevant
    // empty-state/visibility rules from §8.5. Raises `event Action? ClearAllRequested`; never
    // reads IStopwatchStore itself.
}
```

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
    <Platform>x64</Platform>
    <Version>1.8.0</Version>

    <!-- Required by LibraryImportAttribute-based source-generated interop (SYSLIB1062); see
         TrayIconService's DestroyIcon P/Invoke and §17. -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- WinForms application configuration (source-generates ApplicationConfiguration.Initialize) -->
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
    <ApplicationVisualStyles>true</ApplicationVisualStyles>
    <ApplicationUseCompatibleTextRendering>false</ApplicationUseCompatibleTextRendering>

    <!-- S14 (§17) — what Explorer, the pinned taskbar entry, and the published .exe read for their
         icon. Does not by itself set the live title-bar/taskbar-button icon; MainForm separately
         loads the EmbeddedResource below and assigns Form.Icon at runtime. -->
    <ApplicationIcon>Assets\app.ico</ApplicationIcon>

    <!-- Analyzers as real build gates -->
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <!-- S14 (§17) — loaded at runtime by MainForm.LoadAppIcon() to set the live window/taskbar icon.
         See the ApplicationIcon comment above for why both are needed. -->
    <EmbeddedResource Include="Assets\app.ico" />
  </ItemGroup>

</Project>
```

- `<Platforms>` only declares which platforms exist for the project; it does not select the active
  one. `<Platform>x64</Platform>` pins that selection so a direct `dotnet build <csproj>` (no
  solution involved) also lands in `bin\x64\...` — the solution file (`StopwatchApp.slnx`) separately
  declares x64 as its only platform, so a solution-level build stays consistent with it. Both are
  required; setting only one leaves the other build path on `AnyCPU`.
- `EnableNETAnalyzers` defaults on for .NET 5+, but set it explicitly so intent survives future
  edits.
- `EnforceCodeStyleInBuild` promotes `IDExxxx` code-style rules from IDE-only hints to build
  diagnostics — without it, `.editorconfig` rules are invisible outside an IDE.
- `TreatWarningsAsErrors` gives every rule real teeth.
- Escape hatch: `<CodeAnalysisTreatWarningsAsErrors>false</CodeAnalysisTreatWarningsAsErrors>`
  demotes only `CAxxxx` code-quality rules, not compiler warnings. Any suppression must be
  narrow — a justified `[SuppressMessage]` attribute or a scoped
  `#pragma warning disable ... / restore ...` pair — **never** a blanket `<NoWarn>` list.
- The version lives in exactly one place (`<Version>` above, now `1.8.0`) and must also be
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
  re-render the tray icon at the new size when DPI changes at runtime. Implemented in S12 —
  `MainForm.OnDpiChanged` calls `TrayIconService.RefreshIcon()`; see §17, dated 2026-09-11.
- Live OS dark/light-mode detection and reaction (`Application.IsDarkModeEnabled`,
  `SystemEvents.UserPreferenceChanged`) is S12's job, implemented; see §17, dated 2026-09-11, for
  the exact APIs and the category filter.
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
- **Type scale (S14, §17), defined centrally in `Theme/Typography.cs`:** display (elapsed-time
  readout) 72pt/96px bold; body (buttons, headers, dialogs, list rows) 12pt/16px; caption 9pt/12px —
  all at the 96dpi design baseline (`1pt = 4/3px`), scaling further with the OS DPI setting via
  `MainForm`'s `AutoScaleMode.Dpi`. The caption size has had no consumer since S15 removed the
  version footer (below) but stays defined — it is still §11's third type-scale step, and
  `TypographyTests` still covers `Typography.CreateCaptionFont`.
- The large elapsed-time display and the records/laps rows both use a monospace, tabular-figure font
  (`Typography.MonospaceFamilyName` — Cascadia Mono or Consolas) so digits don't shift width as they
  change and so dates/times column-align down the list.
- **Centering (S14, §17):** the elapsed-time display and the button row are both centered on the
  stopwatch card's horizontal axis via `Anchor = AnchorStyles.None` inside a `TableLayoutPanel` cell
  (not `Dock` + `TextAlign`, which is a no-op on an `AutoSize` control). The button row re-centers
  automatically as buttons swap per state.
- A **Laps** panel is shown only when `Laps.Count > 0`, newest first, scrollable — capped to 3 rows
  tall (S15, §17: the tallest 3 rows actually shown, not a flat multiple of a single-line height).
- A **Records** panel is always shown, newest first, with an empty state reading `No records yet`
  when there are none. **Only the most recent 5 are listed** (S14b, §17,
  `RecordsListControl.MaxDisplayedRecords`) — a planned, out-of-scope-for-now history/records-manager
  window will offer the full list with edit/delete; this panel is capped, not scrollable.
- **Header row (S15, §17):** the `Records` label and the two action buttons below share one line —
  `Records` left-aligned, `Manage Records` then `Clear All Records` right-aligned — replacing S14's
  stacked label-then-button-row layout, per the repo owner's markup of a screenshot: "the buttons and
  the title 'Records List' should be on the same line."
- **Row word-wrap, not clip (S15, §17):** a records/laps row past the sized-for worst case (a session
  over `MainForm.WorstCaseElapsedMinutes`, a lap id past 3 digits) word-wraps to a second line
  (`RecordsListControl.MeasureRowHeight`/owner-drawn `DrawMode.OwnerDrawVariable`), instead of S14's
  `EndEllipsis` clipping. Each list box's, and the records host panel's, height is recomputed on
  every `UpdateRecords`/`UpdateLaps` call from the real (possibly wrapped) height of the rows actually
  shown — never a flat per-row constant — so the window (§10.3) can size itself to match.
- A `Clear All Records` button is visible only when `Records.Count > 0` (the *actual* total, not the
  capped display count) and, when clicked and confirmed, clears every persisted record — not just
  the 5 shown. Styled red (`Palette.StopButton`, S15, §17) via the extracted `GlyphButton` (below),
  matching the destructive-action color used elsewhere.
- A **`Manage Records`** button (S17, §17) sits beside `Clear All Records`, is always enabled, and
  opens one modeless manager window. Styled blue (`Palette.LapButton`).
- The manager lists every saved record newest first, 10 per page, with Previous/Next navigation and
  confirmed per-row `Delete` actions. Its header also has a confirmed `Clear All Records` action;
  the main card's own Clear All shortcut remains.
- **`GlyphButton` (S15, §17):** the S11a owner-drawn rounded button, originally private to
  `StopwatchControl`, is extracted to its own `Controls/GlyphButton.cs` so `RecordsListControl`'s
  header-row buttons and `ClearRecordsDialog`'s confirm/cancel buttons can reuse the same rounded,
  palette-driven paint routine — with a `Glyph?` (nullable) glyph for a text-only button, and a
  muted, palette-driven `Enabled = false` visual state (blended toward `Palette.CardBackground`/
  `Palette.MutedText`) instead of the stock gray. S16 centers every label on the full button axis;
  icon-bearing buttons reserve the glyph lane on both sides so their label remains centered without
  overlapping the glyph.
- A confirm dialog, titled `Clear All Records`, body text
  `Are you sure you want to clear all records? This action cannot be undone.`, buttons `Cancel` and
  `Clear All`, both the extracted `GlyphButton` (S15, §17) — `Clear All` red (`Palette.StopButton`),
  `Cancel` dark slate (`Palette.CancelButton`, a new non-destructive-action token) — with equal
  top/bottom margins so the two sit on the same baseline (S14's mismatched default/explicit margins
  had misaligned them). Confirming clears the records table and reloads the (now empty) list.
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
created on first run if it doesn't exist. Rows are mapped via **Dapper** — by column name, against
the model records' own positional constructors — not by hand-written ordinal `reader.Get*` calls.
Dapper is a thin extension-method layer over `Microsoft.Data.Sqlite`'s `SqliteConnection`; it does
not replace the provider, add a design-time tool, or generate any code, so it carries no build-gate
cost. Parameters use SQLite's `@name` form (Dapper does not recognize the `$name` form used before
this layer was introduced).

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

This DDL is now **migration 1** in `SchemaMigrations.cs`, not code run unconditionally on every
startup — see "Schema versioning" below.

**Migration 2 (S11b)** adds a third table, single-slot like `paused_session`, holding the one
remembered manual window position (§10.6):

```sql
CREATE TABLE window_position (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  x  INTEGER NOT NULL,
  y  INTEGER NOT NULL
);
```

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

Task SaveWindowPositionAsync(int x, int y);
  // upsert into the single-row window_position table (id = 1) — S11b

Task<(int X, int Y)?> LoadWindowPositionAsync();
  // returns null if no row exists, or if the row fails to deserialize — S11b
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

Render a 32×32 icon with GDI+ and convert it to an `HICON`. It uses one simple, unbounded
progression while the tooltip remains authoritative:

- **Elapsed time under one hour**: a single large two-digit **MM** readout, centered and sized to
  fill as much of the 32×32 canvas as legibility at the 16 px scaled-down display size allows.
- **Elapsed time from one hour through 23:59**: a single large unpadded whole-hour label, `1H`
  through `23H`; it changes only at the next whole hour.
- **Elapsed time from one day onward**: a single large unpadded whole-day label, `1D`, `2D`, and so
  on; it changes only at the next whole day.

Hour and day labels are rendered at the largest font size that fits their actual text, so the common
`1H`/`1D` labels are not constrained by the width of `23H`. A paused stopwatch freezes whichever
layout it reached; running, paused, and idle may differ in tint but not layout.

Update the icon at most once per second, and only when the displayed visual-day value **or the active
layout** changes — track both explicitly in the dirty-check state.

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

**The window is non-resizable by the user, but not a fixed size (S14, revised S15, §17).**
`FormBorderStyle.FixedSingle`, `MaximizeBox = false` — the frame cannot be dragged wider or taller.
Width is `MainForm.RequiredClientWidth` (a session that ran a full 24 hours,
`MainForm.WorstCaseElapsedMinutes`, is the design worst case a row is sized for; anything longer
word-wraps per §8.5 instead of widening the window further), scaled to the live device DPI. **Height
is content-driven** (S15, §17: `MainForm.ResizeToContent`) — the window ends a few pixels below
whatever is actually shown (idle vs. running, how many records/laps), recomputed on every content
change (`RefreshRecords`, `RefreshLaps`, the stopwatch card's `StateChanged`, `OnDpiChanged`) by
asking the real, live control tree for its preferred size, rather than S14's single constant sized
for the worst case of everything visible at once. Both dimensions are clamped to
`Screen.PrimaryScreen.WorkingArea` on every resize — at higher OS scaling the requested width could
otherwise exceed the working area with no way for the user to shrink it back — and the window's
`Location` is nudged back on-screen (`ClampLocationToWorkingArea`) if a resize would otherwise push
part of it past the working area's edge. This sizing behavior is independent of the
`ControlStyles.ResizeRedraw` fix that stops the stale-border repaint artifact (§17) — the two are not
the same thing.

**Window and taskbar icon (S14, §17).** `MainForm.Icon` is set from the embedded
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
shortcut pressed in a state where it doesn't apply.

### 10.5 Single instance

Named `System.Threading.Mutex` created at startup. If a second instance detects the mutex already
exists, it locates the first instance's window with `FindWindow` (matched by `MainForm.WindowTitle`
— **fixed for the life of one build**, not literally constant text: S15, §17 folds the version into
it, `$"Stopwatch v{Application.ProductVersion}"`, replacing the deleted version footer label, §8.5 —
but both the running instance and the one calling `FindWindow` are the same build, so the strings
always match) and sends it a registered window message (via `RegisterWindowMessage` + `PostMessage`)
asking it to restore and activate itself, then exits immediately — never runs a second copy.

**Not `PostMessage(HWND_BROADCAST, ...)`**, despite that being the originally-planned mechanism
(§17, dated 2026-09-11): once hidden to tray, §10.3's `ShowInTaskbar = false` gives the window an
owner (the mechanism WinForms uses to drop its taskbar button), and Windows excludes owned windows
from `HWND_BROADCAST` delivery regardless of visibility — so a broadcast posted while the window is
hidden is silently never delivered, in exactly the one state single-instance activation exists to
handle. A direct, title-targeted `FindWindow` lookup is not subject to that exclusion.

### 10.6 Window position (always centered)

**The window always centers itself and never remembers a position.** Added post-hoc at the repo
owner's explicit request dated 2026-09-11, then reversed at the repo owner's explicit request dated
2026-09-12 (see §17) — the app never should have remembered a position at all. Every "Open"
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
| Lap (blue) — also `Manage Records` (S15) | `#2563EB` | `#1D4ED8` | `#1E40AF` |
| Stop (red) — also `Clear All Records`/dialog `Clear All` (S15) | `#DC2626` | `#B91C1C` | `#991B1B` |
| Cancel (dark slate, S15, §17) — dialog `Cancel` | `#334155` | `#1E293B` | `#0F172A` |

`Theme/Palette.cs` holds the color and spacing tokens above; `Theme/Typography.cs` (S14, §17) is the
second token file, holding the type scale (§8.5) and the shared monospace-family resolution.

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
- Minimize-to-tray (S15, §10.3/§17 — fixes a regression where minimize left a taskbar button, and
  closing while minimized reopened the window still minimized): minimizing via the title-bar button
  removes the taskbar button exactly like Close does, and every "Open" transition (tray Open/
  single left-click, single-instance activation) always restores the window in its normal (not minimized)
  state, regardless of whether it was minimized when last hidden.
- Tray icon layout: while elapsed hours == 0, the icon shows large minute-only digits; once elapsed
  reaches 1 hour, it switches to the stacked hours-over-minutes layout at exactly that boundary; the
  tooltip's full `HH:MM:SS` text is unaffected by which layout is showing.
- Keyboard shortcuts: with the main window focused, `Space` starts/pauses/continues, `Shift+Space`
  laps, and `Enter` stops, matching the mouse-click behavior of the same buttons; none of the three
  fires while the window is hidden to the tray or while `ClearRecordsDialog` is open.
- Window position (S14b, §10.6): the window always opens centered on the primary screen — on first
  launch, tray Open/single left-click, single-instance activation, and restore-from-minimize alike. It
  never remembers or restores a previous position, and cannot be dragged-then-resized since the frame
  itself is non-resizable (§10.3).
- Window frame and icon (S14, §10.3): the frame cannot be resized (no maximize button, no drag on
  any edge/corner); the stopwatch icon shows in the title bar, the taskbar button, and on the built
  `.exe` in Explorer; the chrono, buttons, and record/lap rows render at the S14 type scale
  (48px/16px/16px) and stay centered as the button set changes state.
- Window sizes to content, not a fixed constant (S15, §10.3/§17): the window's width fits the
  sized-for-24-hours record/lap row and its height ends a few pixels below whatever is actually
  shown, growing/shrinking live as records are added or cleared, as laps appear (up to 3 rows tall)
  or clear, and as the "Resumed from a pause" note appears/disappears — never a band of dead space
  to the right of or below the visible rows.
- Title bar shows the version (S15, §17 — replaces the deleted version-footer label): the title bar
  reads `Stopwatch v{Application.ProductVersion}` (e.g. `Stopwatch v1.3.0`).
- Records header row (S15, §8.5): `Records` sits left-aligned on the same line as, not stacked above,
  `Manage Records` (blue) and `Clear All Records` (red), both right-aligned in that order.
- Row word-wrap (S15, §8.5): a record/lap row longer than the sized-for worst case (over
  `MainForm.WorstCaseElapsedMinutes`, or a lap id past 3 digits) wraps to a second line inside its row
  card instead of clipping or ellipsizing.
- Records display cap (S14b, §8.5): only the 5 most recent records are listed in the main window
  regardless of how many are persisted; "Clear All Records" still clears every persisted record, and
  its own visibility still reflects the true total, not the capped display count.
- Manage Records window (S17, §8.5): `Manage Records` is enabled and opens a single modeless,
  owner-managed window that lists all records newest first, paginated at 10 rows per page. Each row
  has a confirmed Delete action; the header has a confirmed Clear All action. Add/edit behavior is
  out of scope, and the main card's Clear All shortcut remains.
- Clear-all dialog styling (S15, §8.5): `Clear All` renders red and `Cancel` renders dark slate, and
  the two sit aligned on the same baseline.
- Button labels (S16, §8.5/§17): every `GlyphButton` label is centered on the full button axis;
  icon-bearing buttons keep their glyph without overlapping the centered label.
- Tray open gesture (S16, §10.2/§17): a single left-click opens and activates the main window;
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

Never run `git commit` unless the user has explicitly asked for a commit to be created in that
turn. Completing a task, or making code changes, is never implicit permission to commit.

---

## 17. Design decision log

**Rule:** when implementation reveals a design choice that differs from, or is absent from, what
this file says, fold the substance into the governing section **and** log it here, dated, in the
same change. A decision that exists only as a code comment or a chat message is lost to the next
agent that opens this file cold. Keep entries short — one or two lines plus a pointer to the section
that now carries the actual rule.

- **2026-09-09 — Solution platform mapping.** An `.slnx` solution needs explicit per-project
  `<Platform Project="x64" />` entries under each `<Project>`; a bare solution-level
  `<Platform Name="x64" />` with no per-project mapping causes a solution build to silently fall back
  to `AnyCPU` (the solution build's global `Platform` property overrides a project's own
  `<Platform>` unless the solution maps it explicitly). See `StopwatchApp.slnx` and §3/§6.
- **2026-09-09 — Test project omits `GenerateDocumentationFile`.** Deliberate: xUnit test classes
  are public with no XML doc comments, and `CS1591` + `TreatWarningsAsErrors` would otherwise fail
  the build on every test method. See `StopwatchApp.Tests/StopwatchApp.Tests.csproj`.
- **2026-09-09 — CA1707 suppressed in the test project.** xUnit test names follow the
  `MethodUnderTest_Scenario_ExpectedResult` convention, which requires underscores; CA1707 (no
  underscores in identifiers) is suppressed once, module-scoped, via
  `StopwatchApp.Tests/GlobalSuppressions.cs`. This is also the project's test-naming convention,
  otherwise unstated in §13.
- **2026-09-09 — Visibility vs. XML docs.** §5 requires XML docs only on public members of
  `Services/`/`Formatting/`, but `GenerateDocumentationFile` (§6) makes `CS1591` fire on *any*
  undocumented public member, project-wide. Resolved rule: types that are part of §3.1's contract
  (`Services/`, `Formatting/`, `Theme/`, the record models) are `public` with XML docs; app-internal
  plumbing (`Program`) is `internal`. `MainForm` is `public` with minimal docs since it's referenced
  from tests.
- **2026-09-10 — Models are one-file-per-record, not `Models.cs`.** The S1–S3 roadmap stage names a
  single `StopwatchApp/Models.cs`; §5's "one type per file" is the authoritative rule and wins, so
  the three records live in `StopwatchApp/Models/{StopwatchRecord,Lap,PausedSession}.cs` under
  namespace `StopwatchApp.Models`. §3's layout tree reflects this.
- **2026-09-10 — `Database` construction shape.** §3.1 fixes `IStopwatchStore` but not how
  `Database` itself is built. Settled: `public Database(string databasePath)` (explicit path, so
  tests can point at a temp file instead of `%LOCALAPPDATA%`), `static string DefaultDatabasePath`
  (the real `%LOCALAPPDATA%\StopwatchApp\stopwatch.db` path), and `Task InitializeAsync()` (opens
  one long-lived `SqliteConnection`, idempotently runs both `CREATE TABLE IF NOT EXISTS`
  statements). Callers `new` it and `await InitializeAsync()` once at startup.
- **2026-09-10 — CA1031 suppressed once, class-scoped, on `Database`.** §9's swallow-everything
  design requires `catch (Exception)` in every storage method; a single class-level
  `[SuppressMessage("Design", "CA1031:...")]` covers all of them rather than six repeated pragmas,
  which is still narrow (one class, one rule) per §6's "never a blanket `<NoWarn>`" rule.
- **2026-09-10 — `SqliteConnection.ClearPool` required on dispose.** Microsoft.Data.Sqlite pools
  connections by default: disposing a `SqliteConnection` returns it to the pool rather than
  releasing the OS file handle, which left the database file locked immediately after
  `Database.DisposeAsync()` (surfaced by `DatabaseTests`' temp-file cleanup failing with
  `IOException`). Fixed by calling `SqliteConnection.ClearPool(_connection)` right after
  `DisposeAsync()` in `Database.DisposeAsync`. Anyone adding a second `Database`-like consumer of
  the same connection string should know pooling is on by default.
- **2026-09-10 — Test project's `IAsyncLifetime` uses `Task`, not `ValueTask`.** The installed
  xUnit is v2 (`2.9.3`), where `IAsyncLifetime.InitializeAsync`/`DisposeAsync` return `Task`; xUnit
  v3 changed this to `ValueTask`. `DatabaseTests` needs the `Task` signature to compile — relevant
  if this project ever upgrades to xUnit v3.
- **2026-09-10 — CA1001 suppressed on `DatabaseTests`.** The class owns a disposable `Database`
  field but isn't itself `IDisposable`/`IAsyncDisposable` — disposal happens through xUnit's
  `IAsyncLifetime.DisposeAsync` convention instead, which the analyzer doesn't recognize. Suppressed
  once, class-scoped, with a justification citing this.
- **2026-09-10 — S3a: Dapper adopted, EF Core rejected.** Audited whether the hand-rolled
  `Database.cs` (287 lines, ordinal `reader.Get*` mapping, no schema versioning) should move to a
  dependency. EF Core was rejected at this scale: 2 tables, 6 fixed queries, no joins/relationships/
  transactions, and `IStopwatchStore` is fixed architecture (§3.1) that no ORM would change — plus
  its generated `Migrations/*.cs` fails `CS1591` under `GenerateDocumentationFile` +
  `TreatWarningsAsErrors`, and fails `csharpier check .`. Adopted **Dapper** instead (name-based
  column→constructor-parameter mapping, no design-time tooling, no generated code — see §9) plus a
  hand-written `PRAGMA user_version` migration runner (`Services/SchemaMigrations.cs`) for the one
  genuine gap an ORM doesn't fix by default: there was no schema-evolution path at all. SQL parameter
  placeholders moved from SQLite's `$name` form (unrecognized by Dapper) to `@name`; the §9 DDL
  itself is untouched and is now migration 1. `IStopwatchStore`'s six-method surface did not change.
- **2026-09-10 — `SchemaMigrations` needs `InternalsVisibleTo`.** Kept `internal` (matching the
  `Program` precedent, §17 above) to avoid the `CS1591` burden a `public` type would carry under
  `GenerateDocumentationFile`, but `StopwatchApp.Tests` is a separate assembly and can't see
  `internal` types without it. Added `[assembly: InternalsVisibleTo("StopwatchApp.Tests")]` in a new
  `StopwatchApp/AssemblyInfo.cs`.
- **2026-09-10 — `InitializeAsync` double-call bug found and fixed while writing S3a's idempotency
  test.** The original implementation unconditionally did `_connection = new SqliteConnection(...)`
  on every call; calling `InitializeAsync()` twice opened a second connection without disposing the
  first, leaking a handle and leaving the file locked (surfaced as an `IOException` in test cleanup).
  Fixed by only opening a connection when `_connection is null`; a second call now just re-checks
  `PRAGMA user_version` against `SchemaMigrations.All` and applies nothing, since it's already
  current.
- **2026-09-10 — S4: `StopwatchTimer` built against `FakeStopwatchStore`, not a real `Database`.**
  Added `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 to `StopwatchApp.Tests.csproj` for
  `FakeTimeProvider` (namespace `Microsoft.Extensions.Time.Testing`, confirmed via Context7 against
  `/dotnet/extensions`). Since `StopwatchTimer` depends only on `IStopwatchStore` (§3.1), tests use a
  new in-memory `FakeStopwatchStore` instead of a temp-file `Database` — no SQLite in the state-
  machine test suite at all. See §13.
- **2026-09-10 — `RestoreAsync` also reloads `Records`.** §3.1's public surface names `RestoreAsync`
  only for restoring a paused session, but "records ownership" (§3.1) already commits
  `StopwatchTimer` to owning the records list, and nothing else in the fixed interface ever
  populates it — so a freshly constructed `StopwatchTimer` would otherwise expose an empty
  `Records` list forever until the first `StopAsync`. `RestoreAsync` now loads `Records` first,
  then checks for a saved paused session. See §3.1.
- **2026-09-10 — `Tick()` is a no-op while not running.** §8.2's tick pseudocode has no explicit
  guard, but §8.6 requires `OnTick` to never fire "while paused or stopped," and `ElapsedMs` must
  stay frozen at its last value while paused (§8.3's `Pause()` comment) — computing
  `Now - StartTime` unconditionally would silently overwrite the frozen value if the UI timer (S5)
  ever ticked while not running. `StopwatchTimer.Tick()` returns immediately when `!IsRunning`.
- **2026-09-10 — S5: `StopwatchControl` does not subscribe UI updates to `StopwatchTimer`'s events.**
  `PauseAsync`/`StopAsync`/`RestoreAsync` use `ConfigureAwait(false)` internally (§9), so the
  continuation that invokes `OnPause`/`OnStop` can resume off the UI thread — wiring
  `Timer.OnPause += UpdateDisplay` etc. directly would risk a cross-thread control-property write.
  Instead each button `Click` handler calls its `StopwatchTimer` method, then calls `UpdateDisplay()`
  itself right after the `await` — since the *handler's own* await has no `ConfigureAwait(false)`,
  it resumes on the captured UI `SynchronizationContext` regardless of what thread the awaited call
  completed on. `System.Windows.Forms.Timer.Tick` is exempt (it always fires on the UI thread), so
  its handler calls `Timer.Tick()` + `UpdateDisplay()` directly. See §3.1/§8.5.
- **2026-09-10 — S5: dark-mode detection deferred to S12; `StopwatchControl` exposes a `DarkMode`
  bool.** `Theme/Palette.cs` (§11) takes an explicit `dark` bool per call, but nothing wires the
  real OS setting yet — that is S12's stated job ("apply Palette to every custom-painted surface").
  `StopwatchControl.DarkMode` (default `false`) lets S12 flip it once real detection exists, without
  S5 having to guess at that mechanism now. `[DesignerSerializationVisibility(Hidden)]` is required
  on it (WFO1000) since the control isn't designer-serialized.
- **2026-09-10 — S5: monospace font resolved at runtime, not hardcoded.** §8.5 allows either
  "Cascadia Mono or Consolas." `StopwatchControl` checks `System.Drawing.Text.InstalledFontCollection`
  for "Cascadia Mono" and falls back to "Consolas" (present on all supported Windows 11 installs)
  if it's absent, rather than hardcoding one and risking a silent GDI substitution to a
  non-monospace default font.
- **2026-09-10 — S6: `ClearRecordsDialog.ShowConfirm`'s `DialogResult` mapping.** §3.1 fixes the
  method's signature but not its return value. Settled: `DialogResult.Yes` means "Clear All" was
  clicked; `DialogResult.Cancel` covers the "Cancel" button, Escape, and the dialog's close button
  (all wired to the same `CancelButton`). No `AcceptButton` is set, so pressing Enter never
  triggers the destructive action by default. `MainForm` (S7) must check for `DialogResult.Yes`
  specifically, not merely "not Cancel." See §3.1.
- **2026-09-10 — S6: `RecordsListControl` uses stock controls, not owner-drawn rows.** §7 requires
  manually-drawn surfaces to read `Theme/Palette.cs`; stock `ListBox`/`Label`/`Button` already
  follow `Application.SetColorMode(SystemColorMode.System)` for free, so no owner-draw was added.
  The one exception is the "No records yet" empty-state label, whose muted tone the OS theme
  doesn't supply on its own — it's colored from `Palette.EmptyStateText`. `RecordsListControl`
  exposes a `bool Dark` property (default `false`, `[DesignerSerializationVisibility(Hidden)]` to
  satisfy `WFO1000`) that reapplies that one color; wiring it to the live OS setting is S12's job,
  not this stage's. See §3.1.
- **2026-09-10 — S7: records clearing stays owned by `StopwatchTimer`.** `IStopwatchStore` exposes
  `ClearAllRecordsAsync`, but the timer owns its in-memory `Records` cache and the only pre-S7
  reload path (`RestoreAsync`) also applies a paused-session snapshot. `MainForm` must therefore
  call `StopwatchTimer.ClearRecordsAsync()`, which clears the store, reloads the cache through the
  timer's private reload path, and raises `RecordsChanged`; it must not mutate the cache or reuse
  `RestoreAsync` as a records reload.
- **2026-09-10 — S7: `StopwatchControl.StateChanged` is the parent notification boundary.**
  `StopwatchTimer` has no lap-specific event, while `RecordsListControl` needs a pushed update as
  soon as the user clicks Lap. The control raises `StateChanged` after Start, Pause, Lap, Stop, and
  Restore display updates; `MainForm` uses it only to push the timer's current laps into the list.
  `RecordsChanged` remains the sole notification for the records list; both it and `StateChanged`
  are marshaled through `Control.Invoke` on the `MainForm` side, because service continuations
  (`RecordsChanged`) may run off the UI thread and callers should not need to know which case
  applies.
- **2026-09-10 — M7 follow-up: `Database.ClearAllRecordsAsync` already had a real-SQLite test.**
  A review pass while starting S8 flagged this as a gap, but `DatabaseTests.ClearAllRecordsAsync_EmptiesTheTable`
  (added in S3a) already covers it against a temp-file database — no test was added or was needed.
- **2026-09-10 — M7 follow-up: `MainForm.InitializeAsync` now catches `Database.InitializeAsync`
  failures.** §9's swallow-everything rule deliberately excludes `InitializeAsync` (a half-migrated
  schema must not pass silently), but nothing previously caught it either, so a locked or corrupt
  database file crashed the app from an unhandled exception in an `async void Load` handler —
  reachable today since single-instance (S11) doesn't exist yet. `MainForm` now catches
  `SqliteException`/`IOException`/`UnauthorizedAccessException` narrowly (§5: never a bare
  `Exception`), shows a `MessageBox` naming the database path, and calls `Close()`. `Close()` here
  reports `CloseReason.None`, not `UserClosing`, so S9's hide-to-tray override does not intercept
  it — the app actually exits. See §10.3's entry below for why that distinction is load-bearing.
- **2026-09-10 — S8: the tray's once-a-second heartbeat is `StopwatchControl`'s existing UI timer,
  not a second one.** `StopwatchTimer.OnTick` fires only every 5 seconds and from inside
  `ConfigureAwait(false)` continuations — wrong frequency and, per the S5 entry above, off the UI
  thread. `StopwatchControl` already runs a 1000 ms `System.Windows.Forms.Timer` (started only
  while running) for its own display; it now also raises a public `Tick` event from that same
  handler. `TrayIconService` never owns a `Timer` of its own. Because that timer stops while idle
  or paused, `MainForm` also refreshes the tray from `StateChanged` to catch those transitions.
- **2026-09-10 — S8: tray menu actions call `StopwatchControl`'s new action methods, never
  `StopwatchTimer` directly.** Invoking `Timer.PauseAsync()` from a menu item would change state
  behind the window's back, leaving its label and buttons stale. `StopwatchControl` gained
  `StartTimer()`/`PauseTimerAsync()`/`AddLap()`/`StopTimerAsync()` — each does exactly what its
  button's `Click` handler already did (call the timer, `UpdateDisplay()`, raise `StateChanged`) —
  and the four `Click` handlers now call them too, so each body exists in exactly one place. The
  tray menu (and S10's hotkeys, later) route through these, not through `Timer`.
- **2026-09-10 — S8: `TrayIconService` takes callbacks and a `StopwatchControl`, not a `MainForm`
  reference.** `MainForm` exposes no public API beyond its constructor, and §3 mandates
  communication "through events or callback delegates... never shared mutable state." The
  constructor takes `(StopwatchControl control, Action onOpen, Action onExit)`; `MainForm` passes
  its own `RestoreWindow`/`ExitApplication` methods as the callbacks.
- **2026-09-10 — S8: tray icon tint reads `Theme/Palette.cs`, not new literals.** §10.1 makes tint
  optional ("may differ"); running uses `Palette.Accent`, paused `Palette.PauseButton.Base`, idle
  `Palette.MutedText` — same digit layout in all three. `TrayIconService.DarkMode` (default `false`)
  follows the `StopwatchControl.DarkMode`/`RecordsListControl.Dark` precedent; S12 wires it to the
  OS setting.
- **2026-09-10 — S8: `AllowUnsafeBlocks` added to the csproj.** `TrayIconService`'s `DestroyIcon`
  P/Invoke uses `[LibraryImport]` (required — a plain `[DllImport]` trips `SYSLIB1054` and a
  `public` one trips `CA1401` under `TreatWarningsAsErrors`). The source generator itself requires
  unsafe code for the generated marshalling stubs (`SYSLIB1062`), so `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
  is now in §6's fixed property block. No other unsafe code exists or is permitted elsewhere in the
  project — this is interop plumbing, not a license for `unsafe` blocks in application code.
- **2026-09-10 — S9: minimize interception uses `OnResize`, not a `WndProc`/`SC_MINIMIZE` hook.**
  §10.3 offers either; `OnResize` checking `WindowState == FormWindowState.Minimized` is simpler,
  and S10 will add its own `WndProc` override for `WM_HOTKEY` separately, so the two stay
  independent rather than sharing one override.
- **2026-09-10 — S9: database disposal moved from `FormClosed` into `ExitApplication`.** The prior
  `FormClosed` handler was `async void` with no pump guaranteed to still be running once
  `Application.Run` returns, so `Database.DisposeAsync`'s continuation (including its
  `SqliteConnection.ClearPool` call) was not guaranteed to complete. Since §10.3 makes the tray's
  `Exit` item the only real quit path once `FormClosing` cancels `UserClosing`, disposal now happens
  in `MainForm.ExitApplication()` — `_trayIconService.Dispose()`, then `await _database.DisposeAsync()`,
  then `Application.Exit()` — where the `await` runs on a message loop that is still alive.
- **2026-09-11 — S10: global hotkeys dropped in favor of window-scoped keyboard shortcuts.** While
  planning S10, a blocking flaw surfaced in the originally-specified `RegisterHotKey` design: a
  registered hotkey is bound to one specific `HWND`, and `MainForm.HideToTray()` sets
  `ShowInTaskbar = false`, which makes WinForms destroy and recreate the form's native window
  handle (it governs `WS_EX_APPWINDOW`/the owner relationship, which can't be changed on a live
  handle). The hotkeys would silently stop firing after the very first hide-to-tray — exactly the
  state global hotkeys exist to serve — and `UnregisterHotKey` on shutdown would then target a
  handle the registration was never bound to. Decision: drop global/system-wide hotkeys from scope
  entirely (§1) rather than work around the handle lifecycle (e.g. a message-only `NativeWindow`);
  this app is mouse-driven and a hidden-window-only shortcut has no use for a *global* binding.
  Replaced with three ordinary, window-scoped keystrokes — `Space` (toggle Start/Pause/Continue),
  `Shift+Space` (Lap), `Enter` (Stop) — dispatched from `MainForm.ProcessCmdKey` through the same
  `StopwatchControl` action methods S8 already added for the tray menu. See §10.4 for the full
  spec and §3.1 for `MapShortcut`'s shape.
- **2026-09-11 — S11: `user32.dll`'s bare `RegisterWindowMessage`/`PostMessage`/`FindWindow` names
  aren't real export names.** A first `[LibraryImport("user32.dll")]` attempt using the bare
  function names built clean but crashed every launch with
  `EntryPointNotFoundException: Unable to find an entry point named 'RegisterWindowMessage'` — the
  actual exports are the `...W` (wide-string) forms; the bare names are C-header macros, not
  linkable symbols. Fixed with an explicit `EntryPoint = "RegisterWindowMessageW"` /
  `"PostMessageW"` / `"FindWindowW"` on each `[LibraryImport]`, alongside
  `StringMarshalling = StringMarshalling.Utf16` on the two that take a `string` parameter.
- **2026-09-11 — S11: `HWND_BROADCAST` does not reach the window once hidden to tray; switched to a
  `FindWindow`-targeted `PostMessage`.** The originally-specified §3.5/§10.5 mechanism
  (`RegisterWindowMessage` + `PostMessage(HWND_BROADCAST, ...)`) worked when tested against a
  visible window, but silently failed the moment the window was hidden to tray first — exactly the
  scenario this feature exists to serve. Root cause, confirmed by driving both instances externally
  via PowerShell P/Invoke (`EnumWindows` + `GetWindowThreadProcessId` to inspect the live
  owner/visibility state of the actual window handle, which §10.3's handle-recreation note above
  means is a *different* HWND than the one the form started with): `HWND_BROADCAST` is documented
  to reach invisible windows only if they're *unowned*, and §10.3's `ShowInTaskbar = false` gives
  the recreated handle an owner as part of how WinForms drops its taskbar button — so the broadcast
  is accepted (`PostMessage` returns success) but never queued to that window at all. Fixed by
  having the second instance call `FindWindow(null, MainForm.WindowTitle)` to get the first
  instance's window handle directly (unaffected by ownership) and `PostMessage` it there instead of
  broadcasting; `MainForm.WindowTitle` (`"Stopwatch"`) is now a shared `internal const` so both
  classes reference the same literal. See §10.5.
- **2026-09-11 — S11a: `Palette.cs`'s spacing/radius/elevation additions, exact shape.** Extended
  alongside the existing color tables per §3.1's "same per-role accessor shape": six `public const
  int` spacing constants (`SpacingXs`=4, `SpacingSm`=8, `SpacingMd`=12, `SpacingLg`=16,
  `SpacingXl`=24, `SpacingXxl`=32), three `public const int` corner radii (`ControlCornerRadius`=6,
  `CardCornerRadius`=8, `DialogCornerRadius`=12), and three elevation accessors —
  `Color ShadowResting(bool dark)`/`Color ShadowHover(bool dark)` (both `rgba`-derived per the S11a
  comp: light ≈ `rgba(15,23,42,.07)`/`.14)`, dark ≈ `rgba(0,0,0,.5)`/`.55)`) and a non-parameterized
  `Color TopEdgeHighlight` (the dark-mode mica top-edge line, `rgba(255,255,255,.08)`-ish). All new
  color values are additive to §11's table, not a change to any existing hex value in it. See §11.
- **2026-09-11 — S11a: GDI+ elevation translated as owner-drawn rounded outlines + existing
  hover/pressed fills, not a rendered drop shadow.** Per the stage brief's explicit "don't
  over-engineer a shadow renderer" guidance: no layered-window/blur shadow system was built.
  Instead — (1) `StopwatchControl` and `RecordsListControl` each draw a 1px rounded-rect outline in
  `Palette.ShadowResting(dark)` around their own bounds in `OnPaint` (the "card" resting-elevation
  cue); (2) the four transport buttons and the two `ClearRecordsDialog` buttons get
  `Palette.ControlCornerRadius` rounded corners; (3) button hover/pressed feedback continues to be
  the pre-existing base/hover/pressed color swap (§11) — no separate shadow layer was added there,
  since the brief names a background-color shift as an acceptable, cheaper substitute; (4) in dark
  mode only, a hovered/pressed transport button additionally draws a 1px `Palette.TopEdgeHighlight`
  line across its top edge (the "mica" cue) — `Palette.ShadowHover` is defined for symmetry with the
  comp's token set but nothing in this pass currently renders it, since no owner-drawn surface here
  has a distinct "raised" state beyond hover/pressed. See §11 and the `StopwatchControl`/
  `RecordsListControl` `OnPaint` overrides.
- **2026-09-11 — S11a: rounded-rectangle path math shared via a new internal
  `StopwatchApp/Theme/RoundedRectangle.cs` helper.** Not in the stage's named file list, but a
  single `internal static GraphicsPath Path(Rectangle, int radius)` avoids re-deriving the same
  4-arc `GraphicsPath` construction independently in `StopwatchControl` (buttons + card border),
  `RecordsListControl` (row cards + panel border), and `ClearRecordsDialog` (button corner
  `Region`s) — the same "one body, not three copies" principle §17's S7 entries already apply
  elsewhere in this codebase. `internal`, so it carries no `GenerateDocumentationFile` obligation
  under §5's public-only XML-doc rule, though it is documented anyway.
- **2026-09-11 — S11a: transport-button glyphs are `GraphicsPath`/`FillPolygon`-drawn shapes, not
  embedded icon resources.** Implemented in a private nested `StopwatchControl.GlyphButton : Button`
  (owner-painted via `ControlStyles.UserPaint`) that draws its state color fill, an optional dark-
  mode top-edge highlight, a monochrome glyph (`Play`/`Pause`/`Flag`/`Stop`, one `private enum
  Glyph` case each), and the unchanged button text — no new image assets, no font-icon dependency,
  and the glyph color always matches the existing white `ForeColor` used in both themes. Button
  labels, left-to-right order, and base/hover/pressed colors are byte-for-byte the §8.5/§11 values;
  only the corner radius, spacing, and the addition of a glyph changed. `GlyphButton`'s
  `GetPreferredSize` override must be `public` (`ButtonBase.GetPreferredSize` is `public virtual`;
  C# forbids narrowing an override's accessibility even on a `private` nested class), which does not
  widen `StopwatchControl`'s own public surface — verified via `git diff` showing that `public` only
  on the nested type. See §3.1 (no public interface change) and §8.5.
- **2026-09-11 — S11a: `RecordsListControl`'s two `ListBox`es switched to
  `DrawMode.OwnerDrawFixed` with `SelectionMode.None`.** Needed to render each row as its own small
  rounded card (`Palette.RowBackground`/`Palette.Border`, previously-unused accessors) with S11a's
  spacing inset; `SelectionMode.None` was added alongside it because the stock selection highlight
  has no counterpart in the custom-painted row and nothing in the app ever reads `SelectedIndex` —
  this is an internal rendering detail, not a change to `RecordsListControl`'s public surface (still
  just `UpdateRecords`/`UpdateLaps`/`ClearAllRequested`/`Dark`, per §3.1). Row text templates and the
  "No records yet" empty state are byte-for-byte unchanged (§8.5); only the chrome around each row
  differs. Both list boxes and the control's own `BackColor` are now driven by
  `Palette.CardBackground(dark)` (previously the control had no explicit background), giving
  `RecordsListControl` the same card treatment as `StopwatchControl`.
- **2026-09-11 — S11a: `ClearRecordsDialog`'s outer frame is not manually rounded; only its two
  buttons are.** `Palette.DialogCornerRadius` (12px) is defined for the comp's token set but not
  rendered by this dialog: Windows 11's DWM already rounds top-level window frames by default, so a
  manual `Region`/layered-window trick would duplicate what the OS already provides and risks the
  exact flicker/perf cost the stage brief says to avoid. The `Clear All`/`Cancel` buttons instead get
  a one-time `Region`-clipped rounded corner (`Palette.ControlCornerRadius`) computed after
  `dialog.PerformLayout()`, since the dialog is shown once and never resized — cheaper than an
  owner-drawn `OnPaint` loop for a static dialog. `ShowConfirm`'s signature and `DialogResult`
  mapping are unchanged (§3.1). Neither button gained a `Palette` color triplet, since §11 assigns
  colors only to the four transport buttons, not to this dialog's two buttons.
- **2026-09-11 — S11a: `Services/TrayIconService.cs` needed no code change.** Its `RenderIcon` tint
  already reads `Palette.Accent`/`Palette.PauseButton.Base`/`Palette.MutedText` (set in S8, §17
  above), and this stage changes no existing `Palette` hex value — only adds new spacing/radius/
  elevation members alongside them — so the tray icon's rendered color is unchanged output for
  unchanged input. Its two-stacked-digit-rows layout stays exactly as §10.1 specifies, per this
  stage's explicit out-of-scope note. No manual verification gap beyond what §4 item 5 already
  covers, since nothing in this file was touched.
- **2026-09-11 — Wide, clock-style tray icon investigated and rejected — not a supported Windows
  capability.** The repo owner asked whether the tray icon could render wide (multi-character) like
  the Windows taskbar clock, instead of the current 32×32 two-stacked-digit-rows icon. Researched via
  Context7 against `H.NotifyIcon` (`/havendv/h.notifyicon`), the most widely used .NET tray-icon
  library (WPF/WinUI/MAUI/Console): its `TrayIcon` wrapper and `GeneratedIconSource` dynamic-icon
  feature are built on the same `Shell_NotifyIcon` Win32 API this project's `TrayIconService` already
  P/Invokes directly, and render text into a square icon bitmap — exactly what `RenderIcon` already
  does. No wide/rectangular notification-area mode, clock-style widget, or supported multi-icon trick
  exists in that ecosystem. The Windows taskbar clock is rendered by `explorer.exe`'s own taskband, a
  first-party UI element with no public API surface — not a `Shell_NotifyIcon`, which is the only
  mechanism available to third-party apps and which the shell always places in a fixed **square**
  slot. Conclusion: §10.1's two-stacked-digit-rows design stays as the final tray rendering; no
  further work planned here.
- **2026-09-11 — S11b added: window centering + manual-position memory.** Repo owner requested,
  independent of any implementation-time discovery: the window should always open centered, unless
  the user has dragged it, in which case it should reopen at that exact position instead — until
  moved again. Centers on `Screen.PrimaryScreen.WorkingArea` (not the screen under the cursor, for
  deterministic/verifiable behavior); persists only on hide-to-tray/exit, not on every drag frame
  (matches the existing `paused_session`-on-transition pattern rather than adding continuous
  `Move`-event write traffic); a saved position that no longer intersects any connected screen falls
  back to centering. New single-slot `window_position` table is schema migration 2 — the first real
  exercise of the append-only `PRAGMA user_version` path S3a built (previously only migration 1
  existed). Full spec: §9 (data layer), §10.6 (behavior), §14 (acceptance criteria); roadmap stage:
  `plan-stopwatch-csharp-winforms-modern-dotnet.md` §5 "S11b — Window position memory".
- **2026-09-11 — S11c added: tray icon switches to large minute-only digits under an hour.**
  Follow-up to the "wide tray icon" question logged above. The repo owner clarified the actual ask
  was not clock-width, at most "two icons wide" — the goal being legible minute-ticking in the tray,
  not literal parity with the taskbar clock. A two-separate-`NotifyIcon`-instances layout (one icon
  per digit pair) was offered as the closest technical match, but rejected: Windows has no API to
  keep two tray icons adjacent, and a user can independently hide or reorder either one via the OS's
  own tray-icon settings, silently breaking the "one widget" illusion — an unacceptable fragility for
  a two-option choice the owner was asked to make explicitly. Settled instead on a single-icon change:
  `TrayIconService.RenderIcon` now switches, at elapsed hours == 0, to a large two-digit MM-only
  layout filling the full 32×32 canvas (meaningfully more legible than the existing stacked layout),
  falling back to the original hours-over-minutes stacked layout once elapsed reaches 1 hour — no
  adjacency risk since it stays exactly one `NotifyIcon`. Full spec: §10.1 (behavior); roadmap stage:
  `plan-stopwatch-csharp-winforms-modern-dotnet.md` §5 "S11c — Tray icon: large minutes under an
  hour".
- **2026-09-11 — S11b: `window_position` upsert follows `paused_session`'s exact
  `INSERT ... ON CONFLICT(id) DO UPDATE SET` shape.** `Database.SaveWindowPositionAsync` mirrors
  `SavePausedSessionAsync` byte-for-byte in structure: a single `ExecuteAsync` with
  `INSERT INTO window_position (id, x, y) VALUES (1, @x, @y) ON CONFLICT(id) DO UPDATE SET x =
  excluded.x, y = excluded.y;`, same swallow-errors try/catch. `LoadWindowPositionAsync` reads
  through a private `WindowPositionRow` mapping record, same pattern as `PausedSessionRow`. That
  row type had to be declared with `long X, long Y`, not `int X, int Y`, even though the public
  `IStopwatchStore` surface is `int`: Dapper's constructor-based materialization for a record type
  requires an exact parameter-type match against each column's runtime CLR type, and
  Microsoft.Data.Sqlite always returns SQLite's `INTEGER` affinity as `long` — an `int`-parameter
  record throws `InvalidOperationException: ... required for ... materialization` at read time
  (caught and swallowed by §9's convention, so it silently read back as `null` until the row type
  was fixed and reproduced under a debug build). `LoadWindowPositionAsync` narrows to `int` with an
  explicit cast when projecting into the returned tuple. See `Database.cs`'s `WindowPositionRow` and
  `PausedSessionRow` (the latter avoids this by being all-`long` already).
- **2026-09-11 — S11b: migration-2 test mirrors S3a's "legacy database" test one version later, in
  the same file.** No `SchemaMigrationsTests.cs` exists in this repo — the equivalent coverage
  already lived in `DatabaseTests.cs` (`InitializeAsync_OnLegacyDatabaseWithNoVersionStamp_...`), so
  `InitializeAsync_OnV1Database_AppliesMigration2AndKeepsData` was added alongside it there instead
  of a new file. It hand-builds a v1 database (migration 1's `records`/`paused_session` DDL, one
  inserted record, `PRAGMA user_version = 1` stamped explicitly — unlike the S3a test, which leaves
  the stamp at its unset default of 0) via a raw `SqliteConnection`, then opens it through
  `Database.InitializeAsync` and asserts: `user_version` ends at `SchemaMigrations.Current` (2), the
  pre-existing record survived, `window_position` starts empty (`LoadWindowPositionAsync()` returns
  `null`), and the new table is actually usable (a save/load round-trip against it succeeds), not
  merely present.
- **2026-09-11 — S11b: `MainForm` positioning wiring — exact call sites.** `IStopwatchStore`/
  `Database`/`SchemaMigrations` implement the §9 storage surface; this entry pins where `MainForm`
  calls it, since §10.6 describes the behavior but not the method names. Four new private members:
  `PositionWindowCentered()` (sync; centers on `Screen.PrimaryScreen ?? Screen.AllScreens[0]`'s
  `WorkingArea`, also the fallback path), `PositionWindowAsync()` (awaits
  `LoadWindowPositionAsync()`, validates the saved point's bounds via
  `Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(...))`, else falls back to
  `PositionWindowCentered()`), `CurrentPersistableLocation` (a computed property:
  `Location` while `WindowState == Normal`, else `RestoreBounds.Location` — see the next entry),
  and `SaveWindowPositionIfChangedAsync()` (compares `CurrentPersistableLocation` against the new
  `_shownAtLocation` field, writes only on a difference). Call sites: the constructor calls
  `PositionWindowCentered()` synchronously (before the database exists, so it can't yet check for a
  saved position — replaces the old `StartPosition = FormStartPosition.CenterScreen`, now
  `FormStartPosition.Manual`); `InitializeAsync` (the `Load` handler) awaits `PositionWindowAsync()`
  right after `_database.InitializeAsync()` succeeds, overriding the constructor's default if a
  valid saved position exists; `RestoreWindow()` forces `WindowState = FormWindowState.Normal` then
  awaits `PositionWindowAsync()`, both before `Show()`; `HideToTray()` (shared by both the
  `FormClosing` cancel-and-hide branch and `OnResize`'s minimize branch, per S9) fires
  `SaveWindowPositionIfChangedAsync()` without awaiting it (`_ = ...;` — nothing to await inside a
  synchronous override, and the store swallows its own errors per §9); `ExitApplication()` awaits it
  directly, before disposing the database.
- **2026-09-11 — S11b: `CurrentPersistableLocation` reads `RestoreBounds`, not `Location`, whenever
  the form isn't `Normal`.** `Form.Location` is unreliable while `WindowState == Minimized` — Windows
  tracks a minimized window's actual on-screen rect (historically an off-screen sentinel) separately
  from where it should restore to, so reading `Location` at that point does not reliably reflect the
  position the user last dragged it to. This matters because `OnResize` calls `HideToTray()` (and
  therefore the save-if-changed check) at the moment `WindowState` has already become `Minimized`,
  and `ExitApplication()` can likewise run while still `Minimized` if the window was minimized-to-tray
  and never reopened before choosing Exit from the tray menu. `Form.RestoreBounds` is the WinForms-
  documented escape hatch for exactly this (`Bounds`/`Location` when `Normal`, the last known normal
  bounds otherwise), so `CurrentPersistableLocation` branches on `WindowState` and uses it for the
  non-`Normal` case. `PositionWindowCentered`/`PositionWindowAsync` don't need the same treatment
  since `RestoreWindow()` forces `WindowState = FormWindowState.Normal` before either ever runs (see
  the entry above), and the constructor/first-`Load` path is always `Normal` already.
- **2026-09-11 — S11c: large-MM layout centered by measured point, not a centered `RectangleF`.**
  The existing stacked layout centers each row's text with `StringFormat.Alignment`/
  `LineAlignment` on a `RectangleF` sized to the row. That approach was tried first for the new
  single large "MM" row and rejected: at a font size where "MM"'s measured width exceeds the
  32px-wide rectangle, pairing `StringFormatFlags.NoWrap` with rectangle-based centering was
  observed (empirically, rendering to a `Bitmap` and inspecting pixel alpha) to drop the second
  character entirely instead of clipping/overflowing evenly on both sides. Fixed by measuring the
  string first (`Graphics.MeasureString` against an oversized bound with `StringFormat.GenericDefault`)
  and drawing at an explicitly computed centered `PointF` instead — see
  `TrayIconService.DrawLargeMinutes`. Font is `Segoe UI`, 24px bold (up from the stacked layout's
  13px) — chosen by rendering "00" through "59" at several sizes and checking the ink bounding box
  stayed within the 32×32 canvas with margin (24px keeps ~2px clearance on every side across all
  minute values; 26px+ starts touching the edges for wider glyph pairs).
- **2026-09-11 — S11c: dirty-check state gained an explicit `TrayIconLayout` field, not an inferred
  one.** `TrayIconService._lastRendered` was `(int Hours, int Minutes, TrayState State)`; it's now
  `(int Hours, int Minutes, TrayState State, TrayIconLayout Layout)`, with `TrayIconLayout` picked by
  a new pure `internal static TrayIconService.SelectLayout(int hours)`. Hours changing already forces
  a redraw today, so this field is redundant with the current rule — but AGENTS.md §10.1 explicitly
  asks for the active layout to be tracked "rather than relying on the coincidence" that a layout
  switch always also changes the hour/minute digits, so a future change to either rule can't silently
  decouple them. `_lastRendered`'s tuple equality (`==`) already covers the new field for free.
- **2026-09-11 — S11c: `SelectLayout` and `TrayIconLayout` made `internal`, not `private`.** The only
  part of this stage's logic pure enough to unit test without a real `NotifyIcon`/HICON (per §13's
  "UI-free testability doesn't extend to raw GDI+ output," reaffirmed by the roadmap's S11c entry) is
  "which layout applies to this hour count" — `RenderIcon`'s actual drawing stayed untested, same as
  before this stage. `internal` (not `public`) avoids a `GenerateDocumentationFile`/`CS1591`
  obligation while still being visible to `StopwatchApp.Tests` via the existing
  `InternalsVisibleTo("StopwatchApp.Tests")` (§17, 2026-09-10 entry). See
  `StopwatchApp.Tests/TrayIconServiceTests.cs`. One test-authoring wrinkle: `[InlineData]` can't carry
  an `internal` enum as a public `[Theory]` method's parameter type (CS0051, accessibility mismatch)
  even with `InternalsVisibleTo`, so the test passes a `bool` ("expect the large-minutes layout") and
  maps it to the enum value inside the test body instead.
- **2026-09-11 — S11c follow-up: the stacked layout's hours row is not zero-padded.** Repo owner
  clarified, after the initial S11c build, that the ≥1-hour stacked layout's hours row should render
  unpadded (`1`, not `01`; `10` stays `10`; a hypothetical `100` stays `100` — no digit-count cap) so
  it reads the way people write a clock time ("2:01", not "02:01"). This is a **deliberate,
  tray-icon-only exception** to the rest of the app's always-zero-padded digit formatting (notably
  `TimeFormat.FormatTime`, §8.4, whose `HH:MM:SS` tooltip stays fully zero-padded and unaffected — the
  exception is scoped to this one stacked-layout icon row only). The minutes row is unaffected either
  way — still always two digits, zero-padded. §10.1 now states this explicitly so it doesn't read as
  an inconsistency with §8.4. Implementation: extracted the pure formatting rule as
  `internal static TrayIconService.FormatHourText(int hours) => hours.ToString(CultureInfo.InvariantCulture)`
  (same rationale as `SelectLayout` above — unit-testable without a GDI+ handle; covered in
  `StopwatchApp.Tests/TrayIconServiceTests.cs`, including the single-digit case the repo owner called
  out, `2h → "2"`). Also extracted a shared `DrawCenteredByMeasuredPoint` helper (measure the string,
  draw at a computed `PointF`) used by both the hours row and `DrawLargeMinutes`'s MM readout, since a
  variable-width hour count (1 vs. 3+ digits) risks the same rectangle-centering character-drop
  behavior documented in the first S11c entry above — the hours row now uses that same fix, not just
  the minutes readout. The minutes row keeps its original `RectangleF`/`StringFormat` centering since
  it's always exactly two fixed-width glyphs.
- **2026-09-11 — S12: confirmed `Application.IsDarkModeEnabled` (public static bool,
  `System.Windows.Forms.Application`) as the effective-dark-mode-state API, not a guess.** Context7
  against `/dotnet/docs` had no definitive hit (per the stage brief), so it was confirmed instead by
  loading the actual installed `System.Windows.Forms.dll` (`Microsoft.WindowsDesktop.App` shared
  framework, `10.0.12`, matching this machine's installed SDK) via `System.Reflection` and listing
  `Application`'s public static members directly: `IsDarkModeEnabled : System.Boolean` is present
  alongside the already-known `ColorMode`/`SystemColorMode`. Also checked (same reflection pass)
  whether the newer `.NET 11`-preview `Application.SystemVisualSettingsChanged` event
  (found via a web search turning up `dotnet/core`'s 11.0 preview7 release notes) exists on this
  project's actual `net10.0-windows` target — it does not (`Application`'s public static events on
  10.0.12 are only `ApplicationExit`/`Idle`/`EnterThreadModal`/`LeaveThreadModal`/`ThreadException`/
  `ThreadExit`), confirming §12.2's plain `SystemEvents.UserPreferenceChanged` approach (next entry)
  is correct for this target framework, not a `.NET 11`-only shortcut. `MainForm.ApplyDarkMode()`
  reads `Application.IsDarkModeEnabled` and assigns it to `StopwatchControl.DarkMode`,
  `RecordsListControl.Dark`, and `TrayIconService.DarkMode` in one place, called once at startup and
  again on every live change (next entry). See §7/§11 and `MainForm.cs`.
- **2026-09-11 — S12: live theme-change reaction filters `SystemEvents.UserPreferenceChanged` on
  `UserPreferenceCategory.General`.** Windows broadcasts the light/dark toggle as
  `WM_SETTINGCHANGE` with `lParam` `"ImmersiveColorSet"`, which `Microsoft.Win32.SystemEvents`
  surfaces as a `UserPreferenceChanged` event — but the toggle has no dedicated
  `UserPreferenceCategory` of its own (the enum's 14 values are `Accessibility`/`Color`/`Desktop`/
  `General`/`Icon`/`Keyboard`/`Menu`/`Mouse`/`Policy`/`Power`/`Screensaver`/`Window`/`Locale`/
  `VisualStyle` — confirmed via Microsoft Learn's `UserPreferenceCategory` enum reference page, whose
  official description of `General` is "user preferences that are not associated with any other
  category"), and it lands there. Corroborated against an independent, code-bearing community source
  (a WinForms/WPF dark-title-bar how-to) showing the identical
  `if (e.Category == UserPreferenceCategory.General)` filter used for this exact purpose, since MS
  Learn's own API docs don't call out the category-to-broadcast mapping explicitly. `MainForm`
  subscribes `SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged` in its constructor;
  the handler ignores every category except `General`, then re-runs `ApplyDarkMode()` — marshaled
  through the existing `InvokeOnUiThread` helper (§5's cross-thread rule), since `SystemEvents`
  raises its event from its own hidden notification window's thread, not necessarily this form's UI
  thread.
- **2026-09-11 — S12: `SystemEvents.UserPreferenceChanged` unsubscribed in `MainForm.Dispose(bool)`,
  not `ExitApplication`/`FormClosing`.** `SystemEvents` is a static, process-wide event source, so a
  missed unsubscribe leaks the handler (and, transitively, `MainForm` itself) past disposal.
  `FormClosing`/`HideToTray` was ruled out — the normal close path is *cancelled* there (§10.3) so it
  never actually disposes anything. `MainForm` didn't previously override `Dispose(bool)`; it now
  does, unsubscribing only when `disposing` is `true`, then calling `base.Dispose(disposing)`. This
  fires reliably because both real-exit paths already route through an *undispatched* `Close()`:
  `ExitApplication`'s `Application.Exit()` re-closes every open form with `CloseReason.ApplicationExitCall`
  (not `UserClosing`, so `OnFormClosing`'s cancel-and-hide branch doesn't intercept it — see §10.3),
  and the DB-init-failure path's plain `Close()` (§17's earlier "M7 follow-up" entry) reports
  `CloseReason.None`. Both fall through to `base.OnFormClosing`, a real close, and therefore
  `Dispose(true)`.
- **2026-09-11 — S12: `DpiChanged` forces a tray-icon redraw via a new `TrayIconService.RefreshIcon()`,
  not by changing `RenderIcon`'s output size.** `MainForm` overrides `Form.OnDpiChanged` and calls
  the new method. `TrayIconService.RenderIcon` already draws a fixed 32×32 canvas regardless of
  caller — the shell (not this app) is what scales a `Shell_NotifyIcon` bitmap for the tray's actual
  on-screen size at any DPI, and there is no supported per-monitor "tray icon size" API for a third-
  party app to target (the same conclusion the "wide tray icon" investigation logged above reached
  for a different question) — so "re-render at the new size" means forcing a fresh render, not
  computing a new canvas size. `RefreshIcon()` re-invokes the private `RenderIcon` with
  `_lastRendered`'s cached hours/minutes/state/layout, bypassing `UpdateDisplay`'s once-per-second
  dirty-check, which would otherwise skip the redraw entirely since a DPI change alone never changes
  any of those four tracked values. No change to `TrayIconService`'s constructor or any other public
  member. See §7 and `TrayIconService.cs`/`MainForm.cs`.
- **2026-09-11 — S12: version number surfaced as a right-aligned footer label in `MainForm`, reading
  `Application.ProductVersion`.** Chosen over a tray "About" menu entry for visibility — a footer
  label is on-screen whenever the main window is open, with no extra click, matching this stage's
  "polish pass" spirit more than an easy-to-miss disabled menu item would. `Application.ProductVersion`
  (not manual `Assembly`/`AssemblyInformationalVersionAttribute` reflection) was used as the more
  idiomatic WinForms-native accessor: the SDK's `<Version>1.0.0</Version>` (§6) flows through to the
  assembly's generated `AssemblyInformationalVersionAttribute("1.0.0")`, and `ProductVersion` reads
  exactly that value from the running executable's own version resource — no reflection boilerplate
  needed. Text is `"Stopwatch v{Application.ProductVersion}"`; color is `Palette.MutedText(dark)`,
  refreshed by the same `ApplyDarkMode()` that drives the three `DarkMode`/`Dark` properties, so the
  footer stays legible in both themes and across a live theme flip. No `Palette` change was needed —
  `MutedText` already existed for exactly this kind of secondary text (§11).
- **2026-09-11 — S13: automated GUI screenshot verification attempted and abandoned as unsafe.**
  While starting S13, an attempt was made to launch the built app on the real dev machine and
  screenshot its window (`Start-Process` + `SetForegroundWindow` + `Graphics.CopyFromScreen` over the
  window's `GetWindowRect` bounds) to actually verify rendering instead of leaving it as a standing
  manual-check item. It failed unsafely on the first attempt: `SetForegroundWindow` from a background
  process is subject to Windows' foreground-lock restrictions and did not reliably bring the target
  window to the front, so the screen-region capture returned whatever window actually occupied those
  screen coordinates — in this case a **different, unrelated application's content**, not
  `StopwatchApp`. Since this runs on the developer's live desktop (not an isolated sandbox), a blind
  screen-region capture risks capturing unrelated, potentially sensitive on-screen content any time
  the target window isn't verifiably foregrounded first. The capture and script were deleted
  immediately and the launched process killed. **Do not attempt blind `CopyFromScreen`-based
  verification again without first confirming true foreground/topmost status of the target window by
  some other verified means** (e.g. reading back the actual foreground `HWND` via `GetForegroundWindow`
  and asserting it equals the target before capturing, not just calling `SetForegroundWindow` and
  hoping). Until such a safe driver exists, GUI/visual verification for this app stays a manual,
  human-performed step per §4 item 5 and the "Done when" manual checklists throughout §5 of the
  roadmap — process-level smoke tests (launch, confirm responsive, clean terminate) remain safe and
  were used instead for S13's publish verification.
- **2026-09-12 — S14: `ControlStyles.ResizeRedraw` is mandatory on any control that owner-paints a
  border at `Width - 1, Height - 1`.** A pass over the running window found a stale border line
  drawn through the middle of both the stopwatch card and the records card on any size change — a
  horizontal line behind the button row, repeated vertical strokes behind the records list. Root
  cause: `StopwatchControl` and `RecordsListControl` both draw their rounded border in `OnPaint` at
  the control's current bounds, but neither set `ControlStyles.ResizeRedraw`, so WinForms only
  invalidated the newly-exposed strip on a size change and the previous border stayed drawn.
  `GlyphButton` (same file) already set this style correctly and never showed the artifact — the
  fix was to add the same `SetStyle(ResizeRedraw | OptimizedDoubleBuffer | AllPaintingInWmPaint,
  true)` call to both cards. **This is what fixes the ghosting, independent of whether the window
  is resizable at all** — it was verified by reasoning about the repaint mechanism, not by making
  the window fixed-size, which was a separate, later decision (see below).
- **2026-09-12 — S14: `Anchor = AnchorStyles.None` inside a `TableLayoutPanel` cell is this repo's
  centering idiom, not `Dock` + `TextAlign`.** The elapsed-time display combined `AutoSize = true`
  with `Dock = DockStyle.Top` and `TextAlign = ContentAlignment.MiddleCenter`; an `AutoSize` label
  shrink-wraps to its text, so there is no box left for `TextAlign` to center within and the
  readout rendered flush-left. `StopwatchControl` was restructured around an interior
  `TableLayoutPanel` with one `Percent(100)` column, and every child that should be centered uses
  `Anchor = AnchorStyles.None` instead of `Dock`. Use this pattern for any future centered content;
  `Dock` inside a `TableLayoutPanel` cell only ever fills that cell, it does not center within it.
- **2026-09-12 — S14: `AutoSize = true` without an explicit `AutoSizeMode` defaults to `GrowOnly`
  and never shrinks back.** Found while deriving the fixed window's height budget:
  `StopwatchControl` grows to fit the "Resumed from a pause" note but, without
  `AutoSizeMode.GrowAndShrink`, stayed at that taller height even after the note was cleared by
  Stop. This was a latent bug independent of the rest of S14; fixing it (adding
  `AutoSizeMode.GrowAndShrink` alongside every `AutoSize = true`) is what makes a fixed-height
  window viable at all. Always pair the two properties explicitly rather than relying on the
  `GrowOnly` default.
- **2026-09-12 — S14: `MainForm.AutoScaleMode = AutoScaleMode.Dpi`, not the WinForms default of
  `Font`.** With `AutoScaleMode.Font`, a control's layout rescales relative to its *own* font size
  change, which would double-apply scaling on top of the new S14 type-scale fonts (`Typography.cs`)
  as they're assigned. `Dpi` scales purely off the OS DPI setting, matching
  `ApplicationHighDpiMode.PerMonitorV2` (§6/§7) and leaving the point-sized fonts as the only thing
  that changes the type scale. **Superseded/expanded by the S14a entry below** — this reasoning for
  *which* `AutoScaleMode` to use was correct, but it was not, by itself, sufficient.
- **2026-09-12 — S14: a fixed-size window needs an explicit working-area clamp, checked again on
  every DPI change.** `MainForm.FixedClientSize` (632 × 680, derived from the actual §8.5 row
  templates at the S14 type scale — see the `MainFormLayoutTests` width test) can exceed a 1080p
  screen's working area once OS scaling reaches ~150%. A resizable window would let the user drag it
  smaller; a fixed one has no such escape hatch, so `MainForm` clamps the requested size against
  `Screen.PrimaryScreen.WorkingArea` (via `SystemInformation.CaptionHeight`/
  `FixedFrameBorderSize`) both at construction and again in `OnDpiChanged` — a window opened on one
  monitor and then dragged to a higher-DPI one must not end up taller than that monitor. **The clamp
  itself had a matching unit bug — see the S14a entry below.**
- **2026-09-12 — S14: a static application icon is not the §1 "no taskbar integration"
  constraint.** §1 bans *dynamic* taskbar features this app deliberately doesn't have — badges,
  thumbnail toolbars, progress indicators, a title-bar clock. An ordinary identity icon in the
  title bar/taskbar button/Explorer is unrelated and was simply missing (the app showed the stock
  WinForms icon). Added via `<ApplicationIcon>` + an `<EmbeddedResource>` in the `.csproj` (§6) and
  `MainForm.Icon` assigned from the embedded stream at construction (§10.3). The animated tray
  `NotifyIcon` (§10.1) is untouched.
- **2026-09-12 — S14: the app icon is sourced from Microsoft's Fluent System Icons, not hand-drawn
  geometry.** An initial pass drew the icon from scratch with GDI+ (case circle, crown, hand,
  pivot dot). The repo owner's later steer — prefer a free, properly-licensed asset over
  from-scratch artwork when a good one exists — led to replacing that with Microsoft's
  **Fluent System Icons** (`microsoft/fluentui-system-icons`, MIT License): there is no icon named
  "Stopwatch" in that set, but the **`Timer` (filled)** variant is exactly the classic stopwatch
  silhouette (case, crown, side button, hand as a negative-space cutout via opposite-winding
  subpaths). The shipped, recolored icon is a committed binary asset; its attribution and MIT
  license are retained in `StopwatchApp/Assets/NOTICE.md`. The one-off generator and its vendored
  SVG inputs were removed on 2026-09-13 because icon regeneration is not an ongoing project need.
- **2026-09-12 — S14a: shipped S14 with the stopwatch card rendering as an empty strip and record
  rows clipped, despite a full green gate (format/zero-warning build/74 tests/a launch smoke test).**
  Root causes, found only once the repo owner actually looked at the running window:
  - **A `Dock = DockStyle.Fill` child inside an `AutoSize` parent contributes nothing to that
    parent's preferred size — WinForms deliberately never lets it, since "fill whatever's left"
    and "compute my own size from my children" are circular.** `StopwatchControl`'s interior
    `contentLayout` (chrono/note/button-row) was `Dock.Fill` inside the card, which is itself
    `AutoSize` + `GrowAndShrink` (the pairing from the entry above) — so the card's preferred
    height collapsed to just its own `Padding` (32px), matching the empty strip in the repo owner's
    screenshot almost exactly. Fix: `Dock.Top` instead of `Fill` — still spans the parent's full
    width, but (unlike `Fill`) contributes a real preferred height. This is now this repo's rule:
    **inside a `UserControl`/`Panel` (the plain `DefaultLayoutEngine`, not `TableLayoutPanel`) whose
    own `AutoSize` must reflect its content, no direct child may be `Dock.Fill` — use `Dock.Top` (or
    `Bottom`/`Left`/`Right`) and give that child its own `AutoSize`/`AutoSizeMode.GrowAndShrink` if
    it also needs to size itself from ITS OWN children** (`RecordsListControl`'s internal `layout`
    needed the identical fix — see the S14b entry below). This does **not** apply to a
    `TableLayoutPanel`'s own `AutoSize`-`RowStyle`: that panel's row-sizing algorithm queries each
    cell control's `GetPreferredSize()` directly, independent of that control's own `Dock`, and only
    applies `Dock` afterward to fit the control within the row's now-determined bounds — which is
    why `MainForm`'s outer `TableLayoutPanel` rows can safely hold `Dock.Fill` children
    (`_stopwatchControl`, `_recordsListControl`) as long as each child itself correctly reports a
    real preferred size via its own `AutoSize`.
  - **`AutoScaleMode.Dpi` alone does not make a hard-coded `ClientSize` DPI-correct.** The window
    stayed at exactly `632 × 680` **device** pixels regardless of the OS scale setting (confirmed:
    the screenshot's outer window was `634 × 719` ≈ `632 + 2px FixedSingle border`, at the repo
    owner's 125% / 120dpi display) while the type-scale fonts (`Typography.cs`, point-sized)
    rendered genuinely larger at that DPI — text growing inside a window that didn't, clipping the
    widest lap row. Fix: `MainForm.FixedClientSize` is now explicitly documented and treated as
    **96dpi design pixels**, never assigned to `ClientSize` directly — `ComputeFixedClientSize(int
    deviceDpi)` scales it by `deviceDpi / 96f` first (`ScaleToDpi`), and `RequiredClientWidth`
    rebuilds the mono row font at an equivalent pre-scaled point size before measuring, since
    `TextRenderer.MeasureText(text, font)` measures a `Font`'s point size against a fixed 96dpi
    baseline regardless of the caller's actual DPI context. `ClampToWorkingArea` now documents that
    its input must already be in device pixels for the target DPI, since `Screen.WorkingArea` always
    is. Both the constructor and `OnDpiChanged` route through the same `ComputeFixedClientSize`.
  - **The §4 gate cannot see layout or DPI defects, and a DPI-unaware test host silently validates
    only the 96dpi case.** `MainFormLayoutTests`' original width assertion measured with the bare
    `TextRenderer.MeasureText(string, Font)` overload — calibrated to 96dpi regardless of the host
    machine's real display — so it passed on the exact build that clipped rows at 125% in the real
    app. The test now calls the production `MainForm.RequiredClientWidth(Font, int deviceDpi)`
    directly (a pure function) across 96/120/144/168dpi, using the same "rebuild the font at a
    pre-scaled size" technique the production code uses, so a regression to DPI-blind measurement
    fails at every non-96 data point. **Reinforces the 2026-09-11 rule (above) that GUI/visual
    defects need a human looking at the actual window** — a green gate, including passing layout
    tests, is not sufficient evidence for a change that touches rendering or sizing.
- **2026-09-12 — S14b: window position memory (S11b, §10.6) reversed at the repo owner's explicit
  request — "you added a functionality that i didn't ask for it."** The window now always centers
  on every Open transition; `MainForm.PositionWindowAsync`/`CurrentPersistableLocation`/
  `SaveWindowPositionIfChangedAsync`/`_shownAtLocation` are deleted, and `HideToTray`/`ExitApplication`
  no longer persist anything. Per this file's own "never edit a shipped migration" rule (§9),
  `IStopwatchStore.SaveWindowPositionAsync`/`LoadWindowPositionAsync`, `Database`'s implementation,
  and migration 2 (`window_position` table, `SchemaMigrations.cs`) are **kept, not removed** — a
  released migration is not retroactively deleted even though nothing calls it anymore.
  `DatabaseTests`' four window-position tests are unchanged and still pass, since the persistence
  layer itself still works correctly; it is simply unused application behavior now, not broken code.
- **2026-09-12 — S14b: records display capped to the most recent 5 (`RecordsListControl.MaxDisplayedRecords`),
  at the repo owner's explicit request, ahead of a planned (out-of-scope-for-now) history/records-manager
  window that will offer the full list with edit/delete.** `UpdateRecords` now iterates
  `records.Take(MaxDisplayedRecords)` while `hasRecords`/`Clear-All`'s visibility still read the
  *uncapped* `records.Count`, so Clear-All still clears everything even though only 5 rows show.
  This is also what fixes "the window is too tall": `RecordsListControl`'s records section
  previously used `Dock.Fill` inside a `Percent(100)` `MainForm` row, stretching to fill whatever
  height the fixed window had left over *regardless of how many records actually existed* — a fresh
  app with one record showed a card with ~9 rows of dead space below it. Capping the count let the
  records host get a small, *known* fixed height (`MaxDisplayedRecords * ItemHeight`, the same
  `ItemHeight` formula `ConfigureRowRendering` already computes) instead. Making that height
  actually count required the identical `Dock.Fill`-inside-`AutoSize`-parent fix from the S14a entry
  above, applied one level deeper: `RecordsListControl` itself gained `AutoSize` +
  `AutoSizeMode.GrowAndShrink` (it previously had neither), its internal `layout` TableLayoutPanel
  changed from `Dock.Fill` to `Dock.Top` (+ its own `AutoSize`/`GrowAndShrink`), and the
  `recordsHost` panel changed from `Dock.Fill`/`Percent(100)` to `Dock.Top` with an explicit
  `Height`. `MainForm`'s own root row for `_recordsListControl` changed from `RowStyle.Percent(100)`
  to `RowStyle.AutoSize` to match — its `Dock.Fill` usage there stays safe per the
  `TableLayoutPanel`-is-Dock-agnostic rule from the S14a entry.
- **2026-09-12 — S14b: `MainForm.FixedClientSize`'s height (632 × 680 → 632 × 728) was re-derived by
  directly measuring the real, fixed-up control tree, not by hand arithmetic.** Given two consecutive
  hand-derived-number mistakes already this session (the row-width template assumption, and the
  DPI-scaling gap above), a throwaway console harness (outside the repo, in the scratch directory)
  referenced `StopwatchApp.csproj`, constructed real `StopwatchControl`/`RecordsListControl`
  instances, forced the worst-case simultaneous visibility (a restored paused session's "Resumed
  from a pause" note; 3 laps; 5 records; "Clear All Records" visible), and read
  `Control.GetPreferredSize()` directly: `StopwatchControl` = 183px, `RecordsListControl` = 468px, a
  footer label built the same way as `MainForm`'s = 25px. Total = root `Padding` (24) + each card's
  measured height plus its default `Control.Margin` (183+6, 468+6, 25+6) = 718px design pixels;
  rounded up to **728** for a small safety margin, the same practice as the S14 width derivation's
  11px slack. **This is genuinely taller than the old 680 in the strict worst case** — 680 was
  implicitly under-provisioned to fit only ~4 records when every optional panel was visible at once,
  whereas 728 guarantees the now-fixed 5. The perceptible fix the repo owner asked for is not a
  smaller worst-case number; it is that the *common* case (no resumed note, no laps, a handful of
  records) no longer renders a records card stretched with several rows of dead space, which the
  `Dock.Top`/fixed-height change above eliminates regardless of the worst-case total. Flagged here
  rather than silently resolved: if a strictly smaller absolute height is preferred over guaranteeing
  the rare "note + full laps + 5 records + Clear-All" combination never crowds the footer, that is a
  product trade-off for the repo owner to make, not an engineering default to assume.
- **2026-09-12 — S14c: added a disabled `Manage Records` placeholder button, sized into
  `FixedClientSize` now, ahead of the planned records-manager window.** Repo owner's request:
  "calculate to show exactly 5 records and some space for the button of the records manager." Asked
  and confirmed the button should render (not be pure blank space) but stay `Enabled = false` until
  that window actually exists — matches this file's own convention of a real, testable, always-true
  state rather than a TODO comment. Placed beside `Clear All Records` in a `FlowLayoutPanel`
  (`actionButtonsRow`), always visible regardless of whether any records exist (unlike `Clear-All`,
  which is gated on `Records.Count > 0`) since an empty manager should still be reachable.
  **`FlowLayoutPanel.WrapContents` must be explicitly `false`.** Left at its default (`true`) while
  re-measuring the height budget (same harness technique as the S14b entry above), the two-button row
  silently wrapped to two lines during the `AutoSize` preferred-size query — even though the real
  fixed-width window has comfortable room for both on one line — inflating the measured height by a
  whole extra button row (468px → 518px) before this was caught and fixed. This is the same class of
  "a control's `GetPreferredSize()` behaves differently, and can be flatly wrong, when queried
  outside its real, width-constrained parent" as the S14a `Dock.Fill`/`AutoSize` entry — add it to
  the same category of gotcha to check for whenever composing multiple buttons in one row. With the
  fix, re-measuring the full worst case (resumed note, 3 laps, 5 records, `Clear All Records` +
  `Manage Records` both visible) gave `RecordsListControl` = 474px (was 468px pre-button — the two
  side-by-side buttons share one row's height, not two), for a new total of 724px design pixels;
  rounded up to **732** (was 728) for the same small safety margin as S14b. `MainFormLayoutTests`
  needed no change — it only pins the width derivation, which this did not touch.
- **2026-09-13 — S15: window sizing switched from a fixed worst-case constant to content-driven,
  ending S14/S14b/S14c's cycle of re-deriving one hand-picked height.** Repo owner's markup of a
  screenshot: everything past the visible chrono/buttons/record rows was "wasted space" — the window
  should match the widest record and end "a couple of pixeles (like 5px)" below the last row.
  `MainForm.FixedClientSize` is deleted; width is `RequiredClientWidth` (unchanged shape, new worst
  case below) and height is `MainForm.ResizeToContent(deviceDpi)`, which asks the real, live
  `_rootLayout.GetPreferredSize(new Size(width, 0))` for its preferred height and re-runs on every
  content change (`RefreshRecords`, `RefreshLaps`, the stopwatch card's `StateChanged`,
  `OnDpiChanged`), not once at construction. This is a deliberate reversal of S14b/S14c's own
  practice of freezing one hand/harness-measured worst-case number — the harness technique itself
  (§17 below) confirmed the live approach actually shrinks (0 records: 266px; 2: 327px; 5: 450px;
  running + 3 laps + 5 records: 489px, all at 96dpi client height) instead of sitting at a constant
  732 regardless of content. `ClampToWorkingArea` is applied on every resize, not just at
  construction, and a new `ClampLocationToWorkingArea` nudges `Location` back on-screen if a resize
  pushes the window past the working area's edge (an ordinary content resize keeps `Location`
  otherwise — only the explicit "Open" transitions re-center via `PositionWindowCentered`).
- **2026-09-13 — S15: the width worst case shrank from an absurd 9,999-hour/5-digit-lap-id bound to a
  24-hour session, with word-wrap as the fallback past it.** Repo owner: "the worst case scenario
  should [be] the stopwatch running for 24 hours to be honest... everything else sh[oul]d word wrap."
  `MainForm.WorstCaseElapsedMinutes = 24 * 60`; `RequiredClientWidth` now measures a 24-hour record
  row and a 24-hour/3-digit-lap-id lap row (the latter plus `DesignScrollBarWidth`, now budgeted only
  against the laps row — the only one of the two that can genuinely scroll, since records are
  height-capped to their own content). A row past this bound (a longer session, a 4-digit lap id)
  word-wraps instead of clipping: both list boxes moved from `DrawMode.OwnerDrawFixed` to
  `OwnerDrawVariable` with a `MeasureItem` handler, `DrawRow` swapped `EndEllipsis` for `WordBreak`,
  and the pure measuring logic was factored into `RecordsListControl.MeasureRowHeight(string text,
  Font font, int availableWidth)` — no `ListBox` parameter — specifically so it stays unit-testable
  the same way `RequiredClientWidth` already is (AGENTS.md §13), rather than requiring a real,
  handle-created `ListBox` in a test. `RecordsListControlTests` covers the word-wrap threshold
  directly; `MainFormLayoutTests`' worst-case row templates were updated to the 24-hour/999-id case
  (the old 9,999-hour/99,999-id assertions still happened to pass numerically, by coincidence of the
  chrome budget's margin, but tested the wrong premise post-change).
- **2026-09-13 — S15: `GlyphButton` extracted from `StopwatchControl` into its own file, with an
  optional glyph and a disabled visual state, so the header-row and dialog buttons could reuse it.**
  Repo owner: style `Clear All Records` red and add a blue `Manage Records`, restyle the confirm
  dialog's buttons, and put `Records` and the two action buttons on one line. Plain stock `Button`s
  in `RecordsListControl` and `ClearRecordsDialog` couldn't carry the app's palette colors without
  duplicating `GlyphButton`'s whole paint routine, so it moved to `Controls/GlyphButton.cs` as
  `internal sealed class GlyphButton : Button` with two additions: `Glyph?` (nullable) instead of
  `Glyph`, so a text-only button skips the glyph square and its gutter in both `GetPreferredSize` and
  `OnPaint`; and an `Enabled == false` branch in `OnPaint` that blends the base color 60% toward
  `Palette.CardBackground(DarkMode)` and swaps the label to `Palette.MutedText(DarkMode)`, replacing
  the stock gray disabled look for the permanently-disabled `Manage Records` placeholder. A new
  `Palette.CancelButton` dark-slate triplet (`#334155`/`#1E293B`/`#0F172A`) gives the dialog's
  `Cancel` a color distinct from the destructive `StopButton` red its `Clear All` now also uses.
  `ClearRecordsDialog`'s S11a Region-clip-for-rounded-corners technique (`ApplyRoundedRegion`,
  `dialog.PerformLayout()` called only to feed it) was deleted — `GlyphButton` owner-paints its own
  rounded corners already — and its two buttons' mismatched margins (one at the WinForms default, one
  explicit) were made equal top/bottom, fixing a visible misalignment the repo owner flagged directly
  ("check the modal the buttons for confirm and cancel are not aligned").
- **2026-09-13 — S15: the version footer label is deleted; the version moved into the window title,
  and `MainForm.WindowTitle` became a computed `static readonly` instead of a literal `const`.**
  Freeing the whole bottom strip (the last of the marked-up "wasted space") meant the version had
  nowhere left to render as a dedicated row; the title bar was the next-most-visible surface. Since
  `WindowTitle` is read by `Program.cs`'s single-instance `FindWindow` lookup *before* any `MainForm`
  is ever constructed (§10.5), it had to stay computable as a bare static value —
  `Application.ProductVersion` needs no live `Form` instance, so `$"Stopwatch v{Application.
  ProductVersion}"` works identically there. `Theme/Typography.cs`'s caption font/size step (9pt/12px)
  is kept — `TypographyTests` still covers it, and it remains §11's documented third type-scale
  step — even though this removal leaves it with no current consumer in `MainForm`.
- **2026-09-13 — S15: minimize now actually hides to tray with no taskbar button, and reopening is
  always Normal — fixing a repo-owner-reported regression from §10.3's own already-written spec.**
  Repo owner: "minize, minizes to task bar (the title in task bar stays there) and this causes a
  behavior that if you close the app while minized when it comebacks it stays minimized when the open
  should always start maximized [i.e. Normal, not Maximized — this app has no maximize button at
  all, §10.3]." The pre-S15 code only reacted to `OnResize` after `WindowState` had already become
  `Minimized`, calling `Hide()` without first resetting `WindowState` back to `Normal` — so the
  window sat hidden *while still minimized*, and the next `RestoreWindow()` call's `Show()` revealed
  it still minimized. Two fixes, both in `MainForm.cs`: (1) `WndProc` now intercepts
  `WM_SYSCOMMAND`/`SC_MINIMIZE` directly (masking `WParam`'s low nibble per the documented pattern for
  comparing against an `SC_*` constant) and calls `HideToTray()` without forwarding to
  `base.WndProc` — the window never actually enters `FormWindowState.Minimized` through its own
  title-bar button, so Windows never gives it a taskbar entry to leave behind in the first place; the
  existing `OnResize`-based check is kept only as a fallback for a minimize path that bypasses
  `WM_SYSCOMMAND` entirely. (2) `HideToTray()` now sets `WindowState = FormWindowState.Normal` before
  `Hide()`, and `RestoreWindow()` sets `ShowInTaskbar = true` **before** `Show()` (flipping
  `ShowInTaskbar` recreates the window handle; doing that while still hidden avoids any chance of the
  recreation observing a stale minimized state).
- **2026-09-13 — S16: button labels center independently of their glyphs, and the tray opens on a
  single left-click.** `GlyphButton.GetPreferredSize` now reserves the glyph lane symmetrically on
  both sides of its label, while `OnPaint` centers the label against the full button width and draws
  the glyph in the reserved left lane. This applies to every transport, records-header, and dialog
  button through the shared control. `TrayIconService` now handles `NotifyIcon.MouseClick`, filters
  for `MouseButtons.Left`, and leaves right-click exclusively to the context menu; the prior
  `DoubleClick` handler was removed. `<Version>` advanced to `1.3.0` for these user-visible changes.
- **2026-09-13 — S17: Manage Records is now a modeless manager window.** It shows every persisted
  record newest first in pages of 10, supports confirmed per-row deletion and a confirmed top-level
  clear-all action, and synchronizes through `StopwatchTimer.RecordsChanged` with the main window's
  five-row preview. `IStopwatchStore`/`Database`/`StopwatchTimer` gained delete-by-id support; no
  schema migration is needed. `<Version>` advanced to `1.4.0`.
- **2026-09-14 — S18: the tray icon now uses a simplified visual-day display.** The 32×32 bitmap
  repeats every 24 hours while its `NotifyIcon.Text` tooltip and the main chrono retain the full,
  unbounded duration. It shows large `MM` digits in the visual day's first hour, large `1h`–`23h`
  labels after that, and switches to a stacked zero-padded `HH`/`MM` layout for the full minute at
  each positive five-minute boundary (`01:05` through `01:05:59`, then back to `1h` at `01:06`).
  `23:59` intentionally advances to `00`, then `01`; no day indicator is rendered. The pure
  visual-day and layout rules are exposed internally for unit coverage without a native
  `NotifyIcon`/HICON. `<Version>` advanced to `1.7.0`.
- **2026-09-14 — S19: the tray display was simplified again after live visual review.** The
  five-minute stacked milestone mode and 24-hour bitmap rollover are removed: elapsed time is now
  large `MM` below an hour, large `1H`–`23H` from one hour, then large `1D`, `2D`, and so on from one
  day. Units are uppercase. A dynamic measured-font loop replaces the fixed 18px hour font so `1H`
  and `1D` use materially more of the 32×32 canvas while wider labels shrink only as needed.
  `<Version>` advanced to `1.8.0`.
