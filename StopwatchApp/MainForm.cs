using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using StopwatchApp.Controls;
using StopwatchApp.Models;
using StopwatchApp.Services;
using StopwatchApp.Theme;

namespace StopwatchApp;

/// <summary>
/// The application's main window. A thin orchestrator that wires controls and services together;
/// it contains no business logic of its own (see AGENTS.md §3).
/// </summary>
public sealed class MainForm : Form
{
  /// <summary>
  /// The window title, also used by <see cref="Program"/> to locate this window from a second
  /// instance (AGENTS.md §10.5).
  /// </summary>
  internal const string WindowTitle = "Stopwatch";

  // S14/S14a/S14b/S14c (AGENTS.md §17) — the window's fixed content size, expressed in *design*
  // pixels at the 96dpi baseline. Width is driven by the widest realistic records/laps row rendered
  // in the mono body font (a 5-digit lap id with a 4-digit elapsed hour count). Height
  // (632x680 -> 632x728 in S14b -> 632x732 in S14c) is the worst case with the resumed-pause note,
  // the laps panel, 5 records, "Clear All Records", and the disabled "Manage Records" placeholder
  // all visible at once, measured directly from the real, fixed-up control tree rather than hand
  // arithmetic (see §17's S14b/S14c entries for the exact measured numbers) — records are capped to
  // MaxDisplayedRecords, so this worst case is a genuine, known constant instead of "however much
  // space happens to be left." This literal alone is never assigned to ClientSize —
  // ComputeFixedClientSize scales it to the window's real device DPI first, and widens it further
  // if the live-DPI row measurement needs more than this design width provides. Internal (not
  // private) so MainFormLayoutTests can pin the row-width test to this literal instead of
  // duplicating it.
  internal static readonly Size FixedClientSize = new(632, 732);

  // S14a (AGENTS.md §17) — the non-text chrome a records/laps row must fit alongside, in the same
  // 96dpi design pixels as FixedClientSize above: RecordsListControl.DrawRow's text inset, the
  // laps/records ListBox's own vertical scrollbar, RecordsListControl's card Padding, the
  // TableLayoutPanel cell's default Margin, and MainForm's own root layout Padding. Kept as design
  // constants (not read from SystemInformation at call time) so RequiredClientWidth stays a pure
  // function of its dpi parameter and is testable across DPIs the test host isn't actually running.
  private const int DesignRowTextInset = Palette.SpacingSm * 2;
  private const int DesignScrollBarWidth = 17; // SystemInformation.VerticalScrollBarWidth at 96dpi
  private const int DesignCardPadding = Palette.SpacingLg * 2;
  private const int DesignCellMargin = 6; // WinForms' default Control.Margin is 3px/side
  private const int DesignRootPadding = Palette.SpacingMd * 2;

