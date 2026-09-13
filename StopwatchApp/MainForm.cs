using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using StopwatchApp.Controls;
using StopwatchApp.Formatting;
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
  internal const int WorstCaseElapsedMinutes = 24 * 60;
  private const int CompactClientWidth = 600;

  /// <summary>
  /// The window title, also used by <see cref="Program"/> to locate this window from a second
  /// instance (AGENTS.md §10.5). S15 (AGENTS.md §17) — carries the version, replacing the deleted
  /// version footer label: <c>Application.ProductVersion</c> needs no live <see cref="MainForm"/>
  /// instance to read (it comes from the assembly's own version metadata), so this stays a value
  /// computable before <see cref="Program"/> ever constructs one, matching how §10.5's
  /// second-instance path already used it.
  /// </summary>
  internal static readonly string WindowTitle = $"Stopwatch v{Application.ProductVersion}";

  // The non-text chrome a records/laps row must fit alongside, in 96dpi
  // design pixels: RecordsListControl.DrawRow's text inset, RecordsListControl's card Padding, the
  // TableLayoutPanel cell's default Margin, and MainForm's own root layout Padding.
  // DesignScrollBarWidth is budgeted separately, only against the laps row (S15, AGENTS.md §17) —
  // the laps list is the only one of the two that can actually scroll (laps are not capped the way
  // records are); the records list is height-capped to its own content so its row never sits behind
  // a scrollbar. Kept as design constants (not read from SystemInformation at call time) so
  // RequiredClientWidth stays a pure function of its dpi parameter and is testable across DPIs the
  // test host isn't actually running.
  private const int DesignRowTextInset = Palette.SpacingSm * 2;
  private const int DesignScrollBarWidth = 17; // SystemInformation.VerticalScrollBarWidth at 96dpi
  private const int DesignCardPadding = Palette.SpacingLg * 2;
  private const int DesignCellMargin = 6; // WinForms' default Control.Margin is 3px/side
  private const int DesignRootPadding = Palette.SpacingMd * 2;

  private readonly Database _database;
  private readonly StopwatchControl _stopwatchControl;
  private readonly RecordsListControl _recordsListControl;
  private readonly TrayIconService _trayIconService;
  private readonly TableLayoutPanel _rootLayout;
  private readonly Font _bodyFont;
  private readonly Icon _appIcon;
  private readonly int _activateMessage;
  private ManageRecordsForm? _manageRecordsForm;

  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  /// <param name="autosaveIntervalMinutes">The validated running-time checkpoint interval, in minutes.</param>
  public MainForm(int autosaveIntervalMinutes)
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
    Font = _bodyFont;
    _appIcon = LoadAppIcon();
    Icon = _appIcon;
    // Manual, not CenterScreen: the window is sized from its own content below before it is first
    // centered, so the synchronous framework-driven CenterScreen (which needs a size before the
    // content exists) would center the wrong size.
    StartPosition = FormStartPosition.Manual;

    _database = new Database(Database.DefaultDatabasePath);
    _stopwatchControl = new StopwatchControl(
      _database,
      TimeProvider.System,
      autosaveIntervalMinutes
    )
    {
      Dock = DockStyle.Fill,
    };
    _recordsListControl = new RecordsListControl();
    _trayIconService = new TrayIconService(_stopwatchControl, RestoreWindow, ExitApplication);
    _activateMessage = (int)Program.RegisterWindowMessage(Program.ActivateMessageName);

    // S15 (AGENTS.md §17) — two rows now, not three: the version footer row is gone (the version
    // moved into the title bar, WindowTitle above). Bottom Padding is 0, not SpacingMd — together
    // with RecordsListControl's own bottom-0 card Padding, its 1px OnPaint border, and this cell's
    // default 3px margin, that is what leaves the ~6px gap the owner asked for between the last
    // record row and the window's bottom edge, instead of the old footer's dead space.
    _rootLayout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 2,
      Padding = new Padding(Palette.SpacingMd, Palette.SpacingMd, Palette.SpacingMd, 0),
    };
    // S14 (AGENTS.md §17) — without an explicit ColumnStyle, a single-column TableLayoutPanel falls
    // back to an implicit AutoSize column that only happens to span the window's width; pinning it
    // to 100% makes that span structural instead of incidental.
    _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    // S14b (AGENTS.md §17) — AutoSize, not Percent(100): RecordsListControl reports a real content
    // height (S15: now the *actual* shown row count, not a worst-case constant), so it no longer
    // needs to stretch and fill whatever's left of the window.
    _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    _rootLayout.Controls.Add(_stopwatchControl, 0, 0);
    _rootLayout.Controls.Add(_recordsListControl, 0, 1);
    Controls.Add(_rootLayout);

    // S15 (AGENTS.md §17) — RefreshLaps itself calls ResizeToContent after every update (below), so
    // subscribing it to StateChanged is also what re-fits the window when only the "Resumed from a
    // pause" note's visibility changes (e.g. after RestoreAsync) without the laps list itself
    // changing — no separate StateChanged subscription is needed for that.
    _stopwatchControl.Timer.RecordsChanged += RefreshRecords;
    _stopwatchControl.StateChanged += RefreshLaps;
    _stopwatchControl.StateChanged += RefreshTray;
    _stopwatchControl.Tick += RefreshTray;
    _stopwatchControl.Tick += ResizeForElapsedTime;
    _recordsListControl.ClearAllRequested += ClearRecordsAsync;
    _recordsListControl.ManageRecordsRequested += OpenManageRecords;
    Load += InitializeAsync;

    // S15 (AGENTS.md §17) — sized from the actual (idle, empty-records) control tree just built
    // above; ResizeToContent itself centers on that final size (below) — replaces S14's fixed
    // design-pixel literal. DeviceDpi is a reasonable value even before the handle exists (the
    // system DPI); OnDpiChanged re-derives this once the window is actually placed on a specific
    // monitor.
    ResizeToContent(DeviceDpi);

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
    // S15 (AGENTS.md §17) — a fallback, not the primary mechanism: WndProc's WM_SYSCOMMAND/
    // SC_MINIMIZE interception above stops the window ever entering Minimized via its own title-bar
    // button, but a path that bypasses WM_SYSCOMMAND entirely (e.g. a shell-driven minimize) would
    // still land here with WindowState already Minimized — hide to tray rather than let it sit
    // minimized with a taskbar button.
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

    // S15 (AGENTS.md §10.3/§17) — intercept the minimize system command itself, rather than reacting
    // to OnResize after the fact: swallowing WM_SYSCOMMAND/SC_MINIMIZE here (not calling
    // base.WndProc) means the window never actually enters FormWindowState.Minimized, so Windows
    // never gives it a minimized taskbar button to leave behind in the first place. WParam's low
    // nibble carries flags Windows itself may set; masking them off is the documented way to compare
    // a WM_SYSCOMMAND WParam against an SC_* constant.
    const int WM_SYSCOMMAND = 0x0112;
    const int SC_MINIMIZE = 0xF020;
    if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MINIMIZE)
    {
      HideToTray();
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
    // S14/S15 (AGENTS.md §17) — re-size on every DPI change, not just at startup: moving this
    // fixed-width, non-resizable window to a higher-DPI monitor must not leave it wider than that
    // monitor's working area, since the user has no resize handle to shrink it back with.
    // S14a: re-derive from the *new* DPI (e.DeviceDpiNew), not the old size — the previous version
    // reset ClientSize to raw design pixels here, undoing whatever PerformAutoScale had just
    // correctly done for the new monitor.
    ResizeToContent(e.DeviceDpiNew);
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
      _appIcon.Dispose();
    }

    base.Dispose(disposing);
  }

  private void HideToTray()
  {
    // S15 (AGENTS.md §10.3/§17) — WindowState is normalized *before* Hide(), not after: this window
    // must never sit hidden while Minimized, or the next RestoreWindow() call brings it back still
    // minimized (the bug the repo owner reported — closing while minimized reopened minimized).
    // WM_SYSCOMMAND/SC_MINIMIZE is intercepted in WndProc before the window ever becomes Minimized
    // through its own title-bar button, so this mainly guards the OnResize fallback path above.
    WindowState = FormWindowState.Normal;
    _manageRecordsForm?.Close();
    Hide();
    ShowInTaskbar = false;
  }

  private void RestoreWindow()
  {
    // S15 (AGENTS.md §17) — ShowInTaskbar is set back to true *before* Show(), not after: flipping
    // ShowInTaskbar recreates the window handle, and doing that while the form is still hidden
    // avoids any chance of the handle recreation observing (and reinstating) a stale minimized
    // state. WindowState is already Normal from HideToTray's own normalization above, but is
    // reasserted here too since RestoreWindow is also reached from paths that never called
    // HideToTray first (first launch, single-instance activation).
    WindowState = FormWindowState.Normal;
    ShowInTaskbar = true;
    // S14b/S16 (AGENTS.md §10.6/§17) — always centers; a tray Open/single left-click, single-instance
    // activation, or restore-from-minimize all route through here.
    PositionWindowCentered();
    Show();
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
  /// Resizes the window to fit its actual current content at <paramref name="deviceDpi"/> (S15,
  /// AGENTS.md §17) — replaces S14's fixed design-pixel <c>FixedClientSize</c> literal. Width comes
  /// from <see cref="RequiredClientWidth"/> using the currently visible rows; height comes from asking <see cref="_rootLayout"/> for its
  /// real preferred size at that width, so the window always ends a few pixels below whatever is
  /// actually shown (idle vs. running, 0 vs. 5 records, laps present or not) instead of a constant
  /// sized for the worst case of everything visible at once. Call after any change that can affect
  /// the control tree's preferred size — <see cref="RefreshRecords"/>, <see cref="RefreshLaps"/>, a
  /// <see cref="StopwatchControl.StateChanged"/> (the resumed-pause note toggling), and
  /// <see cref="OnDpiChanged"/> — as well as once at construction.
  /// </summary>
  /// <param name="deviceDpi">The device DPI to size for — <see cref="Control.DeviceDpi"/> in the
  /// common case, or <see cref="DpiChangedEventArgs.DeviceDpiNew"/> from <see cref="OnDpiChanged"/>.</param>
  private void ResizeToContent(int deviceDpi)
  {
    int width = ComputeFixedWidth(deviceDpi);
    // Ask the real control tree for its preferred height at this width, with no height constraint
    // (0) — the same "measure the actual fixed-up tree" technique AGENTS.md §17's S14b/S14c entries
    // used via a throwaway harness, now run live on every content change instead of hand-derived
    // once.
    int height = _rootLayout.GetPreferredSize(new Size(width, 0)).Height;
    Size clamped = ClampToWorkingArea(new Size(width, height));
    if (ClientSize != clamped)
    {
      ClientSize = clamped;
    }
    // S15 (AGENTS.md §10.6/§17) — re-center on every content-driven resize, not just the explicit
    // Open transitions: keeping Location fixed while Size changes grows/shrinks the window from its
    // top-left corner, so once real content loads asynchronously (RestoreAsync populating records/
    // laps after the constructor's own initial, empty-state centering) the window visibly drifts
    // off-center both horizontally and vertically. Centering here — the same "always centered, never
    // remembers a position" rule §10.6 already applies to Open transitions — keeps every resize
    // centered too, and as a side effect keeps the window on-screen without a separate clamp: a size
    // already clamped to the working area, centered on that same working area, is always fully
    // visible.
    PositionWindowCentered();
  }

  /// <summary>Computes the DPI-scaled fixed window width for <paramref name="deviceDpi"/> (S15,
  /// AGENTS.md §17) — <see cref="RequiredClientWidth"/> at that DPI, using a throwaway probe font
  /// the way <see cref="OnDpiChanged"/> and the constructor both need without duplicating the
  /// font-creation call at each site.</summary>
  private int ComputeFixedWidth(int deviceDpi)
  {
    using Font monoProbeFont = Typography.CreateMonospaceBodyFont();
    int contentWidth = RequiredClientWidth(
      monoProbeFont,
      deviceDpi,
      _stopwatchControl.Timer.Records,
      _stopwatchControl.Timer.Laps,
      _stopwatchControl.Timer.ElapsedMs
    );
    int compactWidth = (int)Math.Ceiling(CompactClientWidth * (deviceDpi / 96f));
    return Math.Min(contentWidth, compactWidth);
  }

  /// <summary>
  /// Computes the minimum client width for three independent needs, at <paramref name="deviceDpi"/>,
  /// and returns whichever is larger: the widest currently visible row, the stacked records action
  /// row, or the elapsed display. Rows beyond this current-content width word-wrap rather than
  /// permanently reserving unused space. A <see cref="Font"/>'s point size is
  /// otherwise measured against a fixed 96dpi baseline regardless of the caller's actual DPI context
  /// (S14a, AGENTS.md §17) — the very mismatch that let record rows clip at anything above 100%
  /// scaling — so every font used here is rebuilt at an equivalent, pre-scaled size before measuring
  /// rather than measured as-is. The laps row (capped at a realistic 3-digit lap id) also reserves
  /// scrollbar chrome the records row does not (S15, AGENTS.md §17): the laps list is the only one of
  /// the two that can genuinely scroll.
  /// </summary>
  /// <param name="monoBodyFont">The unscaled monospace body font (<see cref="Typography.CreateMonospaceBodyFont"/>).</param>
  /// <param name="deviceDpi">The device DPI to measure for.</param>
  /// <param name="records">The records currently visible in the main window.</param>
  /// <param name="laps">The laps currently visible in the main window.</param>
  /// <param name="elapsedMs">The elapsed stopwatch time currently shown in the display.</param>
  /// <returns>The minimum client width, in device pixels at <paramref name="deviceDpi"/>, that fits
  /// both the widest row and the header row without clipping.</returns>
  internal static int RequiredClientWidth(
    Font monoBodyFont,
    int deviceDpi,
    IReadOnlyList<StopwatchRecord> records,
    IReadOnlyList<Lap> laps,
    long elapsedMs
  )
  {
    float scale = deviceDpi / 96f;

    using Font scaledMonoFont = new(
      monoBodyFont.FontFamily,
      monoBodyFont.Size * scale,
      monoBodyFont.Style
    );
    int recordRowWidth = records
      .Take(5)
      .Select(record => MeasureRowWidth(RecordsListControl.FormatRecordRow(record), scaledMonoFont))
      .DefaultIfEmpty(0)
      .Max();
    int lapRowWidth = laps.Take(3)
      .Select(lap => MeasureRowWidth(RecordsListControl.FormatLapRow(lap), scaledMonoFont))
      .DefaultIfEmpty(0)
      .Max();
    if (laps.Count > 3)
    {
      lapRowWidth += (int)Math.Ceiling(DesignScrollBarWidth * scale);
    }

    int designChrome =
      DesignRowTextInset + DesignCardPadding + DesignCellMargin + DesignRootPadding;
    int scaledChrome = (int)Math.Ceiling(designChrome * scale);
    int rowWidth = Math.Max(recordRowWidth, lapRowWidth) + scaledChrome;

    int contentWidth = Math.Max(
      Math.Max(rowWidth, HeaderActionsWidth(scale)),
      DisplayWidth(scale, TimeFormat.FormatTime(elapsedMs))
    );
    return contentWidth;
  }

  private static int DisplayWidth(float scale, string elapsedText)
  {
    using Font unscaledDisplayFont = Typography.CreateDisplayFont();
    using Font scaledDisplayFont = new(
      unscaledDisplayFont.FontFamily,
      unscaledDisplayFont.Size * scale,
      unscaledDisplayFont.Style
    );
    int textWidth = MeasureRowWidth(elapsedText, scaledDisplayFont);
    int chrome = DesignCardPadding + DesignCellMargin + DesignRootPadding;
    return textWidth + (int)Math.Ceiling(chrome * scale);
  }

  private static int MeasureRowWidth(string row, Font scaledFont) =>
    TextRenderer
      .MeasureText(
        row,
        scaledFont,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width;

  /// <summary>
  /// Computes the minimum client width for the stacked records action row at <paramref name="scale"/>.
  /// </summary>
  private static int HeaderActionsWidth(float scale)
  {
    using Font unscaledBodyFont = Typography.CreateBodyFont();
    using Font scaledBodyFont = new(
      unscaledBodyFont.FontFamily,
      unscaledBodyFont.Size * scale,
      unscaledBodyFont.Style
    );

    int TextWidth(string text) =>
      TextRenderer
        .MeasureText(
          text,
          scaledBodyFont,
          Size.Empty,
          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        )
        .Width;
    // A text-only GlyphButton's own preferred width is Padding.Left + textWidth + Padding.Right,
    // with no glyph square or gutter (GlyphButton.GetPreferredSize); its Padding.Left/Right are
    // both Palette.SpacingMd (GlyphButton's constructor).
    int buttonPadding = (int)Math.Ceiling(Palette.SpacingMd * 2 * scale);
    int manageButtonWidth = buttonPadding + TextWidth("Manage Records");
    int clearButtonWidth = buttonPadding + TextWidth("Clear All Records");
    // The gap between the two buttons in their FlowLayoutPanel is Manage Records' own right
    // Margin, Palette.SpacingSm (RecordsListControl's header row construction).
    int interButtonGap = (int)Math.Ceiling(Palette.SpacingSm * scale);
    int headerRowContentWidth = manageButtonWidth + interButtonGap + clearButtonWidth;

    // Same card/cell/root chrome as the mono row's budget, minus the row-specific text inset and
    // scrollbar width — this row isn't drawn by RecordsListControl.DrawRow and never scrolls.
    int headerRowChrome = DesignCardPadding + DesignCellMargin + DesignRootPadding;
    int scaledHeaderRowChrome = (int)Math.Ceiling(headerRowChrome * scale);

    return headerRowContentWidth + scaledHeaderRowChrome;
  }

  private void ResizeForElapsedTime()
  {
    if (ComputeFixedWidth(DeviceDpi) != ClientSize.Width)
    {
      ResizeToContent(DeviceDpi);
    }
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
  /// Sets <see cref="Form.Location"/> to center the window (both axes) on the primary screen's
  /// working area (S14b, revised S15, AGENTS.md §10.6/§17) — called from <see cref="ResizeToContent"/>
  /// on every content-driven resize (so the window stays centered as it grows/shrinks with content,
  /// not just on the "Open" transitions below) and separately from every tray Open/single click,
  /// single-instance activation, or restore-from-minimize via <see cref="RestoreWindow"/> (redundant
  /// with the resize that already happened via a live update, but cheap and keeps each Open path
  /// correct independently). The window never remembers or restores a previous position.
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
  /// (whose setters already trigger their own repaint) and <see cref="TrayIconService.DarkMode"/>.
  /// Called once at startup and again on every live OS theme change via
  /// <see cref="OnUserPreferenceChanged"/>.
  /// </summary>
  private void ApplyDarkMode()
  {
    bool dark = Application.IsDarkModeEnabled;
    _stopwatchControl.DarkMode = dark;
    _recordsListControl.Dark = dark;
    if (_manageRecordsForm is not null && !_manageRecordsForm.IsDisposed)
    {
      _manageRecordsForm.DarkMode = dark;
    }
    _trayIconService.DarkMode = dark;
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

  private void OpenManageRecords()
  {
    if (_manageRecordsForm is not null && !_manageRecordsForm.IsDisposed)
    {
      _manageRecordsForm.Activate();
      return;
    }

    ManageRecordsForm form = new(
      _stopwatchControl.Timer.Records,
      _stopwatchControl.Timer.DeleteRecordAsync,
      _stopwatchControl.Timer.ClearRecordsAsync,
      _appIcon
    )
    {
      DarkMode = Application.IsDarkModeEnabled,
    };
    form.FormClosed += (_, _) => _manageRecordsForm = null;
    _manageRecordsForm = form;
    form.Show(this);
  }

  private void RefreshLists()
  {
    RefreshRecords();
    RefreshLaps();
  }

  private void RefreshRecords() =>
    InvokeOnUiThread(() =>
    {
      _recordsListControl.UpdateRecords(_stopwatchControl.Timer.Records);
      if (_manageRecordsForm is not null && !_manageRecordsForm.IsDisposed)
      {
        _manageRecordsForm.UpdateRecords(_stopwatchControl.Timer.Records);
      }
      // S15 (AGENTS.md §17) — the records card's preferred height just changed (a row was added,
      // removed by Clear All, or the empty state toggled), so the window must re-fit it.
      ResizeToContent(DeviceDpi);
    });

  private void RefreshLaps() =>
    InvokeOnUiThread(() =>
    {
      _recordsListControl.UpdateLaps(_stopwatchControl.Timer.Laps);
      // S15 (AGENTS.md §17) — the laps panel appearing/disappearing, or growing/shrinking up to its
      // 3-row cap, changes the records card's preferred height.
      ResizeToContent(DeviceDpi);
    });

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
