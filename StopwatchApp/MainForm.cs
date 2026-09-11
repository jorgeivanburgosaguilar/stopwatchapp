using Microsoft.Data.Sqlite;
using StopwatchApp.Controls;
using StopwatchApp.Services;

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

  private readonly Database _database;
  private readonly StopwatchControl _stopwatchControl;
  private readonly RecordsListControl _recordsListControl;
  private readonly TrayIconService _trayIconService;
  private readonly int _activateMessage;

  // S11b (AGENTS.md §10.6) — the position the window was last shown at, reset every time
  // PositionWindowCentered/PositionWindowAsync places the window. Compared against the live
  // Location on hide-to-tray/exit to detect a user-initiated drag.
  private Point _shownAtLocation;

  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  public MainForm()
  {
    Text = WindowTitle;
    ClientSize = new Size(560, 600);
    MinimumSize = new Size(400, 400);
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

    TableLayoutPanel layout = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 2,
      Padding = new Padding(12),
    };
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    layout.Controls.Add(_stopwatchControl, 0, 0);
    layout.Controls.Add(_recordsListControl, 0, 1);
    Controls.Add(layout);

    _stopwatchControl.Timer.RecordsChanged += RefreshRecords;
    _stopwatchControl.StateChanged += RefreshLaps;
    _stopwatchControl.StateChanged += RefreshTray;
    _stopwatchControl.Tick += RefreshTray;
    _recordsListControl.ClearAllRequested += ClearRecordsAsync;
    Load += InitializeAsync;
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

  private void HideToTray()
  {
    Hide();
    ShowInTaskbar = false;
    // Fire-and-forget: Database.SaveWindowPositionAsync swallows its own errors (AGENTS.md §9), so
    // there's nothing for this hide-to-tray path (itself synchronous, per AGENTS.md §10.3) to await
    // or catch. Discarding to `_` deliberately, not leaving the call unobserved (CS4014).
    _ = SaveWindowPositionIfChangedAsync();
  }

  private async void RestoreWindow()
  {
    // WindowState is reset to Normal *before* positioning: Location reads/writes while
    // WindowState is Minimized are unreliable (Windows tracks a minimized window's on-screen rect
    // separately from its "restore" position), so PositionWindowAsync must run only once the form
    // is guaranteed Normal. Safe on a hidden form — this just updates placement, nothing is drawn
    // until Show() below.
    WindowState = FormWindowState.Normal;
    // Position before showing, per AGENTS.md §10.6's "Open" transitions — a tray Open/double-click,
    // single-instance activation, or restore-from-minimize all route through here.
    await PositionWindowAsync();
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
    await SaveWindowPositionIfChangedAsync();
    await _database.DisposeAsync();
    Application.Exit();
  }

  /// <summary>
  /// Sets <see cref="Form.Location"/> to the centered default (S11b, AGENTS.md §10.6) — used both
  /// as the constructor's synchronous default (before the database is ready) and as
  /// <see cref="PositionWindowAsync"/>'s fallback when no valid saved position exists.
  /// </summary>
  private void PositionWindowCentered()
  {
    Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
    Rectangle workingArea = primary.WorkingArea;
    Location = new Point(
      workingArea.X + (workingArea.Width - Width) / 2,
      workingArea.Y + (workingArea.Height - Height) / 2
    );
    _shownAtLocation = Location;
  }

  /// <summary>
  /// Positions the window for an "Open" transition (AGENTS.md §10.6): a saved position is used
  /// only if its bounds intersect at least one currently-connected screen's working area; otherwise
  /// (including when there is no saved position at all) the window is centered.
  /// </summary>
  private async Task PositionWindowAsync()
  {
    (int X, int Y)? saved = await _database.LoadWindowPositionAsync();
    if (saved is { } position)
    {
      Rectangle bounds = new(position.X, position.Y, Width, Height);
      if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
      {
        Location = new Point(position.X, position.Y);
        _shownAtLocation = Location;
        return;
      }
    }

    PositionWindowCentered();
  }

  /// <summary>
  /// The window's location as it should be read for S11b persistence purposes: <see cref="Form.Location"/>
  /// directly while <see cref="Form.WindowState"/> is <see cref="FormWindowState.Normal"/>, or
  /// <see cref="Form.RestoreBounds"/>'s location otherwise. <c>Location</c> is unreliable while
  /// minimized (Windows tracks a minimized window's actual on-screen rect separately from its
  /// "restore" position, so it does not reflect where the window was before it was minimized) —
  /// relevant here because <see cref="OnResize"/> calls <see cref="HideToTray"/> (and therefore this
  /// save check) with <see cref="Form.WindowState"/> already <see cref="FormWindowState.Minimized"/>,
  /// and <see cref="ExitApplication"/> can run while the window is still in that state if it was
  /// minimized-to-tray and never reopened before Exit.
  /// </summary>
  private Point CurrentPersistableLocation =>
    WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;

  /// <summary>
  /// Persists the window's current location only if it differs from <see cref="_shownAtLocation"/>
  /// — i.e. only if the user dragged the window since it was last positioned (AGENTS.md §10.6). An
  /// app that's never been dragged never writes to <c>window_position</c>.
  /// </summary>
  private async Task SaveWindowPositionIfChangedAsync()
  {
    Point current = CurrentPersistableLocation;
    if (current != _shownAtLocation)
    {
      await _database.SaveWindowPositionAsync(current.X, current.Y);
    }
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

    // S11b (AGENTS.md §10.6) — first launch's "Open" transition; overrides the constructor's
    // synchronous centered default if a valid saved position exists.
    await PositionWindowAsync();

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