  private readonly Database _database;
  private readonly StopwatchControl _stopwatchControl;
  private readonly RecordsListControl _recordsListControl;
  private readonly TrayIconService _trayIconService;
  private readonly Label _versionLabel;
  private readonly Font _bodyFont;
  private readonly Font _captionFont;
  private readonly Icon _appIcon;
  private readonly int _activateMessage;

  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  public MainForm()
  {
    Text = WindowTitle;
    // S14 (AGENTS.md §10.3/§17) — the window is fixed-size and not user-resizable: FixedSingle
    // border, no maximize box, no MinimumSize (FixedSingle already blocks dragging, and a fixed
    // MinimumSize fights WinForms' own PerMonitorV2 rescale on DpiChanged). FormBorderStyle affects
    // the non-client chrome that ClientSize/PositionWindowCentered measure against, so it's set
    // before either runs.
    FormBorderStyle = FormBorderStyle.FixedSingle;
    MaximizeBox = false;
    // Dpi, not Font: the body font below is itself point-sized, so scaling a second time off the
    // font would double-apply the DPI factor. AutoScaleDimensions must be set alongside
    // AutoScaleMode for PerformAutoScale to do anything at all — with Dimensions left at its
    // default SizeF.Empty, AutoScaleMode.Dpi was a silent no-op (S14a, AGENTS.md §17): the window
    // stayed at literal 96dpi device pixels while its point-sized fonts scaled with the real DPI,
    // which is what made record rows clip at anything above 100% scaling.
    AutoScaleMode = AutoScaleMode.Dpi;
    AutoScaleDimensions = new SizeF(96F, 96F);
    DoubleBuffered = true;
    _bodyFont = Typography.CreateBodyFont();
    _captionFont = Typography.CreateCaptionFont();
    Font = _bodyFont;
    _appIcon = LoadAppIcon();
    Icon = _appIcon;
    // Clamped so a fixed (non-draggable) window can never end up taller than the screen at high
    // DPI — the user has no resize handle to rescue it with (AGENTS.md §17). DeviceDpi is a
    // reasonable value even before the handle exists (the system DPI); OnDpiChanged re-derives
    // this once the window is actually placed on a specific monitor.
    ClientSize = ClampToWorkingArea(ComputeFixedClientSize(DeviceDpi));
    // Manual, not CenterScreen: S11b (AGENTS.md §10.6) owns initial placement so a saved manual
    // position (loaded from the database once it's ready, in InitializeAsync below) can override
    // the synchronous default center set here.
    StartPosition = FormStartPosition.Manual;
    PositionWindowCentered();

    _database = new Database(Database.DefaultDatabasePath);
    _stopwatchControl = new StopwatchControl(_database, TimeProvider.System)
    {
      Dock = DockStyle.Fill,
    };
    _recordsListControl = new RecordsListControl();
    _trayIconService = new TrayIconService(_stopwatchControl, RestoreWindow, ExitApplication);
    _activateMessage = (int)Program.RegisterWindowMessage(Program.ActivateMessageName);
    // S12 (AGENTS.md §6/§15/§17) — the version footer; <Version> in the .csproj flows through to
    // Application.ProductVersion via the SDK's generated AssemblyInformationalVersionAttribute.
    _versionLabel = new Label
    {
      Dock = DockStyle.Fill,
      TextAlign = ContentAlignment.MiddleRight,
      AutoSize = false,
      Text = $"Stopwatch v{Application.ProductVersion}",
      Padding = new Padding(0, Palette.SpacingXs, 0, 0),
      Font = _captionFont,
    };

    TableLayoutPanel layout = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 3,
      Padding = new Padding(Palette.SpacingMd),
    };
    // S14 (AGENTS.md §17) — without an explicit ColumnStyle, a single-column TableLayoutPanel falls
    // back to an implicit AutoSize column that only happens to span the window's width; pinning it
    // to 100% makes that span structural instead of incidental.
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    // S14b (AGENTS.md §17) — AutoSize, not Percent(100): RecordsListControl now reports a real,
    // bounded preferred height (records capped at MaxDisplayedRecords), so it no longer needs to
    // stretch and fill whatever's left of the fixed window — that stretch was the actual source of
    // "the window is too tall" in typical use, where far fewer than the worst-case row count show.
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.Controls.Add(_stopwatchControl, 0, 0);
    layout.Controls.Add(_recordsListControl, 0, 1);
    layout.Controls.Add(_versionLabel, 0, 2);
    Controls.Add(layout);

    _stopwatchControl.Timer.RecordsChanged += RefreshRecords;
    _stopwatchControl.StateChanged += RefreshLaps;
    _stopwatchControl.StateChanged += RefreshTray;
    _stopwatchControl.Tick += RefreshTray;
    _recordsListControl.ClearAllRequested += ClearRecordsAsync;
    Load += InitializeAsync;

