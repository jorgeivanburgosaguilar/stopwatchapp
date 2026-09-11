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

  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  public MainForm()
  {
    Text = WindowTitle;
    ClientSize = new Size(560, 600);
    MinimumSize = new Size(400, 400);
    StartPosition = FormStartPosition.CenterScreen;

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
  }

  private void RestoreWindow()
  {
    Show();
    ShowInTaskbar = true;
    WindowState = FormWindowState.Normal;
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
