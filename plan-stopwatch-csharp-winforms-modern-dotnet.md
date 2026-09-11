# Stopwatch — Windows 11 Tray Time Tracker

> **This file is the build roadmap, not the standing rules.** Once the project is scaffolded, the
> root `AGENTS.md` is the authoritative, always-loaded contract — behavioral spec, formatters, SQL
> schema, Windows integration rules, analyzer settings, and the "do not fix these" list all live
> there in full, condensed from this document. **If this plan and `AGENTS.md` ever disagree,
> `AGENTS.md` wins.** This plan's remaining job is §5: a staged roadmap of small, dependency-ordered
> milestones an agent can pick up one at a time (or dispatch in parallel where the graph allows) to
> build the app described in §1–§4 and verified by §6. Sections §1–§4 stay here as the detailed
> rationale and spec that both `AGENTS.md` and §5's stages cite by subsection.

## 1. Goal and scope

Build a small Windows 11 desktop application in **C# / .NET 10 / WinForms**: a stopwatch with
start/pause/resume/stop, lap splits, a persisted records list, and a system-tray presence that
shows the running elapsed time even while the main window is hidden.

This document is the complete specification. It is self-contained — build the app from this file
alone, without needing to consult any other project or source.

### Decisions already made

| Topic | Decision |
|---|---|
| Behavior | Exact — every behavior below is the contract, including the ones that look odd (§2.4) |
| Storage | **SQLite** (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\StopwatchApp\stopwatch.db` |
| Tray icon | Runtime-rendered icon showing **hours over minutes**; tooltip = full `HH:MM:SS`, plain text, no emoji or label |
| Window | **Both close and minimize hide to tray** (no taskbar button while hidden); Exit only from the tray menu. Opens **centered** by default; reopens at the last **manually dragged** position instead, once one exists (§3.6, added S11b) |
| Taskbar | **None** — no overlay badge, no thumbnail toolbar buttons, no progress bar, no title-bar clock |
| Features in scope | Window-scoped keyboard shortcuts, single-instance enforcement, close-to-tray, tray context menu |
| Features out of scope | Global/system-wide hotkeys, run-at-login/startup registration, crash recovery of a running (unpaused) session, idle detection, CSV/JSON export, cloud sync, telemetry |
| Packaging | **Framework-dependent** `dotnet publish -c Release` (small output, requires the .NET 10 Desktop Runtime on the machine) |

Pausing is the only action that persists a live session. A session that is running and never
paused before the app closes is not recoverable — this is intentional, not a gap to fill.

Target OS: **Windows 11 only.** Do not add compatibility shims for Windows 10 or earlier.

---

## 2. Behavioral specification

### 2.1 State model

All timer state lives in one control; nothing is computed reactively — every field is written
imperatively by the transition methods below.

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

Data shapes:

```csharp
public sealed record StopwatchRecord(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
public sealed record Lap(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
```

### 2.2 Timing loop

Use a `System.Windows.Forms.Timer` with **interval 1000 ms**, enabled only while `IsRunning`. Each
tick:

```
ElapsedMs = NowUnixMs() - StartTime;          // absolute delta, no accumulation
totalSeconds = ElapsedMs / 1000;
if (totalSeconds > 0 && totalSeconds % 5 == 0) OnTick(ElapsedMs);
```

Use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` as the clock source — **not**
`System.Diagnostics.Stopwatch`. The wall-clock anchor is the specified design: a late or delayed
tick self-corrects on the next tick instead of accumulating drift, and elapsed time is defined
against real time, not a monotonic counter.

Dispose the timer on form close. No ticks may fire after teardown.

### 2.3 State transitions

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

### 2.4 Specified behaviors that look like bugs — do not "fix" these

These are the contract, not oversights:

- `ElapsedMs` is written only by the 1-second tick, so stopping at 1.9 s stores 1.0 s. All
  durations are truncated to whole seconds by construction.
- Stopping before the first tick elapses (under 1000 ms) leaves `ElapsedMs == 0`, which means
  **no record is saved and `OnStop` never fires**.
- Laps exist only in memory and in a paused-session snapshot; a session that runs to Stop without
  ever being paused first has no recovery point if the app is killed mid-run.
- There is exactly **one** saved-session slot. Pausing a second time overwrites the first snapshot.
- Elapsed time is defined against the system clock (`DateTimeOffset.UtcNow`), so a manual clock
  change or a DST transition during a run shifts the reported elapsed time.
- The "every 5 seconds" tick filter (`totalSeconds % 5 == 0`) can skip a bucket if a tick is
  delayed past a 5-second boundary; this is acceptable and must not be "corrected" with a
  reference-counting workaround.

### 2.5 Formatters

Four pure functions. All use `CultureInfo.InvariantCulture` explicitly.

| Function | Signature | Output | Notes |
|---|---|---|---|
| `FormatTime` | `(long ms) → string` | `HH:mm:ss` | Hours are **not** clamped — 100 hours renders as `100:00:00`. Build the string manually with `string.Create` or interpolation and `.ToString("D2", CultureInfo.InvariantCulture)`; **do not** use `TimeSpan.ToString(@"hh\:mm\:ss")`, which wraps at 24 hours. |
| `FormatDate` | `(long unixMs) → string` | `yyyy-MM-dd` | **Local** time (convert from the stored epoch-ms UTC value to local before formatting). |
| `FormatTimeOnly` | `(long unixMs) → string` | `HH:mm:ss` | **Local** time, 24-hour clock. |
| `FormatElapsed` | `(long minutes) → string` | `hh:mm` | **Hard 1-minute floor**: an input of `0` still renders `"00:01"`. This is a display-only floor — the *stored* value may legitimately be `0`. |

Worked examples for `FormatElapsed`: `0 → "00:01"`, `1 → "00:01"`, `60 → "01:00"`, `90 → "01:30"`.

Reference implementation:

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

### 2.6 UI inventory

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
  `Are you sure you want to clear all records? This action cannot be undone.`, with buttons
  `Cancel` and `Clear All`. Confirming clears the records table and reloads the (now empty) list.
- A note reading `Resumed from a pause on {date} at {time}` (using `FormatDate` /
  `FormatTimeOnly` on `RestoredPausedAtMs`) appears under the timer only after restoring a saved
  session. It persists through Continue and is cleared by Stop or a fresh Start.

Row text templates, literal (including the emoji):

```
Lap:    📅 {date} ⏱ {start}-{end} ⏳ Lap {id}: {formatElapsed}
Record: 📅 {date} ⏱ {start}-{end} ⏳ Duration: {formatElapsed}
```

Where `{date}` = `FormatDate(startTimestamp)`, `{start}`/`{end}` = `FormatTimeOnly(...)`, and
`{formatElapsed}` = `FormatElapsed(elapsedMinutes)`.

### 2.7 Callbacks

Expose four events with no-op defaults so the control can be used standalone:

| Event | Fires when | Payload |
|---|---|---|
| `OnStart` | End of `Start()`, when it actually transitions to running | `ElapsedMs` |
| `OnPause` | End of `Pause()`, only if it was running, after the session snapshot is saved | `ElapsedMs` |
| `OnTick` | Inside the 1-second tick, only at 5 s, 10 s, 15 s… of elapsed time, never while paused or stopped | `ElapsedMs` |
| `OnStop` | In `Stop()`, before the database write, only when `ElapsedMs > 0 && SessionStartMs > 0` | `ElapsedMs`, `SessionStartMs`, `EndTimestamp` |

### 2.8 Theme

The app follows the OS light/dark setting. In `Program.Main`, call:

```csharp
Application.SetColorMode(SystemColorMode.System);
```

This is a stable API in .NET 10 (it required an experimental-feature opt-in, diagnostic `WFO5001`,
in .NET 9 — that restriction no longer applies). It themes window chrome and stock controls, but
not custom-painted surfaces, so define an explicit color table for anything drawn manually:

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

## 3. Windows integration

### 3.1 Tray icon rendering

Render a 32×32 icon with GDI+ and convert it to an `HICON`. **Two layouts, chosen by elapsed hours**
(added S11c, 2026-09-11 — see `AGENTS.md` §17 for why: a two-separate-`NotifyIcon`-instances "wide"
layout was considered and rejected, since Windows gives no guarantee the two stay adjacent and lets a
user hide either one independently via its own tray-icon settings, which would silently break it):

- **Elapsed hours == 0** (the common case for a stopwatch): a single large two-digit **MM** readout,
  centered and sized to fill as much of the 32×32 canvas as legibility at the 16 px scaled-down
  display size allows — noticeably bigger than the stacked layout below, since only one row of digits
  needs the space.
- **Elapsed hours ≥ 1**: the original two stacked rows of two digits — hours on top, minutes below —
  so both values keep fitting once neither is optional.

Update the icon at most once per second, and only when the displayed value **or the active layout**
actually changes (don't regenerate the bitmap every tick if neither changed) — the layout switch
itself always coincides with a minute-digit change (`:59` → `:00` at the hour boundary), but track
both explicitly rather than relying on that coincidence.

**Call `DestroyIcon` on the previous handle every time you replace it.** Forgetting this is the
single most common bug in this pattern and leaks GDI handles until the process is killed.

The tooltip text is `FormatTime(ElapsedMs)` — the full `HH:MM:SS` value — plain text, no prefix, no
emoji, regardless of which icon layout is active. Idle, paused, and running states may differ in
icon tint but never in which of the two layouts above is showing — that choice depends only on
elapsed hours, not on run state.

### 3.2 Tray context menu

Order: `Open`, separator, the state-appropriate action(s) from §2.6 (`Start`/`Pause`/`Continue`/
`Lap`/`Stop`), separator, `Exit`. Double-clicking the tray icon opens (restores and activates) the
main window. `Exit` is the only way to quit the application.

### 3.3 Window behavior

Intercept `FormClosing` when `e.CloseReason == CloseReason.UserClosing`: cancel the close, hide the
form, and set `ShowInTaskbar = false`. Do the same on minimize (`WndProc` intercepting
`WM_SYSCOMMAND` with `SC_MINIMIZE`, or handling `Resize` when `WindowState == FormWindowState.Minimized`)
— so both the close button and the minimize button hide the window to the tray with no taskbar
button remaining. Opening from the tray restores and re-activates the window.

`Application.Exit()` is called only from the tray menu's `Exit` item, and only after disposing the
`NotifyIcon` — otherwise a ghost icon lingers in the tray until the user hovers over its former
location.

### 3.4 Keyboard shortcuts

**Global (system-wide) hotkeys are out of scope** — see `AGENTS.md` §17, dated 2026-09-11, for why
the originally-planned `RegisterHotKey` design was dropped (it binds to one `HWND`, and hiding to
the tray recreates the form's window handle, silently breaking the hotkeys at the one moment they'd
matter). Instead, three ordinary, window-scoped keystrokes, active only while the main window has
focus:

| Key | Action |
|---|---|
| `Space` | Start / Pause / Continue — whatever the primary button currently does |
| `Shift+Space` | Lap |
| `Enter` | Stop |

`StopwatchControl.MapShortcut(Keys)` is the pure key table; `MainForm.ProcessCmdKey` calls it and
dispatches to the same action methods (`StartTimer`/`PauseTimerAsync`/`AddLap`/`StopTimerAsync`) the
tray menu already uses.

### 3.5 Single instance

Use a named `System.Threading.Mutex` created at startup. If a second instance detects the mutex
already exists, it sends a registered window message (via `RegisterWindowMessage` +
`PostMessage(HWND_BROADCAST, ...)`) asking the first instance to restore and activate its window,
then exits immediately.

### 3.6 Window position memory

Added post-hoc (§5 S11b) at the user's request, dated 2026-09-11. The window centers itself by
default every time it opens, but remembers the last position the user dragged it to and reopens
there instead — until it's dragged again.

```
Open (first launch, tray Open/double-click, single-instance activation, or restore-from-minimize):
  if a saved WindowPosition exists AND its bounds intersect at least one current screen's working area:
    Location = (SavedX, SavedY)
  else:
    Location = centered on Screen.PrimaryScreen.WorkingArea
    // also the fallback when a saved position no longer fits any connected monitor
  _shownAtLocation = Location   // baseline for detecting a user-initiated move, see below

Hide-to-tray (close or minimize) or Exit:
  if Location != _shownAtLocation:              // the user dragged it since it was last shown
    persist WindowPosition { X: Location.X, Y: Location.Y }
  // unchanged → no write; an app that's never been dragged stays centered forever
```

Resizing does **not** count as a move — only `Location`, never `Size`, is tracked or persisted.
`_shownAtLocation` is plain in-memory `MainForm` state, reset every time the window is positioned by
the block above, so repeated hide/show/drag cycles within one session are each compared against the
position that cycle actually started from, not a stale one from app launch. A saved position is
never trusted blindly — display configurations change (a laptop undocked, a monitor unplugged), so a
saved position whose bounds don't intersect any currently-connected screen falls back to centering
rather than placing the window somewhere unreachable.

---

## 4. Data layer

SQLite via `Microsoft.Data.Sqlite`, database file at `%LOCALAPPDATA%\StopwatchApp\stopwatch.db`,
created on first run if it doesn't exist. Rows are mapped via **Dapper** — by column name, against
the model records' own positional constructors — not hand-written ordinal `reader.Get*` calls.
Dapper sits on top of `Microsoft.Data.Sqlite`'s `SqliteConnection`, not in place of it: no design-time
tool, no generated code. Parameters use `@name`, not the `$name` form the original S3 implementation
used (Dapper doesn't recognize `$`).

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

This DDL is migration 1 in `SchemaMigrations.cs` (S3a), applied and stamped via `PRAGMA user_version`
rather than run unconditionally on every startup — see `AGENTS.md` §9 "Schema versioning" for the
full rule set (append-only migrations, why v1 alone keeps `IF NOT EXISTS`, the `user_version`
interpolation caveat).

S11b (§3.6) adds a second table the same way, as migration 2 — the first real exercise of the
append-only migration path S3a built:

```sql
CREATE TABLE window_position (
  id INTEGER PRIMARY KEY CHECK (id = 1),  -- single slot, same pattern as paused_session
  x  INTEGER NOT NULL,
  y  INTEGER NOT NULL
);
```

API surface — deliberately minimal. **No update, no delete-by-id, no range query:**

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
the UI. This is deliberate — comment it in the code so it doesn't read as an oversight.

Restore runs once at startup, after the database connection opens: if a paused session exists, the
UI shows the frozen elapsed time, a `Continue` button, the restored laps, and the "resumed from a
pause" note (§2.6). **Time spent away while the app was closed is never counted** — resuming
re-anchors `StartTime` from the current clock, exactly as an in-app pause/resume does.
`StopwatchTimer.RestoreAsync` (S4) is what runs this: it loads `Records` first (nothing else in its
fixed interface does), then checks for a saved paused session and restores it if present.

---

## 5. Staged build roadmap

### Progress ledger

Kept current as each stage lands (updated per-stage, not once at the end), so a fresh agent can
resume mid-milestone without re-deriving status from the repo. `AGENTS.md` §17 is the design-decision
log for anything discovered or decided while executing a stage — check it alongside this ledger.

| Stage | Status | Completed | Notes |
|---|---|---|---|
| S0 Scaffold | ✅ Done | 2026-09-09 | + `.slnx`/x64 follow-up (see `AGENTS.md` §17) |
| S1 TimeFormat | ✅ Done | 2026-09-10 | |
| S2 Palette | ✅ Done | 2026-09-10 | |
| S3 Database | ✅ Done | 2026-09-10 | + `SqliteConnection.ClearPool` fix (see `AGENTS.md` §17) |
| S3a Dapper + schema versioning | ✅ Done | 2026-09-10 | + `InitializeAsync` double-call fix (see `AGENTS.md` §17) |
| S4 StopwatchTimer | ✅ Done | 2026-09-10 | |
| S5 StopwatchControl | ✅ Done | 2026-09-10 | |
| S6 RecordsListControl + ClearRecordsDialog | ✅ Done | 2026-09-10 | |
| S7 MainForm | ✅ Done | 2026-09-10 | |
| S8 TrayIconService | ✅ Done | 2026-09-10 | |
| S9 Window-to-tray | ✅ Done | 2026-09-10 | |
| S10 Keyboard shortcuts | ✅ Done | 2026-09-11 | Global hotkeys dropped for window-scoped shortcuts (see `AGENTS.md` §17) |
| S11 Single-instance | ✅ Done | 2026-09-11 | `FindWindow`-targeted `PostMessage`, not `HWND_BROADCAST` (see `AGENTS.md` §17) |
| S11a Visual design refresh | ✅ Done | 2026-09-11 | Owner-drawn rounded buttons/rows + card borders as the GDI+ elevation stand-in; `TrayIconService` needed no edit (see `AGENTS.md` §17) |
| S11b Window position memory | ✅ Done | 2026-09-11 | `window_position` migration 2 + `_shownAtLocation` baseline in `MainForm` (see `AGENTS.md` §17, dated 2026-09-11) |
| S11c Tray icon: large minutes under an hour | ✅ Done | 2026-09-11 | Large-MM layout drawn by measured point, not centered RectangleF; `TrayIconLayout` tracked explicitly in the dirty-check tuple; follow-up: stacked layout's hours row is not zero-padded (`2`, not `02`) — a tray-icon-only exception (see `AGENTS.md` §17, dated 2026-09-11) |
| S12 Theme/DPI/version polish | ✅ Done | 2026-09-11 | `Application.IsDarkModeEnabled` + `SystemEvents.UserPreferenceChanged` (`UserPreferenceCategory.General`) wired in `MainForm`; `DpiChanged` forces `TrayIconService.RefreshIcon()`; version footer label reads `Application.ProductVersion` (see `AGENTS.md` §17, dated 2026-09-11) |
| S13 Publish | 🟡 Published, manual re-verification pending | 2026-09-11 | `dotnet publish -c Release -r win-x64 --self-contained false` succeeded; output at `StopwatchApp/bin/x64/Release/net10.0-windows/win-x64/publish/` launches and stays responsive (process-level smoke test only — no automated GUI driver was safe/reliable in this environment, see `AGENTS.md` §17, dated 2026-09-11). The full §6 re-verification this stage's done-when requires is still outstanding — needs a human pass |

Project layout (unchanged by staging — every stage below adds to this tree):

```
.editorconfig              style + naming rules
.gitignore                 bin/, obj/, publish/, *.db
StopwatchApp/
  StopwatchApp.csproj      net10.0-windows, WinForms, nullable, analyzers-as-errors, x64
  Program.cs               single-instance mutex, SetColorMode, Application.Run
  MainForm.cs              thin orchestrator — wires controls and services, no business logic
  Controls/
    StopwatchControl.cs    timer state (§2.1-2.4) + control row (§2.6)
    RecordsListControl.cs  records and laps list rendering
    ClearRecordsDialog.cs  confirm dialog
  Services/
    StopwatchTimer.cs      tick loop + transitions (§2.2-2.3), UI-free and unit-testable
    TrayIconService.cs     NotifyIcon, rendered icon (§3.1), context menu (§3.2)
    Database.cs            SQLite access via Dapper (§4)
    SchemaMigrations.cs    versioned schema steps, PRAGMA user_version (§4)
  Formatting/
    TimeFormat.cs           the four formatters (§2.5)
  Theme/
    Palette.cs              light/dark color tables (§2.8)
StopwatchApp.Tests/        xUnit — formatters, transitions, database round-trip
```

Each stage below is sized to be **one agent session**: a self-contained unit of work with a fixed
public interface, an explicit dependency list, and a done-condition. Every stage ends with the
`AGENTS.md` §4 post-change checklist — `csharpier format .` → `csharpier check .` →
`dotnet build -c Release` (zero warnings) → `dotnet test` (all green) — before it counts as done.
Stages with no dependency edge between them may be dispatched to parallel agents; run them
sequentially if only one agent is available.

### Dependency graph

```
S0 scaffold
 ├── S1 TimeFormat ────┐
 ├── S2 Palette ───────┤
 └── S3 Database ──┐   │
                   ▼   │
              S3a Dapper + schema versioning
                   │   │
                   ▼   │
              S4 StopwatchTimer
                   │   │
        ┌──────────┴───┴──────────┐
        ▼                         ▼
  S5 StopwatchControl     S6 RecordsList + Dialog
        └──────────┬──────────────┘
                   ▼
              S7 MainForm
        ┌──────────┼──────────┬──────────┐
        ▼          ▼          ▼          ▼
   S8 Tray    S10 Shortcuts S11 Single-instance
        ▼
   S9 Window-to-tray
        └──────────┴──────────┴──────────┘
                   ▼
        S11a Visual design refresh
                   ▼
        S11b Window position memory
                   ▼
     S11c Tray icon: large minutes under an hour
                   ▼
            S12 Theme/DPI/version polish
                   ▼
              S13 Publish
```

Parallel waves: **{S1, S2, S3}** after S0 · **{S5, S6}** after S4 · **{S8, S10, S11}** after S7.
S3a sits on the critical path between S3 and S4, and S11a/S11b/S11c between S11 and S12, the same
way — a lettered stage inserted where a design/infra choice (or, for S11b/S11c, a user-requested
scope addition) turned out to need its own pass; neither is
part of a parallel wave.

### Integration seams (fixed here so parallel stages don't diverge)

- **`IStopwatchStore`** — defined in S3, implemented by `Database`. S4 depends only on the
  interface, so the state machine is unit-testable without a temp database.
- **Async transition naming** — `Pause()` persists a snapshot and `Stop()` awaits a save, so the
  public surface is `Start()`, `PauseAsync()`, `Lap()`, `StopAsync()`. This keeps S5/S7 from wiring
  `void` handlers that would later need `.Wait()` (forbidden by `AGENTS.md` §5).
- **Clock and tick separation** — `StopwatchTimer` takes a `TimeProvider` in its constructor and
  reads `_time.GetUtcNow().ToUnixTimeMilliseconds()` (identical to `DateTimeOffset.UtcNow` under
  `TimeProvider.System`, so §2.2's wall-clock rule holds). The tick body is a public `Tick()`
  method; the actual `System.Windows.Forms.Timer` lives in `StopwatchControl` (S5) and calls
  `Tick()` once a second. Tests advance a `FakeTimeProvider` and call `Tick()` directly — this is
  what makes `AGENTS.md` §13's "never `Thread.Sleep` in a test" rule achievable.
- **Records ownership** — §2.3's duplicate guard reads `Records[0].StartTimestamp`, so
  `StopwatchTimer` owns the records list and its reload (both through `IStopwatchStore`) and raises
  a change event; `RecordsListControl` (S6) only renders from that event. `MainForm` stays a thin
  orchestrator per `AGENTS.md` §3.

### S0 — Scaffold and build gate ✅ Done

- **Depends on:** nothing.
- **Files:** `StopwatchApp.slnx`, `StopwatchApp/StopwatchApp.csproj`,
  `StopwatchApp.Tests/StopwatchApp.Tests.csproj`, `.editorconfig`, `.gitignore`,
  `StopwatchApp/Program.cs` (minimal — `ApplicationConfiguration.Initialize()` +
  `Application.Run(new MainForm())`), `StopwatchApp/MainForm.cs` (empty form, compiles).
- **Build:** both `.csproj` files per `AGENTS.md` §6 (analyzers-as-errors, `PerMonitorV2`,
  `net10.0-windows`, x64); `.editorconfig` per `AGENTS.md` §5; `.gitignore` covers
  `bin/`, `obj/`, `publish/`, `*.db`.
- **Public interface:** none yet — this stage produces a compiling, empty shell.
- **Done when:** `dotnet build -c Release` succeeds with zero warnings on both projects, and
  `dotnet test` runs (zero tests, exit 0).
- **Owns acceptance criteria:** none directly — this is the foundation every other stage builds on.
- **Out of scope:** any actual feature code.

### S1 — `TimeFormat` ✅ Done

- **Depends on:** S0.
- **Files:** `StopwatchApp/Formatting/TimeFormat.cs`, `StopwatchApp.Tests/TimeFormatTests.cs`.
- **Build:** the four formatters exactly as specified in §2.5 (reference implementation given
  there and reproduced in `AGENTS.md` §8.4) — `FormatTime`, `FormatDate`, `FormatTimeOnly`,
  `FormatElapsed`, all with explicit `CultureInfo.InvariantCulture`.
- **Public interface:**
  ```csharp
  public static class TimeFormat
  {
      public static string FormatTime(long ms);
      public static string FormatDate(long unixMs);
      public static string FormatTimeOnly(long unixMs);
      public static string FormatElapsed(long minutes);
  }
  ```
- **Done when:** unit tests cover the worked examples in §2.5 (`0/1/60/90 → "00:01"/"00:01"/"01:00"/"01:30"`),
  the 24-hour-wrap case (100 hours → `"100:00:00"`, proving `TimeSpan.ToString` was not used), and
  local-time conversion for `FormatDate`/`FormatTimeOnly`.
- **Owns acceptance criteria:** "A session under one minute stores `elapsedMinutes == 0` but still
  displays `00:01`" (the display half — storage half is owned by S3/S4).
- **Out of scope:** anything touching `Records` or `Laps` — pure functions only.

### S2 — `Palette` ✅ Done

- **Depends on:** S0.
- **Files:** `StopwatchApp/Theme/Palette.cs`.
- **Build:** light/dark color tables exactly as in §2.8 (text, muted text, card/row background,
  border, accent, empty-state text) plus the four button color triplets (base/hover/pressed).
- **Public interface:**
  ```csharp
  public static class Palette
  {
      public static Color Text(bool dark);
      public static Color MutedText(bool dark);
      public static Color CardBackground(bool dark);
      public static Color RowBackground(bool dark);
      public static Color Border(bool dark);
      public static Color Accent(bool dark);
      public static Color EmptyStateText(bool dark);
      public static (Color Base, Color Hover, Color Pressed) StartButton { get; }
      public static (Color Base, Color Hover, Color Pressed) PauseButton { get; }
      public static (Color Base, Color Hover, Color Pressed) LapButton { get; }
      public static (Color Base, Color Hover, Color Pressed) StopButton { get; }
  }
  ```
  (Exact shape is an agent judgment call — a `bool dark` parameter or a `SystemColorMode`-driven
  singleton are both fine; what must not change is that every hex value in §2.8 is reproduced
  exactly.)
- **Done when:** builds clean; no consumer yet (S5/S6/S12 wire it in later).
- **Owns acceptance criteria:** none directly (theme has no acceptance-criteria bullet in §6 — it's
  verified by the manual tray/UI check).
- **Out of scope:** applying the palette to any control — that happens in S5/S6/S12.

### S3 — Models + `Database` ✅ Done

- **Depends on:** S0.
- **Files:** `StopwatchApp/Models.cs` (or one file per record — agent's call) for
  `StopwatchRecord`, `Lap`, `PausedSession`; `StopwatchApp/Services/IStopwatchStore.cs`;
  `StopwatchApp/Services/Database.cs`; `StopwatchApp.Tests/DatabaseTests.cs`.
- **Build:** the two `CREATE TABLE` statements from §4 verbatim; the six-method API surface;
  every method wrapped in try/catch that **swallows errors** (§4's explicit, commented design —
  keep the comment). Tests run against a temp file-backed database created and deleted per test
  class, never `%LOCALAPPDATA%`.
- **Public interface:**
  ```csharp
  public sealed record StopwatchRecord(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
  public sealed record Lap(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
  public sealed record PausedSession(
      long ElapsedTime, long SessionStartTime, IReadOnlyList<Lap> Laps,
      long LastLapElapsed, long LastLapTimestamp, long PausedAt);

  public interface IStopwatchStore
  {
      Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs);
      Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync();
      Task ClearAllRecordsAsync();
      Task SavePausedSessionAsync(PausedSession session);
      Task<PausedSession?> LoadPausedSessionAsync();
      Task ClearPausedSessionAsync();
  }

  public sealed class Database : IStopwatchStore, IAsyncDisposable { /* ... */ }
  ```
- **Done when:** round-trip tests cover: empty table → empty list; insert 0,1,2 → read back
  2,1,0; a corrupt/missing paused-session row returns `null` with no exception; `ClearAllRecordsAsync`
  empties the table.
- **Owns acceptance criteria:** "`GetAllRecordsAsync()` on an empty table returns an empty list;
  records saved in order 0, 1, 2 read back as 2, 1, 0"; "A corrupt or missing saved-session row
  returns `null` ... with no exception thrown."
- **Out of scope:** any caller — `StopwatchTimer` (S4) is the only consumer, wired next.

### S3a — Dapper + schema versioning ✅ Done

- **Depends on:** S3.
- **Files:** `StopwatchApp/Services/SchemaMigrations.cs` (new); `StopwatchApp/Services/Database.cs`
  (converted to Dapper); `StopwatchApp/AssemblyInfo.cs` (new — `InternalsVisibleTo`);
  `StopwatchApp/StopwatchApp.csproj` (+`Dapper` package reference);
  `StopwatchApp.Tests/DatabaseTests.cs` (+4 tests).
- **Why:** `Database.cs` mapped rows by ordinal (`reader.GetInt64(0..3)`) and ran its two
  `CREATE TABLE IF NOT EXISTS` statements unconditionally, with no schema-evolution path. An
  ORM (EF Core) was evaluated and rejected at this scale — 2 tables, 6 fixed queries, no
  joins/relationships/transactions, `IStopwatchStore` is fixed architecture (§3.1's component
  contracts) — and its generated migrations would fail this repo's `CS1591`/`TreatWarningsAsErrors`/
  `csharpier check .` gates. Adopted Dapper (name-based mapping, no design-time tooling) plus a
  hand-written `PRAGMA user_version` migration runner instead. See `AGENTS.md` §17, dated 2026-09-10,
  for the full record.
- **Build:** `SchemaMigrations.All` — an ordered, append-only `IReadOnlyList<Migration>`; migration 1
  is §4's DDL verbatim (keeps `IF NOT EXISTS` so a pre-existing, unstamped database adopts it as its
  v1 baseline without data loss). `Database.InitializeAsync` reads `PRAGMA user_version`, applies
  every not-yet-applied migration in order inside its own transaction, then stamps the version — and
  is idempotent: a second call does not reopen the connection. The six `IStopwatchStore` methods move
  to Dapper's `ExecuteAsync`/`QueryAsync`/`QuerySingleOrDefaultAsync`, mapping by column name against
  the existing model records; `PausedSession.Laps` still round-trips through `lapsJson` via a private
  `PausedSessionRow` (not part of the public model). SQL parameter placeholders move from `$name` to
  `@name` (Dapper doesn't recognize `$`). Every method keeps its try/catch swallow and the class-scoped
  `CA1031` suppression, unchanged (§4's explicit, commented design).
- **Public interface:** unchanged — `IStopwatchStore` and the three model records are exactly as S3
  left them. `Database`'s public members (`Database(string)`, `DefaultDatabasePath`, `InitializeAsync`,
  `DisposeAsync`) are also unchanged; `SchemaMigrations`/`Migration`/`PausedSessionRow` are `internal`
  or `private`.
- **Done when:** all 9 of S3's original round-trip tests pass unmodified, plus: a fresh database
  stamps `SchemaMigrations.Current`; a database built the pre-versioning way (tables present,
  `user_version` left at 0, one row inserted) opens through `Database` with the row intact and ends
  up stamped current; calling `InitializeAsync()` twice is a no-op the second time; a saved record's
  four columns each round-trip to the correct member (not just two, so a column transposition would
  fail this test even though it passed S3's narrower assertions).
- **Owns acceptance criteria:** "A database created by an earlier build (tables present, no
  `user_version` stamp) opens without data loss and ends up stamped at the current schema version."
- **Out of scope:** any new table or column — none is planned through S13; `IStopwatchStore`'s
  surface; any caller — still unconsumed outside the test project, exactly as S3 left it.

### S4 — `StopwatchTimer` ✅ Done

- **Depends on:** S3a (`IStopwatchStore` — unchanged by S3a, but S3a is the version of `Database.cs`
  to build against).
- **Files:** `StopwatchApp/Services/StopwatchTimer.cs`, `StopwatchApp.Tests/StopwatchTimerTests.cs`,
  `StopwatchApp.Tests/FakeStopwatchStore.cs` (new — an in-memory `IStopwatchStore` so the state
  machine is tested without a temp SQLite database); `StopwatchApp.Tests/StopwatchApp.Tests.csproj`
  (+`Microsoft.Extensions.TimeProvider.Testing` 10.10.0, for `FakeTimeProvider`).
- **Build:** the full state model (§2.1) and transitions (§2.3) exactly as specified, including
  the intentional behaviors in §2.4 — reproduce the pseudocode logic verbatim, do not
  "improve" it. Constructor takes `IStopwatchStore` and `TimeProvider`. Exposes `OnStart`,
  `OnPause`, `OnTick`, `OnStop` events with no-op defaults (§2.7), plus a `RecordsChanged` event
  per the "records ownership" integration seam above. `Tick()` no-ops while not running (keeps
  `ElapsedMs` frozen while paused and satisfies §2.7's "`OnTick` never fires while paused or
  stopped"). `RestoreAsync` loads `Records` before checking for a saved paused session — see
  `AGENTS.md` §17, dated 2026-09-10, for the full rationale on both.
- **Public interface:**
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
      public void Tick();                 // called once/second by the UI-side Timer (S5)
      public Task RestoreAsync();         // startup: loads a saved paused session, if any

      public event Action<long>? OnStart;
      public event Action<long>? OnPause;
      public event Action<long>? OnTick;
      public event Action<long, long, long>? OnStop;
      public event Action? RecordsChanged;
  }
  ```
- **Done when:** transition tests cover every §2.3/§2.4 case using `FakeTimeProvider` (never
  `Thread.Sleep`/`Task.Delay`) — no reset button (Start after Stop clears laps), pause/resume
  preserves laps, lap is a split not a cumulative total, stop appends a final partial lap exactly
  once, double-stop yields one record, the 5-second `OnTick` filter, the sub-1000ms
  no-record/no-`OnStop` case.
- **Owns acceptance criteria:** "Initial display is exactly `00:00:00`"; "Lap button exists only
  while running" (state half); "Consecutive laps are splits, not cumulative"; "Laps clear when a
  fresh session starts after Stop"; "Laps and Continue survive an app restart after Pause"; "Stop
  after a lap adds a final partial lap; a second Stop never appends another"; "Pause → Stop →
  restart shows Start, not Continue"; "Stopping twice yields exactly one record"; "A session under
  one minute stores `elapsedMinutes == 0`" (storage half).
- **Out of scope:** all UI — this class must compile and be fully tested with zero WinForms
  references.

### S5 — `StopwatchControl` ✅ Done

- **Depends on:** S1 (`TimeFormat`), S2 (`Palette`), S4 (`StopwatchTimer`).
- **Files:** `StopwatchApp/Controls/StopwatchControl.cs` (+ designer file if using the WinForms
  designer).
- **Build:** the control row per state (§2.6 — idle/running/paused button sets, no button ever
  disabled), the monospace elapsed-time display, the "resumed from a pause" note, and the
  `System.Windows.Forms.Timer` (1000 ms, enabled only while `IsRunning`) that calls
  `StopwatchTimer.Tick()`. Owns a `StopwatchTimer` instance internally; exposes it or proxies its
  events — agent's call, but `MainForm` (S7) must not need to reach into `StopwatchTimer` state
  directly for anything the control already displays. Built as pure code (no `.Designer.cs` split —
  no interactive WinForms Designer available in an agent environment). Button `Click` handlers call
  `UpdateDisplay()` explicitly after their own (non-`ConfigureAwait(false)`) `await`, rather than
  subscribing it to `StopwatchTimer`'s events, since those events fire from inside the timer's own
  `ConfigureAwait(false)` continuations and could otherwise touch controls off the UI thread — see
  `AGENTS.md` §5/§17, dated 2026-09-10. The monospace font is resolved at runtime (`Cascadia Mono`
  if installed, else `Consolas`) rather than hardcoded. `StopwatchControl.DarkMode` (default
  `false`) exists so S12 can wire real OS dark-mode detection later; `Palette` is already applied
  using it.
- **Public interface:** a `UserControl` subclass; exact member shape is this stage's judgment call,
  but it must expose enough for S7 to read `Laps`/`Records` for `RecordsListControl` and to know
  when a paused session was restored (to show/hide the note). Settled shape: constructor
  `StopwatchControl(IStopwatchStore store, TimeProvider time)`; `public StopwatchTimer Timer { get; }`
  (S7 reads `Timer.Laps`/`Timer.Records`/`Timer.RestoredPausedAtMs` directly rather than the control
  proxying each one); `public bool DarkMode { get; set; }`; `public Task RestoreAsync()` (delegates
  to `Timer.RestoreAsync()` then refreshes the display — S7 calls this one, not `Timer.RestoreAsync()`
  directly, so the UI updates); `public event Action? StateChanged` (added in S7 — raised after
  Start, Pause, Lap, Stop, and Restore so a parent can push a refresh to something it renders
  elsewhere, such as `RecordsListControl`'s laps panel or S8's tray icon); `public event Action?
  Tick` (added in S8 — raised from the internal 1000 ms `System.Windows.Forms.Timer`'s own `Tick`
  handler, so a parent gets the same once-a-second, guaranteed-UI-thread heartbeat without a second
  timer); and the four action methods `StartTimer()`, `PauseTimerAsync()`, `AddLap()`,
  `StopTimerAsync()` (added in S8 — each does what its `Click` handler already does, so a caller
  other than this control's own buttons, such as the S8 tray menu or S10's hotkeys, can drive a
  transition without leaving the window's own display stale).
- **Done when:** manual/visual check that all three button rows from §2.6 render correctly and
  that the timer disposes cleanly on control disposal (no ticks fire after teardown). No interactive
  display was available in the environment that built this stage, so the visual render itself is
  unverified — verify it manually before shipping (§6's tray/hotkey manual-check precedent). The
  build-clean/analyzer-clean/test-green portions of "done" are verified: `dotnet build -c Release`
  zero warnings, `dotnet test` all 40 passing (unchanged — S4 already covers everything this control
  delegates to; no new automated tests were needed for this stage).
- **Owns acceptance criteria:** "Lap button exists only while running" (UI half, absent at idle
  and while paused).
- **Out of scope:** records/laps list rendering (S6), tray (S8).

### S6 — `RecordsListControl` + `ClearRecordsDialog` ✅ Done

- **Depends on:** S1 (`TimeFormat`), S2 (`Palette`), S3 (models).
- **Files:** `StopwatchApp/Controls/RecordsListControl.cs`, `StopwatchApp/Controls/ClearRecordsDialog.cs`,
  `StopwatchApp.Tests/RecordsListControlTests.cs` (covers the two pure row-formatting helpers).
- **Build:** records panel (always shown, newest first, scrollable, `No records yet` empty state),
  laps panel (shown only when `Laps.Count > 0`), the exact row templates from §2.6 (with emoji),
  the `Clear All Records` button (visible only when `Records.Count > 0`), and the confirm dialog
  with the exact title/body/button text from §2.6. Built entirely from stock WinForms controls
  (`ListBox`/`Label`/`Button`), which already follow `Application.SetColorMode` for free — the one
  manually-colored surface is the empty-state label, driven by a `bool Dark` property (default
  `false`; S12 wires it to the live OS setting). See `AGENTS.md` §17, dated 2026-09-10, for this
  and for the `ClearRecordsDialog.ShowConfirm` `DialogResult` mapping.
- **Public interface:** two `UserControl`/`Form` types; `RecordsListControl` exposes
  `UpdateRecords(IReadOnlyList<StopwatchRecord>)` and `UpdateLaps(IReadOnlyList<Lap>)` to push data
  in (pushed from `MainForm`/`StopwatchControl`, not pulled) and raises a `ClearAllRequested` event;
  `ClearRecordsDialog.ShowConfirm(IWin32Window owner)` returns `DialogResult.Yes` for "Clear All",
  `DialogResult.Cancel` otherwise.
- **Done when:** `Clear All Records` button visibility toggles correctly with an empty vs.
  non-empty list, in isolation from live timer state (feed it a fixed list in a manual check).
- **Owns acceptance criteria:** "Clear-all: button hidden with zero records, Cancel leaves records
  untouched, Clear All empties the list back to the empty state."
- **Out of scope:** wiring the `Clear All` confirmation to `IStopwatchStore.ClearAllRecordsAsync`
  — that call happens in S7 (`MainForm`), which owns both controls.

### S7 — `MainForm` orchestration ✅ Done

- **Depends on:** S5, S6.
- **Files:** `StopwatchApp/MainForm.cs` (replaces the S0 stub),
  `StopwatchApp/Controls/StopwatchControl.cs`, `StopwatchApp/Services/StopwatchTimer.cs`, and
  `StopwatchApp.Tests/StopwatchTimerTests.cs`.
- **Build:** constructs `Database`, `StopwatchControl`, `RecordsListControl`, wires
  `StopwatchControl.Timer.RecordsChanged`/`RecordsListControl.ClearAllRequested` between the two
  controls (through `StopwatchTimer`, not directly against `IStopwatchStore` — see `AGENTS.md` §17,
  dated 2026-09-10), and calls `StopwatchControl.RestoreAsync()` once at startup (after the DB
  connection opens) to show the frozen elapsed time, `Continue` button, restored laps, and "resumed
  from a pause" note if a saved session exists. **No business logic lives here** — pure wiring, per
  `AGENTS.md` §3/§5.
- **Public interface:** `MainForm : Form` retains its default constructor. To preserve the timer's
  ownership of its records cache after Clear All, `StopwatchTimer.ClearRecordsAsync()` clears the
  store then reloads `Records` and raises `RecordsChanged`. `StopwatchControl.StateChanged` lets
  `MainForm` refresh the laps panel immediately after Start, Pause, Lap, Stop, or restoration.
- **Done when:** the form runs standalone (`dotnet run`), shows the idle state correctly, a
  Start→Pause→app-restart→relaunch cycle shows Continue with laps intact, and Clear All actually
  empties the SQLite table. The automated gate is verified: `csharpier check .`,
  `dotnet build -c Release` (zero warnings/errors), and `dotnet test` (43 passing). The interactive
  desktop checks remain to be performed manually before shipping.
- **Owns acceptance criteria:** "Laps and Continue survive an app restart after Pause" (the
  full round-trip, end to end — S4 owns the state-machine half, this owns the wiring proof).
- **Out of scope:** tray, hotkeys, single-instance, window-hide-to-tray — the form at this stage
  behaves like an ordinary window with a visible taskbar button and no tray icon.

### S8 — `TrayIconService` ✅ Done

- **Depends on:** S1 (`TimeFormat`, for the tooltip), S7 (`MainForm` to restore/activate).
- **Files:** `StopwatchApp/Services/TrayIconService.cs`, `StopwatchApp/Controls/StopwatchControl.cs`,
  `StopwatchApp/MainForm.cs`, `StopwatchApp/StopwatchApp.csproj`.
- **Build:** §3.1 (32×32 GDI+ rendered icon, hours over minutes, regenerate only when the visible
  minute changes, `DestroyIcon` on every replace) and §3.2 (context menu order: `Open`, separator,
  state action(s), separator, `Exit`; double-click opens/activates). `StopwatchControl` gained a
  `Tick` event (its existing 1000 ms UI timer is the once-a-second, guaranteed-UI-thread heartbeat —
  no second timer) and four action methods (`StartTimer`/`PauseTimerAsync`/`AddLap`/`StopTimerAsync`)
  so the tray menu drives transitions without leaving the window's own display stale. See
  `AGENTS.md` §17, dated 2026-09-10, for these and for the `AllowUnsafeBlocks` addition the
  `DestroyIcon` P/Invoke requires.
- **Public interface:** `TrayIconService(StopwatchControl control, Action onOpen, Action onExit)`,
  `bool DarkMode { get; set; }`, `void UpdateDisplay(long elapsedMs, bool running, bool paused)`,
  `IDisposable`. Takes callbacks and the control, not a `MainForm` reference (AGENTS.md §3).
- **Done when:** `csharpier check .`, `dotnet build -c Release` (zero warnings/errors), and
  `dotnet test` (43 passing) are verified; a smoke launch confirmed the app starts and shows its
  window without crashing. The full interactive checklist — icon renders and updates at most
  once/second, tooltip shows `HH:MM:SS`, GDI handle count stays flat over several minutes via Task
  Manager — requires a human at the keyboard and remains to be run before shipping.
- **Owns acceptance criteria:** "Tray: the icon updates while running and reflects the current
  hour/minute; the tooltip shows the full `HH:MM:SS`." (shared with S9; see there for the rest).
- **Out of scope:** window hide/show behavior (S9 — implemented in the same change, see below),
  hotkeys (S10).

### S9 — Window-to-tray behavior ✅ Done

- **Depends on:** S8.
- **Files:** `StopwatchApp/MainForm.cs`.
- **Build:** §3.3 — `OnFormClosing` cancels `CloseReason.UserClosing`, hides, sets
  `ShowInTaskbar = false`; `OnResize` does the same when minimized; the tray's `Exit` item
  (`MainForm.ExitApplication`) disposes `TrayIconService`, then awaits `Database.DisposeAsync()`,
  then calls `Application.Exit()`. `Close()` calls made programmatically (e.g. the DB-failure path
  in `InitializeAsync`) report `CloseReason.None`, not `UserClosing`, so they are not intercepted —
  see `AGENTS.md` §17, dated 2026-09-10.
- **Public interface:** none new.
- **Done when:** automated gate verified (see S8); the interactive checklist — both close and
  minimize hide the window with no taskbar button remaining, opening from the tray restores and
  activates, Exit leaves no ghost icon — remains a manual check.
- **Owns acceptance criteria:** "both close and minimize hide the window and remove its taskbar
  button; Exit terminates the process with no icon left behind in the tray."
- **Out of scope:** hotkeys, single-instance.

### S10 — Keyboard shortcuts ✅ Done

- **Depends on:** S7 (needs `MainForm` to override `ProcessCmdKey`), S8 (the `StartTimer`/
  `PauseTimerAsync`/`AddLap`/`StopTimerAsync` action methods this dispatches to).
- **Why not `HotkeyService`/global hotkeys, as originally planned:** a `RegisterHotKey` registration
  is bound to one specific `HWND`. `MainForm.HideToTray()` sets `ShowInTaskbar = false`, which makes
  WinForms destroy and recreate the form's native window handle (it governs `WS_EX_APPWINDOW`/the
  owner relationship, neither changeable on a live handle) — so the hotkeys would silently stop
  firing after the very first hide-to-tray, exactly the state global hotkeys exist to serve. Rather
  than work around the handle lifecycle (e.g. a message-only `NativeWindow`), the feature itself was
  dropped: this app is mouse-driven, and a shortcut that only works while the window is visible has
  no use for a *global* binding. See `AGENTS.md` §17, dated 2026-09-11, and §3.4/§10.4 above.
- **Files:** `StopwatchApp/Controls/StopwatchShortcut.cs` (new), `StopwatchApp/Controls/StopwatchControl.cs`,
  `StopwatchApp/MainForm.cs`, `StopwatchApp.Tests/StopwatchControlTests.cs` (new).
- **Build:** §3.4/§10.4 — the three-key table (`Space`/`Shift+Space`/`Enter`), active only while the
  main window has focus. `StopwatchControl.MapShortcut(Keys)` is the pure, unit-tested mapping;
  `StopwatchControl` also gained a `ToolTip` showing each button's shortcut hint (`"Pause (Space)"`,
  etc., refreshed on the primary button as its text switches between `Start`/`Continue`).
  `MainForm.ProcessCmdKey` calls `MapShortcut` and dispatches to the existing S8 action methods —
  chosen over `KeyDown`/`AcceptButton` because it runs before a focused button's own Space/Enter
  handling, so returning `true` both fires the shortcut and stops it from double-firing whatever
  button has focus. No wrong-state guards needed: `Lap()`/`Stop()`'s own no-op guards (§2.3) already
  absorb a shortcut pressed where it doesn't apply.
- **Public interface:** `public static StopwatchShortcut? StopwatchControl.MapShortcut(Keys keyData)`;
  no changes to any existing public member's signature.
- **Done when:** `csharpier check .` / `dotnet build -c Release` (zero warnings) / `dotnet test`
  (49 passing — the existing 43 plus 6 new `MapShortcut` cases) all pass. Manual check: with the
  window focused, `Space`/`Shift+Space`/`Enter` each do what their button does, including from a
  freshly-clicked (focused) button (proving `ProcessCmdKey` suppresses the double-fire); none of the
  three fires while hidden to the tray or while `ClearRecordsDialog` is open; hovering each button
  shows its shortcut hint.
- **Owns acceptance criteria:** the keyboard-shortcuts bullet added to §6/`AGENTS.md` §14 by this
  stage (manual-only check, no automated acceptance criterion beyond `MapShortcut`'s own tests).
- **Out of scope:** global/system-wide hotkeys (removed from the project entirely by this stage),
  user-configurable bindings, single-instance (S11).

### S11 — Single-instance enforcement ✅ Done

- **Depends on:** S7 (needs a window to restore/activate).
- **Files:** `StopwatchApp/Program.cs`, `StopwatchApp/MainForm.cs`.
- **Build:** §3.5/§10.5 — named `Mutex` at startup; a second instance detects the existing mutex,
  locates the first instance's window via `FindWindow(null, MainForm.WindowTitle)`, and sends it a
  `RegisterWindowMessage` + `PostMessage` asking it to restore/activate, then exits immediately.
  **Not `PostMessage(HWND_BROADCAST, ...)`** as originally planned — see `AGENTS.md` §17, dated
  2026-09-11, for why a broadcast silently never reaches the window once it's hidden to tray (the
  exact scenario this feature exists to serve), and for the separate `EntryPoint` gotcha on the
  three `user32.dll` P/Invokes (`RegisterWindowMessageW`/`PostMessageW`/`FindWindowW` — the bare
  names aren't real exports and crash with `EntryPointNotFoundException`). `MainForm.WindowTitle`
  is now a shared `internal const` ("Stopwatch") for the `FindWindow` lookup. `MainForm.WndProc` is
  the receiving end, comparing `m.Msg` against the registered message id and calling the same
  `RestoreWindow()` the tray's `Open` item uses.
- **Public interface:** none new (logic lives in `Program.Main`, plus a `WndProc` override in
  `MainForm` for the registered message, plus the `MainForm.WindowTitle`/`Program.
  RegisterWindowMessage`/`Program.ActivateMessageName` internals the two classes share).
- **Done when:** manually verified (driving both instances via PowerShell + P/Invoke, since no
  interactive desktop session narrates itself) — launching a second instance while one is running
  restores/activates the first window and the second process exits (code 0) with no second window
  ever appearing, in both the normally-visible and the hidden-to-tray starting states. Automated
  gate verified: `csharpier check .`, `dotnet build -c Release` (zero warnings), `dotnet test` (49
  passing, unchanged — this stage added no automated tests, matching its no-new-public-interface
  scope).
- **Owns acceptance criteria:** none listed by number in §6 (single-instance has no dedicated §6
  bullet — verified manually as specified in §3.5).
- **Out of scope:** hotkeys.

### S11a — Visual design refresh

- **Depends on:** S6, S7, S8 (needs the finished records/laps rendering, window layout, and tray
  icon to redesign); scheduled after S9/S10/S11 so it reflects the complete feature set rather than
  a partial one, and before S12 so S12 wires OS light/dark detection to the *final* palette rather
  than to a placeholder that gets replaced right after.
- **Files:** `StopwatchApp/Theme/Palette.cs` (extended with spacing/corner-radius/typography
  constants alongside the existing color tables — same per-role accessor shape from §3.1, still
  compared via `.ToArgb()`), `StopwatchApp/Controls/StopwatchControl.cs`,
  `StopwatchApp/Controls/RecordsListControl.cs`, `StopwatchApp/Controls/ClearRecordsDialog.cs`,
  `StopwatchApp/Services/TrayIconService.cs` (icon tint only — layout stays two-digit rows per
  §10.1). Plus one artifact that never enters the repo: an HTML design comp, described below.
- **Build:** two parts, in order.
  1. **Design comp, via the `impeccable` skill.** WinForms has no browser-rendered surface, and
     `impeccable` is built for frontend/web interfaces — it cannot edit this app's controls
     directly. Use it instead to produce a static HTML/CSS mockup of the app's key states (idle,
     running, paused, a records list with several rows, and the empty-records state) as a
     throwaway reference, never shipped and never part of the .NET project. The goal is a concrete
     design system to build toward: a type scale, a spacing scale, a corner-radius/elevation
     language, and an iconography direction (e.g., glyph icons on the Start/Pause/Lap/Stop buttons
     rather than plain colored rectangles) — aimed at a Windows 11 Fluent-adjacent look, not a
     generic web-app look. Sign off on the comp before starting part 2; it is the spec for it.
  2. **Translation to WinForms.** Re-derive `Palette.cs`'s color tables from the comp, add the new
     spacing/corner-radius constants beside them, and rebuild each affected control's layout and
     (where stock `Button`/`ListBox` chrome can't reach the comp — rounded corners, hover
     elevation) owner-drawn painting to match. `TrayIconService.RenderIcon`'s tint pulls from the
     same refreshed palette rather than being restyled independently (its digit-row layout is
     fixed by §10.1 and out of scope for this stage).
- **Public interface:** none new or changed — no control gains, loses, or changes the signature of
  a method or event; this stage only changes how existing surfaces are drawn and spaced.
- **Done when:** a side-by-side manual comparison against the design comp for idle/running/paused/
  records states, in both light and dark mode, matches; `csharpier check .` / `dotnet build
  -c Release` (zero warnings) / `dotnet test` (all green) still pass unchanged, since no control's
  tested behavior changes, only its rendering.
- **Owns acceptance criteria:** none listed by number in §6 — a look-and-feel pass with no
  functional acceptance criterion, verified only by the manual comparison above.
- **Out of scope:** any new feature, OS dark-mode/DPI wiring (S12's job — this stage produces the
  palette S12 then wires live), motion/animation (not part of this app's brief per §1).

### S11b — Window position memory

- **Depends on:** S9 (window-to-tray hide/show mechanics this stage extends), S3a (the
  `PRAGMA user_version` migration path — this stage's new table is migration 2, the first real use of
  that path beyond migration 1 itself). Independent of S11a's visual changes — touches a disjoint file
  set (`MainForm.cs`, `IStopwatchStore`/`Database`/`SchemaMigrations`, not the controls S11a repainted)
  and could have run in parallel with it; listed after S11a here only for roadmap reading order, since
  S11a already landed by the time this stage was added.
- **Why:** added post-hoc at the repo owner's explicit request (2026-09-11, not a gap found during
  implementation) — the window should always open centered, *unless* the user has dragged it
  somewhere, in which case it should reopen there instead. See `AGENTS.md` §17, dated 2026-09-11, and
  §3.6/§10.6 for the full spec.
- **Files:** `StopwatchApp/Services/SchemaMigrations.cs` (+migration 2), `StopwatchApp/Services/IStopwatchStore.cs`,
  `StopwatchApp/Services/Database.cs` (+`SaveWindowPositionAsync`/`LoadWindowPositionAsync`),
  `StopwatchApp/MainForm.cs` (positioning + persistence logic), `StopwatchApp.Tests/DatabaseTests.cs`
  (+round-trip tests for the new table), `StopwatchApp.Tests/SchemaMigrationsTests.cs` if one doesn't
  already exist, or the equivalent existing test file (+migration-2-applies-cleanly test).
- **Build:** §3.6/§10.6 exactly — center on `Screen.PrimaryScreen.WorkingArea` when no valid saved
  position exists (including when a saved position no longer intersects any connected screen); persist
  `Location` only on hide-to-tray or `ExitApplication`, and only when it differs from the
  `_shownAtLocation` baseline recorded the last time the window was positioned. `window_position` is
  migration 2 in `SchemaMigrations.cs` — **no `IF NOT EXISTS`** (per `AGENTS.md` §9's rule: only
  migration 1 needs it), appended after migration 1, never edited into it.
- **Public interface:** `IStopwatchStore` gains exactly two methods (shown in §4 above);
  `Database` implements both, wrapped in the same swallow-errors try/catch as every other storage
  method (§9's deliberate, commented design). No existing `IStopwatchStore` member changes signature.
  `MainForm` gains no new public members — the positioning logic is private, wired into the existing
  show/hide/exit paths from S7/S9.
- **Done when:** round-trip tests cover save → load → matches; a fresh database with no
  `window_position` row centers the window; a database created before this migration (has `records`/
  `paused_session` but no `window_position`, `user_version` at 1) opens without data loss and ends up
  stamped at migration 2. Automated: `csharpier check .` / `dotnet build -c Release` (zero warnings) /
  `dotnet test` (all green, including the new round-trip and migration tests). Manual (no interactive
  desktop available in an agent environment, same precedent as S8/S9): launch, confirm the window
  opens centered on first run, drag it, hide to tray, reopen — confirm it reopens at the dragged
  position, not centered; drag again and repeat once more to confirm the baseline updates correctly
  each cycle; disconnect/reconfigure a secondary monitor (or simulate via a very off-screen manual DB
  edit) and confirm the app falls back to centering instead of opening unreachably off-screen.
- **Owns acceptance criteria:** the window-position bullet added to `AGENTS.md` §14 by this stage (see
  below) — not present before, since this feature didn't exist in the original spec.
- **Out of scope:** remembering window *size* (only `Location` is tracked, per the user's explicit
  "only if I move it" — resizing is untouched); per-monitor saved positions (one single-slot save, like
  `paused_session`); any change to how the window hides to tray or restores from it (S9's mechanics are
  reused, not modified).

### S11c — Tray icon: large minutes under an hour

- **Depends on:** S8 (`TrayIconService` and `RenderIcon`, which this stage modifies), S11a (the
  refreshed `Palette` tint accessors `RenderIcon` already reads — unchanged by this stage, just still
  the dependency).
- **Why:** added post-hoc at the repo owner's explicit request, dated 2026-09-11. The owner first
  asked whether the tray icon could render wide like the Windows taskbar clock; that's genuinely
  unsupported (see `AGENTS.md` §17's first 2026-09-11 entry) via the standard `Shell_NotifyIcon` API
  every tray icon (including third-party libraries like `H.NotifyIcon`) is built on. The owner then
  clarified the actual goal: not a clock-width readout, at most "two icons wide," specifically so
  minute ticks read clearly in the tray. A two-separate-`NotifyIcon` approach was offered and
  rejected — Windows gives no API to keep two icons adjacent, and the user can independently hide or
  reorder either one via the OS's own tray-icon settings, which would silently break the "one widget"
  effect. Settled instead on a single-icon change: since a stopwatch session under an hour is the
  common case, drop the (in that case, always-`00`) hours row entirely and let the minutes digits use
  the full 32×32 canvas — meaningfully larger and easier to read at a glance than the current stacked
  layout, with no adjacency/ordering risk since it's still exactly one `NotifyIcon`. See `AGENTS.md`
  §17's second 2026-09-11 entry and §10.1/§3.1 for the full spec.
- **Files:** `StopwatchApp/Services/TrayIconService.cs` (`RenderIcon` and its dirty-check state),
  possibly a small addition to `StopwatchApp.Tests/` if `RenderIcon`'s layout selection is pure enough
  to unit test in isolation from GDI+ handle creation (agent's call — the existing icon-rendering code
  was not previously unit tested, per §13's UI-free-testability rule not extending to raw GDI+ output).
- **Build:** §3.1/§10.1 exactly — `RenderIcon` picks the large-MM-only layout when elapsed hours == 0,
  else the existing two-stacked-rows layout; the once-per-second, changed-value-only regeneration rule
  (§3.1/§10.1, from S8) extends to treat a layout switch as a change even where it happens to coincide
  with a minute-digit change already — track the active layout explicitly in the dirty-check state,
  don't rely on the coincidence. `DestroyIcon` on every handle replacement (unchanged rule, both
  layouts). Icon tint by run state (idle/paused/running, from S8/S11a) is unchanged and orthogonal to
  which layout is active. Tooltip text and format (`FormatTime`, full `HH:MM:SS`) is unchanged.
  Follow-up (same day, repo owner clarification): the stacked layout's hours row is **not**
  zero-padded (`2`, not `02`; no digit-count cap) — a tray-icon-only exception to this app's otherwise
  always-zero-padded digit formatting, so it reads like a written clock time ("2:01"). The minutes row
  is unaffected. See `AGENTS.md` §10.1/§17.
- **Public interface:** none changed — `TrayIconService`'s constructor and public members (§3.1) are
  exactly as S8 left them; this stage only changes `RenderIcon`'s internal drawing logic.
- **Done when:** `csharpier check .` / `dotnet build -c Release` (zero warnings) / `dotnet test` (all
  green, unchanged count unless the agent added `RenderIcon`-layout-selection tests per the Files note
  above) pass. Manual (no interactive desktop available in an agent environment, same precedent as
  S8/S9/S11a/S11b): start a session, confirm the tray icon shows large minute digits only while under
  an hour elapsed and updates each time the displayed minute changes; let (or fast-forward, if a debug
  hook exists — otherwise a long-running manual session) elapsed cross the 1-hour mark and confirm the
  icon switches to the stacked HH-over-MM layout at that instant, not before or after; confirm the
  tooltip still reads the full `HH:MM:SS` regardless of which layout is showing.
- **Owns acceptance criteria:** extends the existing Tray bullet in `AGENTS.md` §14 (owned jointly by
  S8/S9) with the two-layout behavior — see the updated bullet there.
- **Out of scope:** the "two icons wide" literal approach (rejected, see Why above); any change to
  icon tint, tooltip format, context menu, or double-click behavior (all S8/S9, unchanged); DPI-based
  resizing of the icon itself (S12's job).

### S12 — Theme, DPI, and version polish pass ✅ Done

- **Depends on:** S9, S10, S11 (i.e., after the whole feature set exists), and S11a — the light/dark
  detection this stage wires must apply to the refreshed palette S11a produces, not the one it
  replaces. S11b/S11c already merged too, but this stage doesn't touch either of their code paths.
- **Files:** touches across `Program.cs`, `MainForm.cs`, `StopwatchControl.cs`,
  `RecordsListControl.cs`, `TrayIconService.cs`.
- **Build:** `Application.SetColorMode(SystemColorMode.System)` in `Program.Main` — **already present**
  (confirm it's still correctly placed: after `ApplicationConfiguration.Initialize()`, before
  `Application.Run`, no action needed if so). The real work:
  - Wire **live OS dark/light-mode detection** into `StopwatchControl.DarkMode`,
    `RecordsListControl.Dark`, and `TrayIconService.DarkMode` — all three still default to the
    hardcoded `false` every stage from S5 onward deliberately left for this stage (see `AGENTS.md`
    §17's S5/S6/S8 entries). Research the exact .NET 10 WinForms API for reading current effective
    dark-mode state via Context7 or Microsoft Learn before writing code — do not guess a member name
    from training data (a candidate to verify is `Application.IsDarkModeEnabled`, but confirm it).
  - React to the OS theme changing **while the app is running**, not just at startup — the standard
    WinForms mechanism is `Microsoft.Win32.SystemEvents.UserPreferenceChanged`; confirm the correct
    `UserPreferenceCategory` to filter on via the same research, and update all three `DarkMode`/
    `Dark` properties (triggering their existing repaint/re-render paths) when it fires. Unsubscribe
    on form/service disposal — this is a static, process-wide event; a missed unsubscribe leaks the
    subscriber.
  - Handle `DpiChanged` to re-render the tray icon at the new size.
  - Add the `<Version>1.0.0</Version>` (already in the `.csproj`) surfaced somewhere in the UI
    (footer or tray "About" entry — agent's call, matching precedent for unfixed UI shape).
- **Public interface:** none new — this stage only wires existing pieces together correctly.
- **Done when:** manual check in both Windows light and dark mode — window chrome and stock
  controls follow the OS setting, custom-painted surfaces (button colors, empty-state text) match
  §2.8's tables in both themes, and flipping the OS setting *while the app is running* updates them
  live, without a restart; moving the window between monitors with different DPI re-renders the tray
  icon at the correct size; the version number is visible somewhere in the UI.
- **Owns acceptance criteria:** none listed by number in §6 (theme/DPI are verified manually, no
  §6 bullet is dedicated to them).
- **Out of scope:** any new feature — this is strictly a polish/consistency pass.

### S13 — Publish

- **Depends on:** S12.
- **Files:** none (a documented command, optionally a `publish.ps1` helper script).
- **Build:** `AGENTS.md` §15 — framework-dependent
  `dotnet publish StopwatchApp/StopwatchApp.csproj -c Release -r win-x64 --self-contained false`.
- **Public interface:** none.
- **Done when:** the publish folder runs standalone on a machine with the .NET 10 Desktop Runtime
  installed (or the same dev machine, as a smoke test), and every §6 acceptance criterion has been
  re-verified once against the published build.
- **Owns acceptance criteria:** none new — this is the final full-suite re-verification of every
  bullet in §6 against the shipped artifact.
- **Out of scope:** installer/MSIX packaging, code signing (both explicitly out of scope per §1).

---

## 6. Acceptance criteria

The build must satisfy every item below. These double as the test list — implement them as
automated tests wherever the behavior is UI-free, and as a manual check where it genuinely requires
a running window (tray, hotkeys). Each bullet is tagged with the §5 stage that owns it, so
"is stage N done?" is answerable without re-reading this whole section.

- **[S4]** Initial display is exactly `00:00:00`; Start and Stop are both visible at idle.
- **[S4/S5]** The Lap button exists only while running — absent at idle and while paused.
- **[S4]** Consecutive laps are splits, not cumulative totals: a lap after 61 s shows `00:01`; a
  second lap 121 s later shows `00:02` (not `00:03`).
- **[S4]** Laps clear when a fresh session starts after Stop.
- **[S4/S7]** Laps and the Continue state survive an app restart after Pause (i.e., they round-trip
  through the `paused_session` table).
- **[S4]** Stop after at least one lap adds a final partial lap; clicking Stop again afterward
  (with no new Start in between) never appends another lap.
- **[S4]** Pause → Stop → restart shows **Start**, not Continue (stopping clears the saved
  session).
- **[S4]** Stopping twice in a row without restarting yields exactly one record (the duplicate
  guard in `Stop()` holds).
- **[S6]** Clear-all: the button is hidden when there are zero records, `Cancel` leaves records
  untouched, `Clear All` empties the list back to the `No records yet` empty state.
- **[S1/S4]** A session under one minute stores `elapsedMinutes == 0` but still displays `00:01`
  via `FormatElapsed`'s floor.
- **[S3]** `GetAllRecordsAsync()` on an empty table returns an empty list; records saved in order
  0, 1, 2 (by insertion) read back as 2, 1, 0 (newest first).
- **[S3]** A corrupt or missing saved-session row returns `null` from `LoadPausedSessionAsync()`
  with no exception thrown.
- **[S3a]** A database created by an earlier build (tables present, no `user_version` stamp) opens
  without data loss and ends up stamped at the current schema version.
- **[S8/S9]** Tray: the icon updates while running and reflects the current hour/minute; the
  tooltip shows the full `HH:MM:SS`; both close and minimize hide the window and remove its
  taskbar button; Exit terminates the process with no icon left behind in the tray.
- **[S11c]** Tray icon layout: while elapsed hours == 0, the icon shows large minute-only digits;
  once elapsed reaches 1 hour, it switches to the stacked hours-over-minutes layout at exactly that
  boundary; the tooltip's full `HH:MM:SS` text is unaffected by which layout is showing.
- **[S11b]** Window position: with no saved position, the window opens centered on the primary
  screen; after being dragged and then hidden-to-tray/exited, it reopens at that exact position
  instead of centering; a saved position that no longer intersects any connected screen falls back
  to centering rather than opening off-screen; resizing alone (no drag) never triggers a save.

`[S13]` re-verifies every bullet above, once, against the published build — it owns none of them
individually but is the final gate that confirms none regressed in packaging.

---

## 7. Standing rules

Superseded by the root `AGENTS.md`, which is loaded automatically for every agent working in this
repo and is kept in sync with this section's original content (project overview, code style, lint
and analyzer settings, the format/build/test commands and post-change checklist, testing rules,
architecture rules, the "intentional behaviors — do not fix these" list, Windows-specific rules,
packaging, and git workflow). **Read `AGENTS.md`, not this section, for those rules** — it is the
authoritative copy and the one that stays current if any of this drifts. This plan's own job ends
at §6; §5 is the roadmap for turning §1–§4 into the codebase `AGENTS.md` then governs.