    // S12 (AGENTS.md §7/§11/§17) — apply the OS's current effective dark/light state once at
    // startup, then keep it live for the rest of the process by reacting to SystemEvents.
    ApplyDarkMode();
    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
  }

  /// <inheritdoc />
  protected override void OnFormClosing(FormClosingEventArgs e)
  {
    // A plain Close() call (the DB-failure path in InitializeAsync) reports CloseReason.None, not
    // UserClosing, so it is unaffected by this and falls through to a real close. Only the window
    // chrome's own close button/Alt+F4/system-menu Close reports UserClosing (AGENTS.md §10.3).
    if (e.CloseReason == CloseReason.UserClosing)
    {
      e.Cancel = true;
      HideToTray();
      return;
    }

    base.OnFormClosing(e);
  }

  /// <inheritdoc />
  protected override void OnResize(EventArgs e)
  {
    base.OnResize(e);
    if (WindowState == FormWindowState.Minimized)
    {
      HideToTray();
    }
  }

  /// <inheritdoc />
  protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
  {
    // ProcessCmdKey runs before a focused control (e.g. a Button) gets to handle Space/Enter
    // itself, so returning true here both dispatches the shortcut and stops it from also
    // triggering whatever button currently has focus (AGENTS.md §10.4/§17).
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(keyData);
    if (shortcut is null)
    {
      return base.ProcessCmdKey(ref msg, keyData);
    }

    DispatchShortcut(shortcut.Value);
    return true;
  }

  /// <inheritdoc />
  protected override void WndProc(ref Message m)
  {
    // A second instance detected our named mutex and posted this registered message instead of
    // running its own copy (AGENTS.md §10.5/§3.5); restore and activate this window in response.
    if (m.Msg == _activateMessage)
    {
      RestoreWindow();
      return;
    }

    base.WndProc(ref m);
  }

  /// <inheritdoc />
  protected override void OnDpiChanged(DpiChangedEventArgs e)
  {
    base.OnDpiChanged(e);
    // TrayIconService.UpdateDisplay only redraws the icon when the displayed hour/minute/state/
    // layout changes (AGENTS.md §10.1) — none of which a DPI change alone affects — so force a
    // fresh render explicitly here instead (AGENTS.md §7/§17).
    _trayIconService.RefreshIcon();
    // S14 (AGENTS.md §17) — re-clamp on every DPI change, not just at startup: moving this
    // fixed-size, non-resizable window to a higher-DPI monitor must not leave it taller than that
    // monitor's working area, since the user has no resize handle to shrink it back with.
    // S14a: re-derive from the *new* DPI (e.DeviceDpiNew), not the old size — the previous version
    // reset ClientSize to raw design pixels here, undoing whatever PerformAutoScale had just
    // correctly done for the new monitor.
    ClientSize = ClampToWorkingArea(ComputeFixedClientSize(e.DeviceDpiNew));
  }

  /// <inheritdoc />
  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      // SystemEvents is a static, process-wide event source: a missed unsubscribe here would leave
      // it holding a reference to OnUserPreferenceChanged (and therefore to this form) past this
      // form's own disposal (AGENTS.md §7/§17).
      SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
      // S14 (AGENTS.md §17) — fonts and icons created via `new Font(...)`/`new Icon(...)` hold
      // native GDI handles and are never disposed by the base Form; each factory/loader here is
      // documented as caller-owned, so ownership is discharged here.
      _bodyFont.Dispose();
      _captionFont.Dispose();
      _appIcon.Dispose();
    }

    base.Dispose(disposing);
  }

  private void HideToTray()
  {
    Hide();
    ShowInTaskbar = false;
  }

  private void RestoreWindow()
  {
    // WindowState is reset to Normal *before* positioning: Location reads/writes while
    // WindowState is Minimized are unreliable (Windows tracks a minimized window's actual on-screen
    // rect separately from its "restore" position). Safe on a hidden form — this just updates
    // placement, nothing is drawn until Show() below.
    WindowState = FormWindowState.Normal;
    // S14b (AGENTS.md §10.6/§17) — always centers; a tray Open/double-click, single-instance
    // activation, or restore-from-minimize all route through here.
    PositionWindowCentered();
    Show();
    ShowInTaskbar = true;
    Activate();
  }

  private async void ExitApplication()
  {
    // Dispose the tray icon before Application.Exit() — otherwise a ghost icon lingers in the
    // tray until the user hovers over its former location (AGENTS.md §10.3). Application.Exit()
    // is called only from here, the tray menu's Exit item, per the same section.
    _trayIconService.Dispose();
    await _database.DisposeAsync();
    Application.Exit();
  }

  /// <summary>
  /// Loads the app icon (S14, AGENTS.md §6/§17) embedded via the .csproj's
  /// <c>&lt;EmbeddedResource Include="Assets\app.ico" /&gt;</c> — the title bar, the taskbar button,
  /// and (via the .csproj's separate <c>&lt;ApplicationIcon&gt;</c>) the built .exe's own icon.
  /// </summary>
  private static Icon LoadAppIcon()
  {
    const string resourceName = "StopwatchApp.Assets.app.ico";
    using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName);
    if (stream is null)
    {
      throw new InvalidOperationException(
        $"Embedded resource \"{resourceName}\" was not found. Check the "
          + "<EmbeddedResource> item in StopwatchApp.csproj."
      );
    }

    return new Icon(stream);
  }

  /// <summary>
  /// Computes this window's fixed <see cref="Form.ClientSize"/> for a given device DPI (S14a,
  /// AGENTS.md §17): <see cref="FixedClientSize"/> scaled from its 96dpi design baseline up to
  /// <paramref name="deviceDpi"/>, widened if necessary to <see cref="RequiredClientWidth"/> so the
  /// widest realistic records/laps row never clips at that DPI. The result still needs
  /// <see cref="ClampToWorkingArea"/> applied before assignment.
  /// </summary>
  /// <param name="deviceDpi">The device DPI to size for — <see cref="Control.DeviceDpi"/> at
  /// construction, or <see cref="DpiChangedEventArgs.DeviceDpiNew"/> from <see cref="OnDpiChanged"/>.</param>
  private static Size ComputeFixedClientSize(int deviceDpi)
  {
    Size scaledDesignSize = ScaleToDpi(FixedClientSize, deviceDpi);
    using Font monoProbeFont = Typography.CreateMonospaceBodyFont();
    int requiredWidth = RequiredClientWidth(monoProbeFont, deviceDpi);
    // The fixed width is a floor, not a bare literal: it must never end up narrower than what the
    // widest realistic row actually needs at this DPI, since the frame cannot be dragged wider.
    return new Size(Math.Max(scaledDesignSize.Width, requiredWidth), scaledDesignSize.Height);
  }

  /// <summary>
  /// Scales a size expressed in 96dpi design pixels up to <paramref name="deviceDpi"/> device
  /// pixels (S14a, AGENTS.md §17) — the same linear ratio WinForms' own <c>PerformAutoScale</c>
  /// applies for <see cref="AutoScaleMode.Dpi"/>.
  /// </summary>
  private static Size ScaleToDpi(Size designSize, int deviceDpi)
  {
    float scale = deviceDpi / 96f;
    return new Size(
      (int)Math.Ceiling(designSize.Width * scale),
      (int)Math.Ceiling(designSize.Height * scale)
    );
  }

  /// <summary>
  /// Measures the widest row <see cref="RecordsListControl"/> can realistically render — a 5-digit
  /// lap id with a 4-digit elapsed-hour count (AGENTS.md §8.5) — in <paramref name="monoBodyFont"/>
  /// as it will actually render at <paramref name="deviceDpi"/>, and adds the itemized non-text
  /// chrome budget (<see cref="DesignRowTextInset"/> etc.) scaled to the same DPI. A <see cref="Font"/>'s
  /// point size is otherwise measured against a fixed 96dpi baseline regardless of the caller's
  /// actual DPI context (S14a, AGENTS.md §17) — the very mismatch that let record rows clip at
  /// anything above 100% scaling — so the font is rebuilt at an equivalent, pre-scaled size before
  /// measuring rather than measured as-is.
  /// </summary>
  /// <param name="monoBodyFont">The unscaled monospace body font (<see cref="Typography.CreateMonospaceBodyFont"/>).</param>
  /// <param name="deviceDpi">The device DPI to measure for.</param>
  /// <returns>The minimum client width, in device pixels at <paramref name="deviceDpi"/>, that fits
  /// the widest row without clipping.</returns>
  internal static int RequiredClientWidth(Font monoBodyFont, int deviceDpi)
  {
    float scale = deviceDpi / 96f;
    Lap worstCaseLap = new(
      Id: 99_999,
      StartTimestamp: 0,
      EndTimestamp: 60_000,
      ElapsedMinutes: 9_999 * 60
    );
    string worstCaseRow = RecordsListControl.FormatLapRow(worstCaseLap);

    using Font scaledFont = new(
      monoBodyFont.FontFamily,
      monoBodyFont.Size * scale,
      monoBodyFont.Style
    );
    int rowTextWidth = TextRenderer
      .MeasureText(
        worstCaseRow,
        scaledFont,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width;

    int designChrome =
      DesignRowTextInset
      + DesignScrollBarWidth
      + DesignCardPadding
      + DesignCellMargin
      + DesignRootPadding;
    int scaledChrome = (int)Math.Ceiling(designChrome * scale);

    return rowTextWidth + scaledChrome;
  }

  /// <summary>
  /// Reduces <paramref name="size"/>, if necessary, so that a <see cref="FormBorderStyle.FixedSingle"/>
  /// window of that client size fits within <see cref="Screen.PrimaryScreen"/>'s working area (S14,
  /// AGENTS.md §17). This window cannot be resized by dragging, so unlike a normal window, it must
  /// never be allowed to render taller or wider than the screen in the first place — there would be
  /// no way for the user to shrink it back down. <paramref name="size"/> must already be in device
  /// pixels for the target DPI (S14a, AGENTS.md §17) — <see cref="Screen.WorkingArea"/> is reported
  /// in physical pixels, so comparing it against a 96dpi design size silently under-clamped at any
  /// DPI above 100%.
  /// </summary>
  private static Size ClampToWorkingArea(Size size)
  {
    Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    Rectangle workingArea = primary.WorkingArea;
    int chromeHeight =
      SystemInformation.CaptionHeight + (SystemInformation.FixedFrameBorderSize.Height * 2);
    int chromeWidth = SystemInformation.FixedFrameBorderSize.Width * 2;
    int maxWidth = Math.Max(1, workingArea.Width - chromeWidth);
    int maxHeight = Math.Max(1, workingArea.Height - chromeHeight);
    return new Size(Math.Min(size.Width, maxWidth), Math.Min(size.Height, maxHeight));
  }

  /// <summary>
  /// Sets <see cref="Form.Location"/> to center the window on the primary screen's working area
  /// (S14b, AGENTS.md §10.6/§17) — called every time the window is shown: the constructor's
  /// synchronous default, and every tray Open/double-click, single-instance activation, or
  /// restore-from-minimize via <see cref="RestoreWindow"/>. The window never remembers or restores
  /// a previous position.
  /// </summary>
  private void PositionWindowCentered()
  {
    Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    Rectangle workingArea = primary.WorkingArea;
    Location = new Point(
      workingArea.X + (workingArea.Width - Width) / 2,
      workingArea.Y + (workingArea.Height - Height) / 2
    );
  }

  /// <summary>
  /// Applies the current effective OS dark/light state (AGENTS.md §7/§11/§17) to every theme-aware
  /// surface: <see cref="StopwatchControl.DarkMode"/> and <see cref="RecordsListControl.Dark"/>
  /// (whose setters already trigger their own repaint), <see cref="TrayIconService.DarkMode"/>, and
  /// the version footer's muted-text color. Called once at startup and again on every live OS theme
  /// change via <see cref="OnUserPreferenceChanged"/>.
  /// </summary>
  private void ApplyDarkMode()
  {
    bool dark = Application.IsDarkModeEnabled;
    _stopwatchControl.DarkMode = dark;
    _recordsListControl.Dark = dark;
    _trayIconService.DarkMode = dark;
    _versionLabel.ForeColor = Palette.MutedText(dark);
  }

  /// <summary>
  /// Reacts to a live OS user-preference change (AGENTS.md §7/§17). The light/dark-mode toggle is
  /// delivered under <see cref="UserPreferenceCategory.General"/> — it has no dedicated category of
  /// its own — so every other category is ignored here. This fires on <c>SystemEvents</c>'s own
  /// notification window's thread, not necessarily this form's UI thread, hence the
  /// <see cref="InvokeOnUiThread"/> marshal.
  /// </summary>
  private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
  {
    if (e.Category != UserPreferenceCategory.General)
    {
      return;
    }

    InvokeOnUiThread(ApplyDarkMode);
  }

  private async void InitializeAsync(object? sender, EventArgs e)
  {
    try
    {
      await _database.InitializeAsync();
    }
    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
    {
      // Deliberately not swallowed the way the rest of the storage layer is (AGENTS.md §9 scopes
      // that rule to individual reads/writes, not schema initialization) — a half-migrated schema
      // must not pass silently. Report it and close rather than run against a broken database.
      MessageBox.Show(
        this,
        $"Failed to open the database at \"{Database.DefaultDatabasePath}\":"
          + $"{Environment.NewLine}{Environment.NewLine}{ex.Message}",
        "Stopwatch",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error
      );
      Close();
      return;
    }

    await _stopwatchControl.RestoreAsync();
    RefreshLists();
    RefreshTray();
  }

  private async void DispatchShortcut(StopwatchShortcut shortcut)
  {
    // No wrong-state guards needed: Timer.Lap() and the ElapsedMs > 0 && SessionStartMs > 0 check
    // in Stop() already no-op when the shortcut doesn't apply to the current state (AGENTS.md
    // §8.3/§8.5), and Start() already resumes when paused, so Toggle covers both start and
    // continue with one branch.
    switch (shortcut)
    {
      case StopwatchShortcut.Toggle:
        if (_stopwatchControl.Timer.IsRunning)
        {
          await _stopwatchControl.PauseTimerAsync();
        }
        else
        {
          _stopwatchControl.StartTimer();
        }
        break;
      case StopwatchShortcut.Lap:
        _stopwatchControl.AddLap();
        break;
      case StopwatchShortcut.Stop:
        await _stopwatchControl.StopTimerAsync();
        break;
    }
  }

  private async void ClearRecordsAsync()
  {
    if (ClearRecordsDialog.ShowConfirm(this) != DialogResult.Yes)
    {
      return;
    }

    await _stopwatchControl.Timer.ClearRecordsAsync();
  }

  private void RefreshLists()
  {
    RefreshRecords();
    RefreshLaps();
  }

  private void RefreshRecords() =>
    InvokeOnUiThread(() => _recordsListControl.UpdateRecords(_stopwatchControl.Timer.Records));

  private void RefreshLaps() =>
    InvokeOnUiThread(() => _recordsListControl.UpdateLaps(_stopwatchControl.Timer.Laps));

  private void RefreshTray() =>
    InvokeOnUiThread(() =>
      _trayIconService.UpdateDisplay(
        _stopwatchControl.Timer.ElapsedMs,
        _stopwatchControl.Timer.IsRunning,
        _stopwatchControl.Timer.IsPaused
      )
    );

  private void InvokeOnUiThread(Action action)
  {
    if (IsDisposed || Disposing || !IsHandleCreated)
    {
      return;
    }

    if (InvokeRequired)
    {
      Invoke(action);
      return;
    }

    action();
  }
}
